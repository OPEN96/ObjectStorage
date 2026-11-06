# 客户端接入指南

服务端实现的是 **AWS S3 REST API**，因此任何支持自定义 endpoint 的 S3 客户端都可以接入。
唯一的硬性要求是 **path-style 寻址**（默认关闭虚拟主机寻址）。

## 端点与凭证

| 项 | 值 |
| --- | --- |
| Endpoint | `http://127.0.0.1:9000`（生产环境为你的 TLS 域名） |
| Region | `us-east-1`（可用 `S3:Region` 修改；`EnforceRegion=false` 时客户端 region 不敏感） |
| AccessKey / Secret | 首次启动配置的 `BootstrapAccessKeys`，或在管理端创建 |
| 寻址方式 | path-style：`http://host/bucket/key` |

> 本地开发默认凭证见 `src/S3ObjectStorage.Api/appsettings.Development.json`，
> **仅 Development 环境有效**，切勿用于任何共享或生产环境。

## AWS CLI

```bash
export AWS_ACCESS_KEY_ID=AKIA...
export AWS_SECRET_ACCESS_KEY=...
export AWS_DEFAULT_REGION=us-east-1

aws --endpoint-url http://127.0.0.1:9000 s3 mb s3://demo
aws --endpoint-url http://127.0.0.1:9000 s3 cp ./big.bin s3://demo/big.bin
aws --endpoint-url http://127.0.0.1:9000 s3 ls s3://demo/
aws --endpoint-url http://127.0.0.1:9000 s3 presign s3://demo/big.bin --expires-in 600
aws --endpoint-url http://127.0.0.1:9000 s3 rb s3://demo --force
```

AWS CLI v2 默认会为部分操作启用 CRC 校验与 `aws-chunked` 流式签名，服务端已支持
（包括 `STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER`）。

## MinIO Client（mc）

```bash
mc alias set mystore http://127.0.0.1:9000 AKIA... <secret> --api S3v4 --path on
mc mb mystore/demo
mc cp ./big.bin mystore/demo/          # 超过阈值自动分片
mc ls mystore/demo/
mc cat mystore/demo/big.bin > /dev/null
mc stat mystore/demo/big.bin
mc share download --expire 10m mystore/demo/big.bin
mc rm --recursive --force mystore/demo
mc rb mystore/demo
```

`mc alias set` 会依次探测 `ListBuckets` 与 `GetBucketLocation`，两者均已实现。

## Python（boto3）

```python
import boto3

s3 = boto3.client(
    "s3",
    endpoint_url="http://127.0.0.1:9000",
    aws_access_key_id="AKIA...",
    aws_secret_access_key="...",
    region_name="us-east-1",
    config=boto3.session.Config(s3={"addressing_style": "path"}),
)

s3.create_bucket(Bucket="demo")
s3.upload_file("big.bin", "demo", "big.bin")
print(s3.list_objects_v2(Bucket="demo"))
```

## Python（minio-py）

```python
from minio import Minio

client = Minio(
    "127.0.0.1:9000",
    access_key="AKIA...",
    secret_key="...",
    secure=False,
)

client.make_bucket("demo")
client.fput_object("demo", "big.bin", "big.bin", part_size=5 * 1024 * 1024)
for obj in client.list_objects("demo", recursive=True):
    print(obj.object_name, obj.size)
```

## .NET（AWS SDK）

```csharp
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

var client = new AmazonS3Client(
    new BasicAWSCredentials("AKIA...", "..."),
    new AmazonS3Config
    {
        ServiceURL = "http://127.0.0.1:9000",
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
    });

await client.PutBucketAsync("demo");
await client.PutObjectAsync(new PutObjectRequest
{
    BucketName = "demo",
    Key = "a.txt",
    ContentBody = "hello",
});

using var response = await client.GetObjectAsync("demo", "a.txt");
```

## .NET（minio-dotnet 6.x）

```csharp
using Minio;

var minio = MinioClient
    .CreateBuilder()
    .WithEndpoint("127.0.0.1", 9000)
    .WithCredentials("AKIA...", "...")
    .WithSSL(false)
    .Build();

await minio.PutObjectAsync(new PutObjectArgs()
    .WithBucket("demo")
    .WithObject("a.txt")
    .WithStreamData(stream)
    .WithObjectSize(stream.Length));
```

