# 部署与运维

## 选择部署方式

| 方式 | 适用场景 | 数据位置 |
| --- | --- | --- |
| `dotnet run` | 本地开发 | `src/S3ObjectStorage.Api/data/objectstorage` |
| `dotnet publish` + systemd | 单机长期运行 | 由 `Storage:RootPath` 指定，例如 `/var/lib/s3objectstorage` |
| Docker / docker compose | 单机、快速交付 | 命名卷 `s3objectstorage-data` |

三种方式共用同一份配置模型，见 [configuration.md](configuration.md)。

## 资源规格

| 项目 | 实测结论 |
| --- | --- |
| 内存 | 峰值 RSS 约 185 MB（16 并发 × 32 MB 分片上传）。192 MB 限额会被 OOM Kill（exit 137），**最小 256 MB，推荐 512 MB～1 GB** |
| CPU | 2 核可跑满（4 KB PUT 约 156 rps / GET 约 320 rps），1 核约为其一半 |
| 磁盘 | 对象体 + SQLite；覆写频繁时 1 小时内会产生孤儿文件，需按写放大预留空间（见 configuration.md 的孤儿回收） |
| 文件句柄 | 稳定在 270 个左右（长稳 10 分钟无增长）；容器默认 `nofile=65536` 足够 |

```yaml
# docker-compose.yml
    deploy:
      resources:
        limits:
          memory: 512M
```

## 方式一：二进制 + systemd

```bash
dotnet publish src/S3ObjectStorage.Api/S3ObjectStorage.Api.csproj \
  -c Release -o /opt/s3objectstorage -p:UseAppHost=false

sudo useradd --system --home /var/lib/s3objectstorage --shell /usr/sbin/nologin s3storage
sudo mkdir -p /var/lib/s3objectstorage
sudo chown -R s3storage:s3storage /var/lib/s3objectstorage
```

`/etc/s3objectstorage.env`（`chmod 600`，属主 root）：

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:9000
S3STORAGE_Admin__Token=<openssl rand -base64 48>
S3STORAGE_Storage__RootPath=/var/lib/s3objectstorage
S3STORAGE_Storage__BootstrapAccessKeys__0__AccessKeyId=AKIA...
S3STORAGE_Storage__BootstrapAccessKeys__0__SecretAccessKey=<openssl rand -base64 36>
S3STORAGE_Storage__BootstrapAccessKeys__0__DisplayName=root
S3STORAGE_Storage__BootstrapAccessKeys__0__IsAdmin=true
```

`/etc/systemd/system/s3objectstorage.service`：

```ini
[Unit]
Description=S3ObjectStorage
After=network-online.target
Wants=network-online.target

[Service]
# 本项目未集成 sd_notify，用 Type=notify 会等满 TimeoutStartSec 后被判启动失败。
# 启动是否真的成功由上层的 /minio/health/ready 探针判断。
Type=simple
User=s3storage
Group=s3storage
EnvironmentFile=/etc/s3objectstorage.env
WorkingDirectory=/opt/s3objectstorage
ExecStart=/usr/bin/dotnet /opt/s3objectstorage/S3ObjectStorage.Api.dll
Restart=always
RestartSec=3
TimeoutStopSec=30
# 加固
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/var/lib/s3objectstorage
ProtectHome=true

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now s3objectstorage
journalctl -u s3objectstorage -f
```

## 方式二：Docker Compose

```bash
cp .env.example .env
# 编辑 .env：ADMIN_TOKEN、S3_ACCESS_KEY、S3_SECRET_KEY 必须替换
docker compose up -d --build
docker compose ps
docker compose logs -f objectstorage
```

- 镜像以 `APP_UID`（1654）非 root 运行；
- 数据卷 `s3objectstorage-data` 挂载到 `/data`；
- 修改端口：`S3_PORT=9100 docker compose up -d`；
- 使用 bind mount 时先 `chown -R 1654:1654 /host/path`。

### 可选：进一步收紧容器权限

`compose` 已包含 `init: true`、`cap_drop: ALL`、`no-new-privileges`。若想再收紧，可加只读根文件系统
（实测 `smoke.py` 仍 18/18）：

```yaml
    read_only: true
    tmpfs:
      - /tmp:rw,noexec,nosuid,size=64m
