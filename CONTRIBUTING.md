# 贡献指南

感谢你愿意改进 S3ObjectStorage。本文说明开发环境、代码规范与提交要求。

## 行为准则

参与本项目即表示你同意遵守 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。

## 开发环境

| 依赖 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0.100+ | `global.json` 已固定，`rollForward: latestFeature` |
| Python | 3.10+（可选） | 运行 `scripts/smoke.py`、`scripts/minio_compat.py` |
| `minio` Python 包 | 7.x（可选） | `pip install minio` |
| MinIO `mc` | 最新（可选） | 交叉验证 CLI 兼容性 |
| Docker | 24+（可选） | 构建镜像 / compose |

```bash
git clone git@github.com:OPEN96/ObjectStorage.git
cd ObjectStorage

# 一键：还原 + 构建 + 测试 + 发布产物（--help 查看参数）
bash scripts/build.sh

# 或手工分步执行
dotnet restore
dotnet build
dotnet test
```

本地运行（自动使用 `appsettings.Development.json` 中的开发凭证）：

```bash
dotnet run --project src/S3ObjectStorage.Api
# S3:     http://127.0.0.1:9000
# Admin:  http://127.0.0.1:9000/admin/  （令牌 dev-admin-token-change-me）
```

## 代码结构约定

- **Domain** 不依赖 ASP.NET Core 或 EF Core；只放实体、仓储接口与 S3 错误码。
- **Infrastructure** 实现持久化与 blob 存储；新增存储后端时实现 `IBlobStore`。
- **S3Protocol** 只关心协议：SigV4、XML、URL 编码、路由、结果封装。
- **Api** 负责装配、中间件与具体 S3 操作；`Admin` 子目录是独立的管理面。
- 不要在数据面（S3）里返回 JSON，也不要在管理面返回 S3 XML。

## 代码规范

以下规则由 `Directory.Build.props` 与 `.editorconfig` 强制，CI 会执行
`dotnet format --verify-no-changes`：

- `Nullable` 开启，`TreatWarningsAsErrors=true`，分析器级别 `latest-recommended`。
- 文件级 namespace、4 空格缩进、LF 换行。
- 公开 API 写中文 XML 注释；注释解释「为什么」，而不是复述代码。
- 异步方法带 `Async` 后缀并传递 `CancellationToken`。
- 不要吞异常；需要兜底时记录日志并说明原因。
- 新增 `NoWarn` 必须在 `.editorconfig` 或 csproj 中写明理由。

提交前：

```bash
dotnet format          # 自动修复格式与 using
dotnet build -warnaserror
dotnet test
```

## 测试要求

- 修 bug：先补一个能复现的测试。
- 新功能：至少覆盖成功路径 + 一个失败/边界路径。
- 协议相关改动：优先用 AWS SDK 或 minio-dotnet 的集成测试（`S3HostCollection` 共享宿主）。
- 手工客户端验证（可选但推荐）：

```bash
python scripts/smoke.py            # 独立 SigV4，18 项
python scripts/minio_compat.py     # minio-py，20 项
bash scripts/mc_compat.sh          # mc CLI（需安装 mc）
```

## 提交信息

采用 [Conventional Commits](https://www.conventionalcommits.org/)：

```
feat: 支持 GetBucketVersioning 占位响应
fix(multipart): 校验 uploadId 必须属于当前桶
docs: 补充反向代理部署示例
test: 覆盖 HEAD + Range 的 206 响应
chore(deps): 升级 AWSSDK.S3
```

一个提交只做一件事；不要混入无关的格式化改动。

## 分支与 PR

1. 从 `main` 切出 `feat/<topic>` 或 `fix/<topic>`。
2. PR 描述中写清：动机、行为变化、测试方式、兼容性影响。
3. 确保 CI 全绿（构建、格式、测试、镜像构建、冒烟）。
4. 涉及 S3 语义的改动，请附上客户端验证结果（aws cli / mc / SDK）。
5. 破坏性变更需要在 CHANGELOG 的 `Changed`/`Removed` 中注明。

## 新增一个 S3 操作

1. 在 `S3Dispatcher` 中按「方法 + 子资源」分发；
2. 若引入新的查询参数，更新 `SupportedSubresources` 或 `UnsupportedSubresources`；
3. 在 `S3Operations` 的分部类中实现，复用 `RequireBucketAsync` / `RequireUploadAsync`；
4. 新增/复用 `S3ErrorCode`，不要手写状态码字符串；
5. 补集成测试，并更新 `docs/api-support.md`。

## 维护者：发布检查清单

发布前依次确认：

```bash
dotnet format --verify-no-changes
dotnet build -c Release -warnaserror
dotnet test -c Release
docker build -t s3objectstorage:check .
```

- `Directory.Build.props` 的 `<Version>`、`CHANGELOG.md` 的版本标题、计划打的 tag 三者一致；
- `README.md` 与 `README.en.md` 中的版本号、测试数量与实际结果一致；
- `docs/screenshots/` 中的截图不含真实密钥或内网地址；
- `git status` 干净，无 `data/`、`tools/`、`.env`、`*_test/` 等运行期产物；
- `CHANGELOG.md` 的 `Unreleased` 已整理为对应版本号并打 tag（`vX.Y.Z`）；
- 推 tag 后确认 `release.yml` 产出多架构镜像与 release 附件。

## 许可证

提交代码即表示你同意以 [MIT](LICENSE) 许可证发布你的贡献。