## Java（AWS SDK v2）

```java
S3Client s3 = S3Client.builder()
    .endpointOverride(URI.create("http://127.0.0.1:9000"))
    .credentialsProvider(StaticCredentialsProvider.create(
        AwsBasicCredentials.create("AKIA...", "...")))
    .region(Region.US_EAST_1)
    .forcePathStyle(true)
    .build();

s3.createBucket(b -> b.bucket("demo"));
s3.putObject(b -> b.bucket("demo").key("a.txt"), RequestBody.fromString("hello"));
```

## Node.js（AWS SDK v3）

```js
import { S3Client, CreateBucketCommand, PutObjectCommand } from "@aws-sdk/client-s3";

const s3 = new S3Client({
  endpoint: "http://127.0.0.1:9000",
  region: "us-east-1",
  forcePathStyle: true,
  credentials: { accessKeyId: "AKIA...", secretAccessKey: "..." },
});

await s3.send(new CreateBucketCommand({ Bucket: "demo" }));
await s3.send(new PutObjectCommand({ Bucket: "demo", Key: "a.txt", Body: "hello" }));
```

## 预签名 URL

- `GET` / `PUT` 均支持；
- 有效期 1 秒 – 7 天（`X-Amz-Expires` 会被限制在该区间）；
- 预签名 URL 的签名包含 Host，换域名/端口会失效；
- 预签名请求不需要 `Authorization` 头。

```python
url = client.presigned_get_object("demo", "big.bin", expires=timedelta(minutes=10))
```

## 虚拟主机寻址（可选）

设置 `S3:VirtualHostSuffix=s3.example.com` 并配置泛解析 `*.s3.example.com` 后，
`bucket.s3.example.com/key` 会被解析为桶 `bucket`。默认关闭，客户端请使用 `ForcePathStyle=true`。

## 兼容性矩阵

| 客户端 | 版本 | 验证内容 | 结果 |
| --- | --- | --- | --- |
| AWS SDK for .NET | 4.0.104.7 | 桶/对象/分片/预签名/条件请求/管理端 | 85 项集成测试通过 |
| MinIO .NET SDK | 6.0.4 | 桶/对象/分片/预签名/encoding-type | 集成测试通过 |
| minio-py | 7.2.20 | 20 项（含特殊字符 key、分片、复制、预签名） | 20/20 |
| MinIO Client `mc` | RELEASE.2022-12 及以上 | 12 项（alias/建桶/上传/下载/列举元数据/预签名/删除） | 12/12（`scripts/mc_compat.sh` 可复跑） |
| 独立 SigV4 脚本 | — | 18 项协议行为 | 18/18 |

## 常见客户端问题

| 报错 | 原因 | 处理 |
| --- | --- | --- |
| `SignatureDoesNotMatch` | 客户端用了 virtual-host 寻址；或代理改写 Host | 开启 `forcePathStyle`；代理透传原始 Host |
| `AuthorizationHeaderMalformed` | Credential scope 的 service/region 不对 | region 用 `us-east-1`，service 必须是 `s3` |
| `RequestTimeTooSkewed` | 本机时间偏差过大 | 同步系统时间 |
| `InvalidAccessKeyId` | 密钥不存在或被禁用 | 管理端确认密钥状态 |
| 上传 403 但签名正确 | 桶属于其它账号 | 用桶 owner 的密钥，或管理员密钥 |
| `mc alias set` 失败 | 用了 DNS 寻址或 TLS 校验失败 | 加 `--path on`；HTTP 端点不要加 `--insecure` 之外的自定义 CA |
| `mc stat` 报 `Target ... not found` | mc 自身缺陷：部分版本在 Windows + IPv4 端点下连桶都 stat 不出 | 用 `mc ls --json <alias>/<bucket>/<key>` 取元数据；服务端 `HEAD` 本身正常 |

> 排查技巧：`mc --debug ls|cp ...` 会打印完整的请求头与响应头，能直接看出是签名、
> 编码还是权限问题；服务端返回的 `x-amz-request-id` 可在日志中检索同一次请求。
