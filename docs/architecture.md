# 架构设计

## 总览

```
                   ┌──────────────────────────── Kestrel (HTTP) ────────────────────────────┐
客户端             │                                                                        │
(aws cli / mc /    │  RequestId → Exception → Metrics → [Admin 分支] → [健康检查] → SigV4 →   │
 SDK / curl) ─────▶│  S3Dispatcher → S3Operations → 仓储接口 / IBlobStore                    │
                   │                                                                        │
                   └───────────────┬──────────────────────────────┬─────────────────────────┘
                                   ▼                              ▼
                        S3ObjectStorage.S3Protocol        S3ObjectStorage.Infrastructure
                        SigV4 / aws-chunked / XML         EF Core + SQLite   FileSystemBlobStore
                        URL 编码 / 路由 / 结果封装          （元数据）          （对象体）
                                   │                              │
                                   └──────────▶ S3ObjectStorage.Domain ◀── 实体 / 接口 / 错误码
```

依赖方向严格单向：`Api → S3Protocol / Infrastructure → Domain`。
Domain 不引用 ASP.NET Core，也不引用 EF Core。

## 项目职责

| 项目 | 内容 | 关键类型 |
| --- | --- | --- |
| `Domain` | 实体、仓储抽象、S3 错误码 | `BucketEntity`、`IObjectStore`、`S3ErrorCode` |
| `Infrastructure` | SQLite 元数据、本地文件系统 blob | `StorageDbContext`、`FileSystemBlobStore`、`OrphanBlobSweeper`、`TempCleanupWorker` |
| `S3Protocol` | SigV4、aws-chunked、XML、路由、URL 编码 | `SigV4Validator`、`AwsChunkedBodyStream`、`S3Xml`、`S3Results` |
| `Api` | 主机装配、中间件、S3 操作、管理端 | `S3Dispatcher`、`S3Operations`、`AdminApi` |
| `tests` | 单元 + 真实 Kestrel 集成测试 | `S3TestHost`、`S3HostCollection` |

## 请求处理链路

1. **RequestIdMiddleware**：生成 `x-amz-request-id` / `x-amz-id-2`，便于按请求排障。
2. **S3ExceptionMiddleware**：把 `S3Exception` 转成 S3 XML 错误；管理端路径转成 JSON。
3. **MetricsMiddleware**：统计请求数、4xx/5xx、进出的声明字节数。
4. **Admin 分支**（`/admin`）：在 SigV4 之前短路，自带安全响应头、CSRF 校验、体积限制与 Cookie 认证。
5. **健康检查分支**（`/minio/health/*`）：匿名；`ready` 会探测 SQLite。
6. **S3 根路径重定向**：浏览器访问 `/` 且 `Accept: text/html` 时跳转 `/admin/`，不影响 S3 客户端。
7. **S3AuthenticationMiddleware**：调用 `SigV4Validator` 得到 `S3Principal`，写入 `HttpContext.Items`。
8. **S3Dispatcher**：按「路径 + 方法 + 子资源」分发；未实现的子资源返回 `501`。
9. **S3Operations**：校验桶归属、对象 key、条件头、Range、完整性，然后读写仓储 / blob。

## 为什么用终端处理器而不是 Controller

S3 只有一个 URL 空间：`GET /bucket?location`、`GET /bucket?uploads`、`GET /bucket/key?uploadId`
在 ASP.NET Core 路由里会互相冲突，且桶名/对象 key 都来自路径，不适合模型绑定。
因此用一个集中式 dispatcher 显式表达「方法 + 子资源」的组合，路由表保持可控。

## 存储布局

```
<Storage:RootPath>/
├── metadata.db          SQLite：桶、对象元数据、分片任务、访问密钥
├── metadata.db-wal      WAL 日志（高并发读写下减少锁竞争）
├── blobs/aa/bb/<guid>.bin   对象体，两级前缀分片，避免单目录文件过多
├── parts/<sha256(uploadId)>/<partNumber:D5>.part   分片临时文件
└── tmp/<guid>.tmp      写入中的临时文件（后台每小时清理 6 小时前的残留）
```

