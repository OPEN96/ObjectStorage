# 更新日志

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Planned

- 为主流客户端提供可选的 CORS 配置（浏览器直传）
- 存储桶级配额与限速
- 可选的 PostgreSQL 元数据实现，支持多实例
- 桶策略 / ACL 的最小实现

## [0.2.0] - 2026-11-06

生产化硬化版本。在 6 核 / 1 vCPU 等效配额的真实 Docker 环境里做了功能、性能、容量、
故障注入、安全对抗、密钥轮换、备份恢复与 10 分钟长稳测试，修复了下列问题。

### Fixed

- **写性能被 fsync 支配**：`PRAGMA synchronous` / `busy_timeout` 是连接级设置，
  原先只在启动时执行一次，连接池后续新建的连接仍是 `synchronous=FULL`，导致每次 `SaveChanges` 都落盘。
  新增 `SqlitePragmaInterceptor` 在每次连接打开时补齐 PRAGMA。
  实测 4 KB PUT：并发 8 从 14 rps → 82 rps，并发 32 从 20 rps → 156 rps；每次 PUT 的 fsync 从 5.25 次降到 0.78 次。
- **并发覆写泄漏对象体**：8 并发覆写 200 次遗留 121 个孤儿 blob，32 并发 400 次遗留 316 个（单线程仅 1 个）。
  新增 `OrphanBlobSweeper` / `OrphanBlobSweepWorker`，按「元数据差集 + 宽限期」回收；
  实测把 10150 个 blob 降到 12 个、释放 1.45 GB，在用对象内容校验一致。
- **畸形 XML 拖死请求**：150 KB–1.2 MB 的深度嵌套 XML 会让请求挂起约 30 秒（CPU 放大）。
  在反序列化前扫描标签层级，超过 64 层直接返回 `MalformedXML`；10 万层嵌套现在 2 秒内被拒。
- **自定义元数据无上限**：20 KB 的 `x-amz-meta-*` 头会被接受并落库。限制为 2 KiB，
  超限返回 `MetadataTooLarge`。
- **桶名规范**：`UPPER`、`double..dot` 曾被接受。现在拒绝大写字母与连续 `..`（仍允许 `.`、`-`，长度 3–63）。
- **管理端会话无法撤销**：删除 / 禁用管理员访问密钥后，已签发的 Cookie 仍可用满 12 小时。
  会话现在绑定签发时的 Secret 指纹，并在每次请求回查密钥状态；禁用、删除、轮换 Secret 都会立即登出。
- **无法轮换 Secret**：`PATCH /admin/api/access-keys/{id}` 原先只接受 `enabled` / `displayName`，
  轮换只能「删除 + 新建」，而桶属主是 `AccessKeyId`，新建密钥后原桶会立刻不可见。现在支持原地轮换。
- **schema 升级无保护**：原先只有 `EnsureCreated`，旧代码可以打开新库、新代码也可能静默跑在旧结构上。
  新增 `SchemaGuard`，用 `PRAGMA user_version` 记录版本：低版本按序升级，高版本拒绝启动。

### Added

- 管理端「访问密钥 → 轮换」按钮，前端用 `crypto.getRandomValues` 生成 48 位随机 Secret。
- 存储配置：`OrphanSweepEnabled`、`OrphanSweepIntervalHours`、`OrphanSweepInitialDelaySeconds`、
  `OrphanGraceHours`、`OrphanSweepMaxObjects`。
- 测试脚本：`bench.py`（并发基准，含 P50/P90/P95/P99）、`loadgen.c`（C 压测器，keep-alive + 分位延迟）、
  `capacity_test.py`（200 MB 分片 + SHA256 全量校验）、`security_probe.py`（24 项对抗）、
  `key_permission_test.py`（24 项密钥生命周期与租户隔离）、`snapshot.py`（实例快照比对）、
  `check_refs.py`（元数据引用 ↔ 磁盘文件一致性）、`soak.sh`（长稳压测 + 资源采样）、
  `run_container.sh`（硬化参数启动容器）。
- 文档：`docs/deployment.md` 补充热备一致性的实测结论与 schema 版本语义；
  `docs/configuration.md` 补充孤儿回收、会话失效语义与密钥轮换步骤。
- GitHub Actions 发布流水线 `.github/workflows/release.yml`：标签推送或手动触发，
  产出 linux-x64 / linux-arm64 / win-x64 自包含单文件包（tar.gz / zip），
  构建 linux/amd64 + linux/arm64 多架构镜像推送 GHCR，并在标签发布时创建 GitHub Release（附 `SHA256SUMS.txt`）。
- `Dockerfile` 支持 `--build-arg VERSION=` 注入程序集版本。

### Changed

- 测试规模：75 → 85 项 xUnit（新增 schema 守卫 3 项、密钥轮换与会话撤销 3 项、孤儿回收 2 项、XML 深度 2 项）。
- `InvalidPartNumber` 错误码由非标准的 `ArgumentOutOfRange` 改为 AWS 标准 `InvalidArgument`；
  移除未使用的 `NotImplemented1` 常量，合并重复的 XML 文档注释。

## [0.1.0] - 2026-11-05

首个版本：AWS S3 兼容对象存储服务端 + 网页管理端 + 容器化部署。

### Added

- S3 数据面：ListBuckets / CreateBucket / DeleteBucket / HeadBucket / GetBucketLocation /
  ListObjectsV1+V2（prefix、delimiter、continuation-token、start-after、max-keys、encoding-type=url、fetch-owner）/
  批量删除。