```

裸 `docker run` 时务必带 `--init`：否则 dotnet 成为 PID 1，信号处理与僵尸进程回收都会退化，
`docker stop` 要等满超时，未捕获异常的退出码也会从 134 变成 139。

## 反向代理与 TLS

S3 的大对象上传/下载必须**关闭代理缓冲**，否则会先落盘到代理、延迟陡增甚至超时。

代理会改写来源 IP 与协议，因此服务端必须显式登记代理地址，否则登录节流会把所有用户
算成同一个 IP（一人输错 10 次全员被锁 5 分钟），Cookie 也不会带 `Secure`：

```bash
# nginx 与本服务同机
S3STORAGE_ReverseProxy__Enabled=true
S3STORAGE_ReverseProxy__KnownProxies__0=127.0.0.1
```

只在代理可信时开启；名单外的来源即使带了 `X-Forwarded-For` 也会被忽略。

### Nginx

```nginx
server {
    listen 443 ssl http2;
    server_name s3.example.com;

    ssl_certificate     /etc/letsencrypt/live/s3.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/s3.example.com/privkey.pem;

    # 对象大小不设上限（或按业务设定）
    client_max_body_size 0;

    location / {
        proxy_pass http://127.0.0.1:9000;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # 关键：不要缓冲请求与响应
        proxy_request_buffering off;
        proxy_buffering off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # 管理端建议限制来源
    location /admin/ {
        allow 10.0.0.0/8;
        allow 192.168.0.0/16;
        deny all;
        proxy_pass http://127.0.0.1:9000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### Caddy

```caddyfile
s3.example.com {
    request_body {
        max_size 0
    }
    reverse_proxy 127.0.0.1:9000 {
        flush_interval -1
        transport http {
            read_timeout 1h
            write_timeout 1h
        }
    }
}
```

> 转发头默认**全部忽略**。挂了代理就必须显式开启并登记代理地址（见上一节）：
>
> ```bash
> S3STORAGE_ReverseProxy__Enabled=true
> S3STORAGE_ReverseProxy__KnownProxies__0=127.0.0.1
> ```
>
> 不要改用 `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`：它沿用框架的 loopback 白名单，
> 容器或另一台机器上的代理不在名单内，转发头会被静默忽略。细节见
> [configuration.md](configuration.md#反向代理tls-终止--负载均衡)。

## 健康检查与监控

| 端点 | 用途 | 说明 |
| --- | --- | --- |
| `GET /minio/health/live` | 存活 | 进程能响应即 200 |
| `GET /minio/health/ready` | 就绪 | 探测 SQLite，可连接返回 200，否则 503 |
| `GET /admin/api/health` | 管理端健康 | 返回版本与时间 |

- 镜像内置 TCP 健康检查；
- 管理端「概览」展示请求数、4xx/5xx、流量、内存、磁盘；
- 指标是**进程内累计值**，重启归零；长期监控请采集日志或在反向代理层统计。

## 备份

对象体与元数据都在同一个目录/卷中，必须一起备份。

### 停机备份（推荐，强一致）

```bash
docker compose stop objectstorage
docker run --rm -v s3objectstorage-data:/data -v "$PWD:/backup" alpine \
  tar czf /backup/s3objectstorage-$(date +%F).tar.gz -C /data .
docker compose start objectstorage
```

### 在线备份（SQLite 一致性快照）

```bash
# 宿主机能访问数据目录时
sqlite3 /var/lib/s3objectstorage/metadata.db ".backup '/backup/metadata-$(date +%F).db'"
rsync -a --delete /var/lib/s3objectstorage/blobs/ /backup/blobs/
```

> 直接 `cp` 正在写入的 SQLite 文件可能得到损坏副本；请用 `.backup` 或停机打包。

实测（112 MB 热备、3 路并发写入 180 个对象）：

- 恢复出来的实例可以正常启动，元数据库无损坏、无「引用存在但文件缺失」；
- 但数据库与 blob 目录并非同一时刻的快照，会带入部分**未被引用的** blob（本次 71 个），
  也会丢掉快照瞬间之后才提交的写入（本次 117/180）——属于可接受的数据回退，不是损坏；
- 若要求零丢失，请用停机备份，或在宿主机上先 `sqlite3 ... ".backup"` 再做文件系统快照（LVM/ZFS/btrfs）。

### 恢复

```bash
docker compose stop objectstorage
docker run --rm -v s3objectstorage-data:/data -v "$PWD:/backup" alpine \
  sh -c "rm -rf /data/* && tar xzf /backup/s3objectstorage-2026-11-05.tar.gz -C /data"
docker compose start objectstorage
```

恢复后访问 `/minio/health/ready` 确认元数据库可读，再抽查若干对象。

实测（9 桶 / 18 对象 / 32.5 MB）：恢复实例的桶、对象、ETag、分片任务与访问密钥清单与备份时**逐一相同**，
26 个对象体的 MD5 汇总也完全一致；随后可用 `scripts/check_refs.py` 复核「元数据引用 ↔ 磁盘文件」的对应关系。

## 升级与回滚

```bash
git fetch --tags
git checkout v0.2.0
docker compose build --pull
docker compose up -d
```

- 数据卷不受镜像升级影响；实测 0.1.5 → 0.2.0 原地升级：启动 6 秒内健康，桶/对象/密钥快照与升级前完全一致。
- 元数据库有显式的 **schema 版本号**（SQLite 的 `PRAGMA user_version`）：
  - 启动时若库版本低于程序版本，按 `SchemaGuard.Upgrades` 顺序执行幂等升级语句并写回新版本；
  - 若库版本**高于**程序版本（用旧版程序打开新版数据），会**拒绝启动**并给出明确提示，
    避免旧代码按旧结构写坏新数据；
  - 因此回滚到旧版本前，请确认该版本能识别当前 schema 版本，否则从备份恢复。
- 升级日志里会出现 `元数据库 schema 版本已更新为 vN（原 vM）`，可据此确认迁移是否发生。


## 故障排查

| 现象 | 常见原因 | 处理 |
| --- | --- | --- |
| `SignatureDoesNotMatch` | 时钟偏差 >15 分钟；region 不一致；代理改写了 Host/路径 | 校时；`S3:EnforceRegion=false` 或统一 region；确保代理保留原始 Host 与路径 |
| `RequestTimeTooSkewed` | 客户端时钟错误 | 同步 NTP |
| `411 MissingContentLength` | PUT 既无 `Content-Length` 也无 `Transfer-Encoding` | 使用支持流式上传的客户端 |
| `501 NotImplemented` | 使用了未实现的子资源（acl/tagging/versioning…） | 见 [api-support.md](api-support.md)；不要依赖这些能力 |
| 管理端登录 401 | 令牌错误或密钥被禁用/非管理员 | 检查 `Admin:Token`；用管理员密钥登录 |
| 管理端登录 429 | 触发失败节流（10 次/5 分钟/IP） | 等待 5 分钟；检查是否被暴力尝试 |
| 启动即退出，日志提示必须配置 Token/密钥 | 生产环境缺少必填配置 | 按 [configuration.md](configuration.md) 注入环境变量 |
| `/minio/health/ready` 返回 503 | SQLite 不可读、磁盘满、权限错误 | 检查目录权限与磁盘；查看日志 |
| 容器启动后权限错误 | bind mount 属主不是 UID 1654 | `chown -R 1654:1654 <dir>` |
| 裸 `docker run` 时 `docker stop` 要等 10 秒、退出码是 139 | 没加 `--init`，dotnet 成为 PID 1，信号与僵尸进程回收都退化 | 加 `--init`（compose 已配置 `init: true`）；正常崩溃应为 134 |
| 上传大文件超时 | 反代缓冲/超时 | 关闭 `proxy_request_buffering`，调大超时 |
| 对象下载 404 但列表里有 | blob 文件被外部删除 | 从备份恢复；检查是否有外部清理任务 |

日志默认为结构化文本，包含 `x-amz-request-id`；排查客户端问题时让用户提供该 ID。
