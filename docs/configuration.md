# 配置参考

配置来源优先级（后者覆盖前者）：

1. `appsettings.json`
2. `appsettings.{Environment}.json`（仓库只提供 `Development`）
3. 环境变量（前缀 `S3STORAGE_`，层级用 `__`）
4. 命令行参数

环境变量映射规则：`S3STORAGE_Admin__Token` → `Admin:Token`，
`S3STORAGE_Storage__BootstrapAccessKeys__0__AccessKeyId` → `Storage:BootstrapAccessKeys[0]:AccessKeyId`。

> 生产环境（`ASPNETCORE_ENVIRONMENT=Production`）不会加载 `appsettings.Development.json`。

## Admin（管理端）

| 配置项 | 环境变量 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `Admin:Enabled` | `S3STORAGE_Admin__Enabled` | `true` | 关闭后 `/admin` 全路径返回 404 |
| `Admin:Token` | `S3STORAGE_Admin__Token` | 空 | 管理员令牌。生产环境必填且 ≥24 位；开发环境留空会生成随机令牌并打印到日志 |
| `Admin:SessionHours` | `S3STORAGE_Admin__SessionHours` | `12` | 登录 Cookie 有效期（小时），最小 1 |

### 会话与凭证的失效语义

管理端会话是 HMAC 签名的 HttpOnly Cookie，服务端不存会话表，但**用访问密钥登录的会话会绑定当时的 Secret 指纹**：

| 操作 | 已签发的会话 | 说明 |
| --- | --- | --- |
| 禁用 / 删除该访问密钥 | 立即失效 | 每次请求回查密钥是否存在且启用 |
| 轮换该密钥的 Secret | 立即失效 | Cookie 中的指纹与新 Secret 不匹配 |
| 轮换 `Admin:Token` | 全部失效 | 签名密钥由 Token 派生，换 Token 等于全员登出 |
| 令牌登录的会话 | 到期或换 Token 才失效 | 与具体访问密钥无关 |

### 轮换访问密钥

`PATCH /admin/api/access-keys/{accessKeyId}` 支持三个字段，可只传其一：

```bash
curl -X PATCH http://127.0.0.1:9000/admin/api/access-keys/AKIA... \
  -H "Authorization: Bearer $S3STORAGE_Admin__Token" \
  -H 'Content-Type: application/json' \
  -d '{"secretAccessKey":"新的至少 16 位随机串"}'
```

- 轮换是**原地**的：`AccessKeyId` 不变，因此桶属主（`OwnerId` = AccessKeyId）与客户端配置无需改动；
- 轮换后旧 Secret 的签名、预签名 URL 与会话立即失效；
- 控制台「访问密钥 → 轮换」按钮等价于该接口，并会生成 48 位随机 Secret。

> 不要用「删除 + 新建密钥」来轮换：新建的 AccessKeyId 不同，原密钥名下桶的属主校验会失败，
> 客户端将看不到旧数据（管理员密钥仍可访问）。

## S3（协议）

| 配置项 | 环境变量 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `S3:Region` | `S3STORAGE_S3__Region` | `us-east-1` | 对外宣称的区域；`GetBucketLocation` 返回该值 |
| `S3:EnforceRegion` | `S3STORAGE_S3__EnforceRegion` | `false` | 是否要求客户端 Credential scope 中的 region 与之一致 |
| `S3:TimestampSkewSeconds` | `S3STORAGE_S3__TimestampSkewSeconds` | `900` | 允许的时钟偏差 |
| `S3:VirtualHostSuffix` | `S3STORAGE_S3__VirtualHostSuffix` | 空 | 非空时启用 `bucket.suffix` 虚拟主机寻址，例如 `s3.localhost` |
| `S3:RequireAuthentication` | `S3STORAGE_S3__RequireAuthentication` | `true` | 关闭后匿名请求可读写（**仅调试**） |
| `S3:MaxKeyLength` | `S3STORAGE_S3__MaxKeyLength` | `1024` | 对象 key 最大字符数 |

## Storage（存储）

| 配置项 | 环境变量 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `Storage:RootPath` | `S3STORAGE_Storage__RootPath` | `data/objectstorage` | 元数据库与对象体根目录 |
| `Storage:ConnectionString` | `S3STORAGE_Storage__ConnectionString` | 空 | 自定义 SQLite 连接串；空则使用 `RootPath/metadata.db` |
| `Storage:VerifyPayloadHash` | `S3STORAGE_Storage__VerifyPayloadHash` | `true` | 校验 `x-amz-content-sha256` |
| `Storage:MaxSinglePutBytes` | `S3STORAGE_Storage__MaxSinglePutBytes` | `0` | 单次 PUT 上限（字节），0 表示不限 |
| `Storage:BufferSize` | `S3STORAGE_Storage__BufferSize` | `131072` | 读写缓冲（4 KiB–4 MiB） |
| `Storage:BootstrapAccessKeys` | 见下方示例 | `[]` | 首次启动（元数据库为空）写入的初始密钥 |
| `Storage:OrphanSweepEnabled` | `S3STORAGE_Storage__OrphanSweepEnabled` | `true` | 是否回收「磁盘上有文件、元数据里没有引用」的孤儿对象体 |
| `Storage:OrphanSweepIntervalHours` | `S3STORAGE_Storage__OrphanSweepIntervalHours` | `6` | 回收周期（小时） |
| `Storage:OrphanSweepInitialDelaySeconds` | `S3STORAGE_Storage__OrphanSweepInitialDelaySeconds` | `60` | 启动后首次扫描的延迟（秒） |
| `Storage:OrphanGraceHours` | `S3STORAGE_Storage__OrphanGraceHours` | `1` | 只删除修改时间早于该阈值的孤儿文件，避免误删正在写入的对象 |
| `Storage:OrphanSweepMaxObjects` | `S3STORAGE_Storage__OrphanSweepMaxObjects` | `500000` | 单轮扫描的对象数上限，超过则跳过整轮（防止大集群上打爆内存） |