- 对象操作：Put / Get / Head / Delete / CopyObject，Range 下载、`response-*` 覆盖头、
  条件请求（If-Match / If-None-Match / If-Modified-Since / If-Unmodified-Since）、
  `x-amz-meta-*` 自定义元数据。
- 分片上传：Initiate / UploadPart / Complete / Abort / ListParts / ListMultipartUploads。
- SigV4：Authorization 头与预签名 URL、`UNSIGNED-PAYLOAD`、
  `STREAMING-AWS4-HMAC-SHA256-PAYLOAD`（含 `-TRAILER`）、
  `STREAMING-UNSIGNED-PAYLOAD-TRAILER`（minio-go / mc 非 TLS 场景）。
- 网页管理端 `/admin`：概览、存储桶、对象浏览器（拖拽上传/批量删除）、分片任务、访问密钥。
- 管理端认证：管理员令牌或 `IsAdmin` 访问密钥；HMAC 签名 HttpOnly Cookie；
  也支持 `Authorization: Bearer` / `X-Admin-Token`。
- 容器化：多阶段 Dockerfile、docker compose、非 root 用户、健康检查、数据卷备份说明。
- 健康检查：`/minio/health/live`、`/minio/health/ready`（探测元数据库）。
- 反向代理支持：`ReverseProxy:Enabled` / `KnownProxies` / `ForwardLimit`，默认关闭；
  只有名单内的代理来源才采纳 `X-Forwarded-*`，代理不在名单时登录节流与 Cookie 安全性会退化，已在文档中标明。
- 测试与验证：75 项 xUnit 测试；`scripts/smoke.py`（18 项）、
  `scripts/minio_compat.py`（20 项）、`scripts/mc_compat.sh`（12 项）。

### Security

- 分片上传的 `uploadId` 现在必须属于当前桶与对象 key，修复跨租户续传/完成/终止的风险。
- `UploadPart` 的 `Content-MD5` 校验失败时只回滚当前分片，不再误删同一上传的其它分片。
- XML 请求体限制 2 MiB（批量删除 1 MiB），避免无界内存分配。
- 管理端新增：CSP / `X-Frame-Options` / `nosniff` / `Referrer-Policy` 等安全响应头；
  Origin 与 `Sec-Fetch-Site` 跨站校验；登录失败节流（10 次 / 5 分钟 / IP）；JSON 接口 1 MiB 体积上限。
- 生产环境启动校验：缺少管理员令牌、使用示例/过短密钥、首次启动未配置初始密钥时拒绝启动。
- 默认凭证从 `appsettings.json` 移除，仅保留在 `appsettings.Development.json`。
- 对象 body 路径解析增加目录边界校验；Kestrel 不再发送 `Server` 头。

### Fixed

- HEAD / Range 响应不再被空结果覆盖状态码与 `Content-Length`。
- 管理端「分片任务」此前始终显示 0 个分片，现正确统计已传分片数与字节数。
- `CopyObject` 在 `COPY` 模式下保留 Content-Type、Content-Encoding、Cache-Control、
  Content-Disposition、Content-Language、Expires 与自定义 `x-amz-*` 头。
- 同一 key 并发写入触发唯一索引冲突时自动重试一次，避免偶发 500。
- 未配置初始密钥时，开发环境会把生成的 Secret 打印到日志（此前只打印 AccessKeyId，无法登录）。
- 管理端错误响应不再清空已设置的安全响应头与 `Retry-After`：此前 429/401 等错误会丢失
  节流提示和 CSP / `nosniff` 等防护头。
- **容器镜像此前无法构建**：`.dockerignore` 排除了 `.editorconfig`，容器内 `dotnet publish`
  丢失分析器抑制规则，在 `TreatWarningsAsErrors` 下直接失败（18×CA1848 + 8×CA1873 + CA1305）。
  现已在 Dockerfile 中显式复制该文件，并在 `.dockerignore` 注明它是构建输入。
- 匿名请求（无 `Authorization`、无预签名参数）由 400 `MissingSecurityHeader` 改为
  S3 规范的 403 `AccessDenied`，避免客户端把"未认证"误判为"请求格式错误"。
- 分片文件名改用不变文化格式化（`partNumber.ToString("D5", InvariantCulture)`），
  避免在非拉丁数字区域设置下写出异常文件名。
- `scripts/mc_compat.sh` 改用 `mc --config-dir`（Windows 版 mc 不认 `MC_CONFIG_DIR`），
  并在 `alias set` 未生效时直接中止——此前 mc 会退化为本地文件系统模式，
  11 项里 9 项"通过"却完全没测到服务端。

### Changed

- 依赖版本集中到 `Directory.Packages.props`；启用可空引用、分析器与警告即错误。
- 移除运行时用不到的 `Microsoft.EntityFrameworkCore.Design` 引用：EF 的 `BuildHost-*`
  不再被复制进发布输出，容器镜像因此少约 4 MB（引入 migrations 时再加回）。
- 新增 `.editorconfig`、`.gitattributes`、`global.json`、`Directory.Build.props`。
- 版本号统一由 `Directory.Build.props` 的 `<Version>` 决定：管理端「概览」此前固定显示
  `1.0.0`（程序集默认版本），与发布号不一致；镜像里也清掉了基础镜像预置的
  `ASPNETCORE_HTTP_PORTS=8080`，启动不再打印 Kestrel 覆盖端口的告警。
- README 重写，补充配置、部署、客户端接入、兼容性与限制说明。

[Unreleased]: https://github.com/OPEN96/ObjectStorage/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/OPEN96/ObjectStorage/releases/tag/v0.2.0
[0.1.0]: https://github.com/OPEN96/ObjectStorage/releases/tag/v0.1.0
