# syntax=docker/dockerfile:1.7
# ============================================================
# S3 对象存储 —— 多阶段构建
#   构建阶段：.NET 10 SDK 编译发布
#   运行阶段：ASP.NET Core 运行时，非 root 用户，数据落在 /data
# ============================================================

# ------------------------------ 构建 ------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只复制工程文件与集中式依赖清单，让 restore 层可以被缓存：只改代码时不必重新拉包。
# .editorconfig 必须一起复制：它承载分析器抑制规则，缺失会让 TreatWarningsAsErrors 直接失败。
COPY S3ObjectStorage.slnx Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/S3ObjectStorage.Domain/S3ObjectStorage.Domain.csproj src/S3ObjectStorage.Domain/
COPY src/S3ObjectStorage.Infrastructure/S3ObjectStorage.Infrastructure.csproj src/S3ObjectStorage.Infrastructure/
COPY src/S3ObjectStorage.S3Protocol/S3ObjectStorage.S3Protocol.csproj src/S3ObjectStorage.S3Protocol/
COPY src/S3ObjectStorage.Api/S3ObjectStorage.Api.csproj src/S3ObjectStorage.Api/
RUN dotnet restore src/S3ObjectStorage.Api/S3ObjectStorage.Api.csproj

COPY src/ src/

# 发布流水线可传 --build-arg VERSION=x.y.z 覆盖程序集版本（管理端「概览」展示它）；
# 留空则回落到 Directory.Build.props 中的 Version。
ARG VERSION=
RUN VERSION_ARGS="" \
    && if [ -n "$VERSION" ]; then VERSION_ARGS="-p:Version=$VERSION"; fi \
    && dotnet publish src/S3ObjectStorage.Api/S3ObjectStorage.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        -p:UseAppHost=false \
        -p:ContinuousIntegrationBuild=true \
        $VERSION_ARGS

# ------------------------------ 运行 ------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

LABEL org.opencontainers.image.title="S3ObjectStorage" \
      org.opencontainers.image.description="S3-compatible object storage server built on .NET 10" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app

# 容器内固定监听 9000，数据目录固定为 /data（可用环境变量覆盖）。
# ASPNETCORE_ENVIRONMENT=Production：缺少管理员令牌/初始密钥时会拒绝启动，而不是用开发默认值上线。
ENV Storage__RootPath=/data \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:9000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_NOLOGO=1

# 基础镜像预置了 ASPNETCORE_HTTP_PORTS=8080，与上面的 Kestrel 端点二选一；
# 清空它，避免每次启动都打印 "Overriding address(es) 'http://*:8080'" 的告警。
ENV ASPNETCORE_HTTP_PORTS="" \
    ASPNETCORE_URLS=""

# 预建数据目录并交给非 root 用户；命名卷首次挂载会继承该属主。
RUN mkdir -p /data && chown -R $APP_UID /data

COPY --from=build --chown=$APP_UID /app/publish ./

USER $APP_UID
EXPOSE 9000
VOLUME ["/data"]

# 镜像自带健康检查：只需要能建立 TCP 连接（不必带签名）。
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/9000"]

ENTRYPOINT ["dotnet", "S3ObjectStorage.Api.dll"]