### 孤儿对象体回收

并发覆写同一个 key 时，旧的对象体文件会先落盘、再由新记录取代；若两次写入并发交错，
可能留下「没有任何元数据引用」的文件。回收器按周期做一次差集：

1. 把 `Objects.BlobPath` 全量读入内存集合；
2. 枚举 `blobs/` 下的 `*.bin`；
3. 只删除「不在集合内、且 mtime 早于 `OrphanGraceHours`」的文件。

实测：模拟 8 并发覆写 200 次后回收 10150 个孤儿文件、释放 1.45 GB，在用对象校验一致。
关闭该功能（`OrphanSweepEnabled=false`）不会影响正确性，只是孤儿文件会持续占用磁盘。

### 初始访问密钥

```json
{
  "Storage": {
    "BootstrapAccessKeys": [
      {
        "AccessKeyId": "AKIAEXAMPLELOCAL001",
        "SecretAccessKey": "至少 16 位的随机字符串",
        "DisplayName": "root",
        "IsAdmin": true
      }
    ]
  }
}
```

环境变量写法：

```bash
export S3STORAGE_Storage__BootstrapAccessKeys__0__AccessKeyId=AKIA...
export S3STORAGE_Storage__BootstrapAccessKeys__0__SecretAccessKey=...
export S3STORAGE_Storage__BootstrapAccessKeys__0__DisplayName=root
export S3STORAGE_Storage__BootstrapAccessKeys__0__IsAdmin=true
```

行为说明：

- 只在 `AccessKeys` 表为空时写入，之后修改配置不会覆盖已有密钥；
- 生产环境若库为空且未配置，会**拒绝启动**，避免无凭证或把密钥打印到日志；
- 开发环境若未配置，会生成随机密钥并把 AccessKeyId/Secret 打到日志（仅限本地）。

## Kestrel / 日志

| 配置项 | 环境变量 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `Kestrel:Endpoints:Http:Url` | 建议用 `ASPNETCORE_URLS` 或 `Kestrel__Endpoints__Http__Url` | `http://0.0.0.0:9000` | 监听地址 |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` | `Information` | 日志级别；EF Core 与 ASP.NET 默认压到 Warning |

容器内已固定 `Kestrel__Endpoints__Http__Url=http://0.0.0.0:9000`，映射端口用 compose 的 `S3_PORT`。

## 反向代理（TLS 终止 / 负载均衡）

默认**不信任**任何 `X-Forwarded-For/Proto/Host`。前面挂了 nginx / Caddy / Traefik 时必须显式开启，
否则会有两个真实后果：

- 管理端登录节流按来源 IP 计数，所有请求都会算到代理 IP 上——任意一人输错 10 次，**所有人**被锁 5 分钟；
- `Request.IsHttps` 恒为 false，会话 Cookie 不会带 `Secure`，即使客户端走的是 HTTPS。

| 配置 | 环境变量 | 默认 | 说明 |
| --- | --- | --- | --- |
| `ReverseProxy:Enabled` | `S3STORAGE_ReverseProxy__Enabled` | `false` | 是否采纳转发头 |
| `ReverseProxy:KnownProxies` | `S3STORAGE_ReverseProxy__KnownProxies__0`、`__1`… | `[]` | 可信代理的 IP 或 CIDR；不在名单内的来源一律忽略其转发头 |
| `ReverseProxy:ForwardLimit` | `S3STORAGE_ReverseProxy__ForwardLimit` | `1` | 最多信任几层代理 |

开启时至少配置一条 `KnownProxies`，否则启动直接失败。`0.0.0.0/0`、`::/0` 会被拒绝——
那等于信任所有来源，任何客户端都能伪造 IP 绕过节流。

```bash
# 代理与服务端同机（nginx 反代 127.0.0.1:9000）
S3STORAGE_ReverseProxy__Enabled=true
S3STORAGE_ReverseProxy__KnownProxies__0=127.0.0.1

# 代理在 Compose 内的另一个容器，按网段声明
S3STORAGE_ReverseProxy__KnownProxies__0=172.18.0.0/16
```

> **不要用 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`**：它沿用框架默认白名单（仅 loopback），
> 容器里的代理 IP 不在名单内，转发头会被静默忽略，表现为"配了没用"。

| 变量 | 说明 |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production`；生产必须为 `Production` |

## 生产环境检查清单

- [ ] `ASPNETCORE_ENVIRONMENT=Production`
- [ ] `S3STORAGE_Admin__Token` ≥24 位随机值（`openssl rand -base64 48`）
- [ ] `S3STORAGE_Storage__BootstrapAccessKeys__0__SecretAccessKey` ≥16 位随机值
- [ ] `S3:RequireAuthentication=true`、`Storage:VerifyPayloadHash=true`
- [ ] 前置 TLS 终止；`/admin` 限制来源 IP
- [ ] 数据目录仅服务账号可读写（`chown 1654:1654` 或对应 UID）
- [ ] 配置磁盘容量告警与 `/minio/health/ready` 探针
- [ ] 备份 `Storage:RootPath`（SQLite + blobs），并验证恢复流程