### 写入流程（单次 PUT）

1. 请求体（必要时经 `AwsChunkedBodyStream` 解码）写入 `tmp/`；
2. `HashingWriteStream` 边写边算 MD5 与 SHA256，不二次读盘；
3. 写入完成后 `File.Move` 到 `blobs/`（同卷内原子 rename）；
4. 校验 `x-amz-content-sha256` / `Content-MD5`；
5. 元数据 `Upsert` 到 SQLite；
6. 覆盖写时删除旧 blob，并更新桶统计。

任一步失败都不会留下「元数据指向半截文件」的状态；代价是极端情况下可能出现**孤儿 blob**
（文件已落盘但元数据写入失败）。孤儿由 `OrphanBlobSweeper` 按周期回收：它把
`Objects.BlobPath` 读入集合、枚举 `blobs/*.bin`，只删除「不在集合内且 mtime 早于
`Storage:OrphanGraceHours`」的文件。宽限期保证正在写入、尚未提交元数据的对象不会被误删，
因此孤儿会在磁盘上多停留最多一个宽限期。

### 分片上传

- `parts/<hash(uploadId)>/00001.part` 与元数据中的 `UploadedPartEntity` 一一对应；
- 重传同一 `partNumber` 覆盖文件并更新元数据；
- Complete 按请求中的顺序拼接分片到新的 blob，计算
  `MD5(concat(part_md5_bytes)) + "-N"` 作为最终 ETag；
- 拼接成功后删除分片目录与任务记录；覆盖已有对象时删除旧 blob。

## 多租户模型

- 每个桶记录 `OwnerId`（AccessKeyId）与 `OwnerDisplayName`；
- `IsAdmin=true` 的密钥可访问所有桶；
- 普通密钥只能访问自己拥有的桶，越权返回 `403 AccessDenied`；
- `ListBuckets` 只返回当前身份拥有的桶；
- 分片任务额外校验 `uploadId` 必须属于当前桶与对象 key。

这是一套**简化模型**：没有 IAM policy、桶 ACL、跨账号授权或临时凭证（STS）。

## 一致性与并发

- SQLite WAL + `busy_timeout=5000` + `synchronous=NORMAL`，适合单机中低并发写入。
- 对象写入是 last-write-wins；没有版本号与条件写（`If-None-Match: *` 的原子创建）。
- 同一 key 并发 `Upsert` 会撞唯一索引，仓储层会重试一次并转为覆盖。
- 桶统计（`ObjectCount` / `TotalSize`）是增量更新，异常时可能漂移；
  管理端展示的是实时 `COUNT/SUM`，不依赖缓存值。

## 管理端设计

- 路径前缀 `/admin`，在 SigV4 之前短路，避免被当作桶名；
- 前端为原生 HTML/CSS/JS，无构建步骤，静态资源由 `AdminDispatcher` 托管；
- 认证：管理员令牌或 `IsAdmin` 访问密钥；Cookie 为 HMAC-SHA256 签名的无状态令牌；
- 后端只调用领域接口，不直接访问 EF Core 或文件系统；
- 上传为单次 PUT（浏览器场景足够），超大文件建议走 S3 分片上传。

## 可替换点

| 扩展点 | 现状 | 替换方式 |
| --- | --- | --- |
| 对象体存储 | 本地文件系统 | 实现 `IBlobStore`，注册到 DI |
| 元数据存储 | EF Core + SQLite | 替换 `StorageDbContext` 提供程序，或重写四个仓储接口 |
| 认证 | SigV4 + 管理端 Cookie | 在 `S3AuthenticationMiddleware` 前插入自定义中间件 |
| 可观测性 | 进程内指标 + 标准日志 | 接入 OpenTelemetry（现有 `ServerMetrics` 可替换） |
