# S3 API 支持矩阵

图例：✅ 已实现 · ⚠️ 部分实现/有限制 · ❌ 未实现（返回 `501 NotImplemented`）

## 服务级

| API | 状态 | 说明 |
| --- | --- | --- |
| `GET /`（ListBuckets） | ✅ | 只返回当前身份拥有的桶 |
| `GET /?x-id=ListBuckets` | ✅ | 忽略 `x-id` 等客户端标记参数 |

## 桶级

| API | 状态 | 说明 |
| --- | --- | --- |
| `PUT /{bucket}`（CreateBucket） | ✅ | 3–63 位、小写字母/数字/`-`/`.`；支持 `CreateBucketConfiguration` |
| `DELETE /{bucket}`（DeleteBucket） | ✅ | 非空桶返回 `409 BucketNotEmpty` |
| `HEAD /{bucket}`（HeadBucket） | ✅ | 存在且有权访问返回 200 |
| `GET /{bucket}?location` | ✅ | 返回配置的 region |
| `GET /{bucket}?list-type=2`（ListObjectsV2） | ✅ | prefix / delimiter / continuation-token / start-after / max-keys / fetch-owner / encoding-type |
| `GET /{bucket}`（ListObjectsV1） | ✅ | marker / max-keys / delimiter / encoding-type |
| `GET /{bucket}?uploads`（ListMultipartUploads） | ✅ | prefix / key-marker / upload-id-marker / max-uploads |
| `POST /{bucket}?delete`（DeleteObjects） | ✅ | 最多 1000 个；支持 `Quiet`；校验 `Content-MD5` |
| `GET /{bucket}?acl` / `?policy` / `?tagging` / `?versioning` / `?cors` / `?lifecycle` / `?logging` / `?website` / `?encryption` / `?replication` / `?notification` / `?object-lock` / `?publicAccessBlock` / `?accelerate` / `?requestPayment` / `?versions` | ❌ | 统一 `501 NotImplemented` |
| `PUT /{bucket}?acl` 等写操作 | ❌ | 同上 |

## 对象级

| API | 状态 | 说明 |
| --- | --- | --- |
| `PUT /{bucket}/{key}`（PutObject） | ✅ | 支持 `x-amz-meta-*`、`Content-Type/Encoding/Disposition/Language`、`CacheControl`、`Expires`、`Content-MD5`、`x-amz-content-sha256` |
| `PUT` + `x-amz-copy-source`（CopyObject） | ✅ | 支持 `x-amz-metadata-directive: COPY / REPLACE` |
| `GET /{bucket}/{key}`（GetObject） | ✅ | Range、`response-content-type` 等覆盖头、条件请求 |
| `HEAD /{bucket}/{key}`（HeadObject） | ✅ | 返回真实 `Content-Length`；支持带 Range 的 206 |
| `DELETE /{bucket}/{key}`（DeleteObject） | ✅ | 幂等，不存在也返回 204 |
| `POST /{bucket}/{key}?uploads`（CreateMultipartUpload） | ✅ | 保存 Content-Type 与用户元数据 |
| `PUT /{bucket}/{key}?partNumber&uploadId`（UploadPart） | ✅ | 校验 `Content-MD5`；分片 1–10000 |
| `POST /{bucket}/{key}?uploadId`（CompleteMultipartUpload） | ✅ | ETag = `md5(concat(part_md5))-N`；分片必须升序 |
| `DELETE /{bucket}/{key}?uploadId`（AbortMultipartUpload） | ✅ | 需 uploadId 属于当前桶与 key |
| `GET /{bucket}/{key}?uploadId`（ListParts） | ✅ | max-parts / part-number-marker |
| `GET /{bucket}/{key}?versionId=...` | ⚠️ | 参数被接受但忽略；未实现版本控制 |
| `PUT/GET ?acl`、`?tagging`、`?retention`、`?legal-hold`、`?torrent`、`?select` | ❌ | `501 NotImplemented` |

## 鉴权与完整性

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| SigV4 `Authorization` 头 | ✅ | `AWS4-HMAC-SHA256` |
| SigV4 预签名 URL | ✅ | GET / PUT；有效期 1s–7d；校验过期 |
| `UNSIGNED-PAYLOAD` | ✅ | 预签名与非 TLS 客户端常用 |
| `STREAMING-AWS4-HMAC-SHA256-PAYLOAD` | ✅ | 分块签名链逐块校验 |
| `STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER` | ✅ | AWS SDK v4 默认 flexible checksum 场景 |
| `STREAMING-UNSIGNED-PAYLOAD-TRAILER` | ✅ | minio-go / mc 非 TLS 场景 |
| `x-amz-content-sha256` 校验 | ✅ | 可用 `Storage:VerifyPayloadHash` 关闭（不建议） |
| `Content-MD5` 校验 | ✅ | PutObject / UploadPart / DeleteObjects |
| `x-amz-checksum-*` 服务端校验 | ⚠️ | 仅消费 aws-chunked trailer，不落库、不在 GET 回显 |
| STS / IAM / ACL / 桶策略 | ❌ | 只有「管理员 / 普通密钥 / 桶 owner」模型 |

## 兼容性相关行为

- **`encoding-type=url`**：Key / Prefix / Delimiter / StartAfter / Marker / CommonPrefixes
  按 RFC 3986 编码，空格为 `%20`、`+` 为 `%2B`，不编码层级分隔符 `/`。
- **`fetch-owner=true`**：ListObjectsV2 的 `Contents` 中返回 `Owner`。
- **传输层头剥离**：`Content-Encoding: aws-chunked`、`x-amz-trailer`、
  `x-amz-decoded-content-length` 不作为对象元数据保存或回显。
- **MinIO 健康检查**：`/minio/health/live`、`/minio/health/ready`、`/minio/health/cluster` 匿名可用。
- **未知查询参数**：返回 `501`，避免被误判为列举请求而静默返回错误结果。

## 错误码

| HTTP | Code | 场景 |
| --- | --- | --- |
| 400 | `InvalidBucketName` | 桶名不符合规则 |
| 400 | `MalformedXML` | 请求体不是合法 XML |
| 400 | `BadDigest` | `Content-MD5` 不匹配 |
| 400 | `XAmzContentSHA256Mismatch` | 声明的 payload hash 与实际不符 |
| 400 | `InvalidPart` / `InvalidPartOrder` | 分片不存在 / ETag 不匹配 / 顺序错误 |
| 400 | `EntityTooLarge` | XML 或请求体超过上限 |
| 403 | `AccessDenied` | 非桶 owner 访问 |
| 403 | `InvalidAccessKeyId` | 密钥不存在或被禁用 |
| 403 | `SignatureDoesNotMatch` | 签名不匹配 |
| 403 | `RequestTimeTooSkewed` | 时钟偏差过大 |
| 404 | `NoSuchBucket` / `NoSuchKey` / `NoSuchUpload` | 资源不存在 |
| 409 | `BucketNotEmpty` | 删除非空桶 |
| 409 | `BucketAlreadyExists` / `BucketAlreadyOwnedByYou` | 重复建桶 |
| 411 | `MissingContentLength` | PUT 无长度信息 |
| 416 | `InvalidRange` | Range 不可满足 |
| 501 | `NotImplemented` | 未实现的子资源/参数 |

错误响应为无命名空间的 `<Error>` XML，包含 `Code`、`Message`、`Resource`、`RequestId`
（与 AWS 一致），并带 `x-amz-request-id` 响应头。
