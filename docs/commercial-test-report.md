# 商业级验收测试报告

| 项目 | 内容 |
| --- | --- |
| 被测版本 | 0.2.0（镜像 `s3objectstorage:0.2.0`，269 MB） |
| 测试时间 | 2026-11-05 14:30 — 2026-11-06 01:30（UTC+8） |
| 测试环境 | Ubuntu 24.04 / 6 核 / 15 GB（可用约 2 GB）/ ext4 251 GB / Docker 29.9.0 |
| 部署形态 | 单容器，`--read-only --cap-drop ALL --init --memory 1g --cpus 2`，命名卷 `/data` |
| 测试方式 | 真实网络（压测端与被测端分离）、真实 Docker、真实 AWS/MinIO SDK 与自研 C 压测器 |
| 结论 | **可用于单机生产**；11 项问题中 9 项已修复并回归，2 项转为已知限制（见第 11 节） |

---

## 1. 结论摘要

- **功能与协议**：`smoke.py` 18/18、minio-py 兼容 20/20、`mc` 12/12、容量与分片 15/15、xUnit 85/85 全部通过。
- **安全**：24 项对抗探测全部通过——预签名过期/篡改、错误签名、未知 AccessKey、载荷哈希不符、
  路径穿越、超限元数据、畸形与超深 XML、跨桶 uploadId 复用、非法桶名、管理端越权与跨站请求、
  错误响应信息泄露、SQL 注入式 key。
- **密钥与权限**：24 项通过——租户隔离、禁用/启用/删除的即时生效、原地轮换 Secret、最后一个管理员保护、会话撤销、Cookie 伪造。
- **数据可靠性**：停机冷备 → 恢复后桶/对象/ETag/分片任务/密钥清单**逐一相同**，26 个对象体 MD5 汇总完全一致；
  0.1.5 → 0.2.0 原地升级数据零差异；0.2.0 → 0.1.5 回滚可用（0.1.x 为 0.2.0 之前的开发中间版本，未公开发布）。
- **稳定性**：10 分钟混合压测（GET 29.3 万次 / PUT 7.2 万次）RSS 179→182 MB、句柄 268→264、线程 20→17、
  WAL 4.0→7.5 MB 后持平，容器日志 0 条 error/warn。
- **性能**：修复 fsync 放大后，4 KB PUT 并发 8 从 14 rps 提升到 **82 rps**（×5.9），并发 32 从 20 提升到 **156 rps**（×7.8）；
  200 MB 分片上传 **38.5 MB/s**、全量下载 **44.6 MB/s**（修复前 15.2 / 20.5 MB/s）。

---

## 2. 测试范围与方法

| 维度 | 覆盖内容 | 工具 |
| --- | --- | --- |
| 功能与协议回归 | 桶/对象 CRUD、ListObjects V1+V2、Range、条件请求、批量删除、CopyObject、分片全流程 | `smoke.py`、minio-py、`mc`、xUnit |
| 性能 | 4 KB / 256 KB 的 GET/PUT，单并发到 64 并发，P50/P90/P95/P99 | `loadgen.c`、`bench.py` |
| 容量与完整性 | 200 MB 分片上传 + SHA256 全量校验 + Range + 手工分片 + Abort | `capacity_test.py` |
| 故障注入 | 进程 SIGKILL、崩溃后分片续传、上传中断、磁盘写满、内存限额、高版本库拒绝启动 | docker（SIGKILL / cgroup 内存限制 / tmpfs 限额） |
| 安全对抗 | 签名/预签名/载荷/路径/输入/鉴权/信息泄露/注入 | `security_probe.py` |
| 密钥与权限 | 签发、禁用、删除、轮换、租户隔离、会话撤销、最后一个管理员 | `key_permission_test.py` |
| 备份恢复 | 停机冷备、在线热备、恢复校验、原地升级、回滚 | `snapshot.py`、`check_refs.py`、`soak.sh` |
| 稳定性与可观测性 | 10 分钟长稳 + 每 10 s 采样 RSS/anon/FD/线程/WAL/cgroup 内存 | `soak.sh` |

压测端与被测端严格分离：压测运行在宿主机（不经容器端口转发路径的额外开销），
`loadgen` 为自写 C 程序（多线程 + keep-alive + 逐请求延迟分位），避免 Python GIL 与 `ab` 自身吞吐上限的污染。

---

## 3. 功能与协议回归

| 套件 | 结果 | 说明 |
| --- | --- | --- |
| xUnit（`dotnet test`） | **85/85** | 含真实 Kestrel + AWS SDK / MinIO SDK 集成测试 |
| `smoke.py` | **18/18** | 自实现 SigV4，作为与服务端无关的第三方校验 |
| minio-py 兼容 | **20/20** | 预签名、分片、条件请求、`fetch-owner`、`encoding-type=url` |
| `mc` 兼容 | **12/12** | `mb/ls/cp/rm/stat/du`、`--json` 输出、多段上传 |
| `capacity_test.py` | **15/15** | 见第 5 节 |

---

## 4. 性能基线

环境：容器 2 vCPU / 1 GB，宿主 load 5–7（另有 10 个业务容器），对象 4 KB / 256 KB。

| 场景 | 单并发 | P50 | 高并发 | P50 | P99 |
| --- | --- | --- | --- | --- | --- |
| 4 KB GET | 63 rps | 12.6 ms | c=64：321 rps | 192 ms | — |
| 4 KB PUT | 27 rps | 32.8 ms | c=32：156 rps | 191 ms | — |
| 256 KB GET | 31 rps / 7.8 MB/s | 31 ms | c=32：48.5 rps / 12.1 MB/s | 660 ms | — |
| 256 KB PUT | 30 rps / 7.6 MB/s | 33 ms | c=32：47.4 rps / 11.9 MB/s | 672 ms | — |
| 200 MB 分片上传 | 38.5 MB/s | — | — | — | — |
| 200 MB 全量下载 | 44.6 MB/s | — | — | — | — |

长稳压测（持续 10 分钟、单对象热点、GET c=6 + PUT c=2）：

| 指标 | 结果 |
| --- | --- |
| GET | 292 542 次成功，529 rps，P50 7.99 ms / P95 25.14 ms / P99 36.49 ms |
| PUT | 72 169 次成功，130.6 rps，P50 11.40 ms / P95 31.47 ms / P99 43.05 ms |
| 长尾 | 单次最大 1231 ms（宿主机 load 抖动，非服务端排队） |

**读多写少的形态下，本机在 2 vCPU 上可稳定支撑约 500 rps 的小对象读取。**

### 写入路径的关键修复

压测初期出现「吞吐随并发上升而下降、4 MB 的 P50 高达 7 秒」的反常现象。逐层排除：

1. 磁盘顺序写 298 MB/s —— 不是 IO 上限；
2. 健康端点 0.3 ms —— 不是网络或事件循环；
3. `/proc/diskstats` 计数：40 次 4 KB PUT 触发 **210 次 fsync（5.25 次/PUT）**，而 100 次 GET 仅 34 次背景噪声。

根因：`PRAGMA synchronous` 与 `busy_timeout` 是**连接级**设置，原实现只在启动时执行一次，
连接池后续新建连接的 `synchronous` 仍是默认的 `FULL`，导致每次元数据提交都落盘。
新增 `SqlitePragmaInterceptor` 在每次连接打开时补齐 PRAGMA 后：

| 场景 | 修复前 | 修复后 |
| --- | --- | --- |
| 4 KB PUT c=8 | 14 rps | **82 rps** |
| 4 KB PUT c=32 | 20 rps | **156 rps** |
| fsync / PUT | 5.25 次 | 0.78 次 |
| 200 MB 上传 | 15.2 MB/s | **38.5 MB/s** |
| 200 MB 下载 | 20.5 MB/s | **44.6 MB/s** |

---

## 5. 容量与大对象

200 MB 文件、16 MB 分片，全流程 SHA256 校验：

| 步骤 | 结果 |
| --- | --- |
| 分片上传（13 片） | 5.19 s / 38.5 MB/s，`ETag` 后缀 `-13` 正确 |
| 元数据 | 大小 209 715 200 字节、自定义元数据保留 |
| 全量下载 | 4.48 s / 44.6 MB/s，SHA256 与源文件一致 |
| Range（50 MB 起 1 MB） | 内容一致 |
| 手工分片（3 片） | Initiate/UploadPart/ListParts/Complete 全部正确，合并后 15 728 640 字节 |
| Abort | 204，任务不再列出，分片目录回收 |

---

## 6. 故障注入

| 场景 | 结果 |
| --- | --- |
| `SIGKILL` 杀进程 | 容器自动重启，**6 s 内恢复 healthy**；分片任务持久化，续传 Complete 后内容一致 |
| 120 MB 单次 PUT 中途断开 | 无半截对象（GET 返回 404），`/data/tmp` 无残留（复查为 0 个临时文件） |
| **磁盘写满**（48 MB tmpfs 卷） | 第 2 个 16 MB 对象起返回 `500 InternalError`；已有对象不受影响、列举一致、临时文件自动清理；容器保持 healthy；日志含 `No space left on device` 与临时文件路径，可定位 |
| **内存限额 192 MB** | 16 并发 × 32 MB 分片上传时 **OOM Kill（exit 137）**，进程峰值 RSS 约 185 MB |
| **内存限额 256 MB** | 同样负载 **16/16 成功**，无 OOM |
| 内存限额 512 MB | 16/16 成功，峰值 RSS 185.6 MB |
| **高版本数据库**（`user_version=99`） | 拒绝启动，`exit 134`，日志明确提示「schema 版本高于当前程序支持」 |

> 结论：磁盘是硬约束但失败是「干净失败」；内存下限是 256 MB，`docker-compose.yml` 已给出 512 MB 的建议值与注释。

---

## 7. 安全对抗（24/24）

| 组别 | 用例 |
| --- | --- |
| 预签名 | 有效期内可访问、过期后 403、签名篡改 403、改路径 403、换密钥 403 |
| 凭据 | 未知 AccessKey 403、内容哈希与实际不符 400、错误签名 403 |
| 输入 | 路径穿越（`%2e%2e%2f`、`..%2F`）403 且越界对象未创建、20 KB 自定义元数据 400、畸形 XML 400、超限 XML 400、超深嵌套 400（10 万层 <2 s） |
| 授权 | 跨桶复用 uploadId 404、非管理员密钥进管理端 401 |
| 管理端 | 未登录 401、跨站 Origin 403、伪造会话 Cookie 401 |
| 其他 | 404 响应不含堆栈/内部路径、`a'; DROP TABLE Objects;--` 式 key 不影响元数据库 |

### 深嵌套 XML 的 CPU 放大

初测发现 150 KB–1.2 MB 的深度嵌套 XML 会让请求挂起约 30 秒（而 >2 MB 因为请求体上限反而快速 400）。
在反序列化前增加 64 层深度扫描（`S3Xml.GuardNestingDepth`），超限直接 `MalformedXML`；
10 万层嵌套现在 2 秒内被拒。

---

## 8. 密钥轮换与权限模型

### 权限模型

- **管理员密钥**（`IsAdmin=true`）：可访问所有桶，可登录管理端；
- **普通密钥**：只能访问 `OwnerId == 自己的 AccessKeyId` 的桶（读/写/删/列举都校验）；
- **管理端会话**：HMAC 签名 HttpOnly Cookie，支持令牌登录与管理员密钥登录；
- **保护**：最后一个可用的管理员密钥不能禁用/删除（409）。

### 24 项结果（全部通过）

| 组别 | 用例 |
| --- | --- |
| 签发 | 新密钥可建桶、可写、可读 |
| 租户隔离 | 跨租户读/写/删/列举 403；ListBuckets 只返回自有桶；非管理员不能进管理端、不能登录管理端 |
| 生命周期 | 禁用后**立即** 403（无缓存延迟）、启用后立即恢复、删除后立即 403 |
| 轮换 | `PATCH {"secretAccessKey": ...}` 原地生效：旧 Secret 403、新 Secret 200，AccessKeyId 与桶属主不变 |
| 保护 | 拒绝禁用/删除最后一个管理员（409） |
| 会话 | 密钥删除/禁用/轮换后旧会话立即 401；伪造 Cookie 401 |

---

## 9. 备份、恢复与升级

| 场景 | 结果 |
| --- | --- |
| 停机冷备（9 桶 / 18 对象 / 32.5 MB → 66 MB tar.gz） | 恢复到新卷后快照（桶、对象、ETag、分片任务、密钥）**与备份时逐字节一致**；26 个对象体的 MD5 汇总一致 |
| 在线热备（并发写入中 tar，112 MB） | 实例可正常启动、元数据库无损坏、**0 个「引用存在但文件缺失」**；但带入 71 个未引用 blob、丢失快照后提交的写入（本次 117/180 完成） |
| 原地升级 0.1.5 → 0.2.0 | 6 s 内 healthy；日志出现 `元数据库 schema 版本已更新为 v1（原 v0）`；升级前后快照 `diff` 为空 |
| 回滚 0.2.0 → 0.1.5 | 同一卷启动正常，快照仍与升级前一致（schema 未变，向前兼容） |
| 高版本库 | 拒绝启动（见第 6 节） |

> 在线热备的结论已写入 `docs/deployment.md`：可接受的数据回退而非损坏，但需要零丢失时应停机备份或使用 `sqlite3 .backup` + 文件系统快照。

`scripts/check_refs.py` 会比对「元数据库 BlobPath ↔ 磁盘 `*.bin`」，用于恢复后复核引用完整性。

---

## 10. 稳定性与可观测性

10 分钟、60 个采样点（每 10 s）：

| 指标 | 起始 | 结束 | 最小 | 最大 | 判断 |
| --- | --- | --- | --- | --- | --- |
| 进程 RSS | 179 256 KB | 182 216 KB | 179 256 KB | 186 704 KB | +1.6%，无泄漏 |
| cgroup anon | 61.3 MB | 62.2 MB | 61.1 MB | 66.6 MB | +1.5%，平稳 |
| 文件句柄 | 268 | 264 | 264 | 276 | 无增长 |
| 线程数 | 20 | 17 | 16 | 23 | 无增长 |
| WAL 大小 | 4.0 MB | 7.5 MB | 4.0 MB | 7.5 MB | 后期持平，checkpoint 正常回收 |

- 容器日志：`grep -ci 'error|exception|warn'` = **0**；
- CPU 稳定在 199%（2 核饱和），说明负载真实打满而服务未退化；
- 可观测性入口：`/admin/api/overview`（桶/对象/字节数、活跃分片、磁盘、进程版本、uptime、工作集、请求指标）、
  `/minio/health/live`、`/minio/health/ready`、每条响应带 `x-amz-request-id`。

---

## 11. 发现的问题与处理

| # | 级别 | 现象 | 根因 | 处理 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 1 | **P0** | 4 KB PUT 吞吐随并发下降，4 MB P50 达 7 s；磁盘写放大 5 倍 | `PRAGMA synchronous/busy_timeout` 只对启动连接生效，连接池新连接仍 `FULL` | `SqlitePragmaInterceptor` 每次连接打开时补齐 PRAGMA | 已修复 |
| 2 | **P1** | 并发覆写同 key 泄漏对象体：c=8 泄漏 121/200，c=32 泄漏 316/400 | 覆写路径「先写新 blob 再替换记录」，并发交错时旧 blob 无人删除 | `OrphanBlobSweeper` + 宽限期回收；实测 10150 → 12 个 blob、释放 1.45 GB | 已修复 |
| 3 | **P1** | 深度嵌套 XML 使请求挂起 30 s（CPU 放大 DoS） | 反序列化前不做深度/规模约束 | 反序列化前 64 层深度护栏 | 已修复 |
| 4 | **P1** | 删除/禁用管理员密钥后，已签发会话仍可用满 12 小时 | Cookie 只校验签名与过期，不回查密钥状态 | Cookie 绑定 Secret 指纹 + 每请求回查密钥 | 已修复 |
| 5 | **P1** | 无法轮换 Secret；「删除 + 新建」后原桶对客户端不可见 | `PATCH` 只接受 `enabled`/`displayName`，而桶属主是 AccessKeyId | 支持 `secretAccessKey` 原地轮换 + 控制台按钮 | 已修复 |
| 6 | P2 | 20 KB 的 `x-amz-meta-*` 被接受并落库 | 元数据体积无上限 | 2 KiB 上限 + `MetadataTooLarge` | 已修复 |
| 7 | P2 | 桶名 `UPPER`、`double..dot` 被接受 | 校验规则宽松 | 拒绝大写与连续 `..` | 已修复 |
| 8 | P2 | schema 升级无保护：旧代码能开新库，新代码可能静默跑在旧结构上 | 仅用 `EnsureCreated` | `SchemaGuard`（`PRAGMA user_version`）：低版本按序升级、高版本拒绝启动 | 已修复 |
| 9 | P2 | 测试脚本不可重复执行（同名桶残留导致 409） | 脚本用固定桶名 | 改为随机后缀；预签名有效期用例放宽到 5 s 以适应数秒时钟偏差 | 已修复 |
| 10 | P2 | 192 MB 内存限额下 OOM Kill | 进程峰值 RSS ≈185 MB，限额过低 | 文档与 compose 给出 256 MB 下限 / 512 MB 建议值 | 已缓解（配置层） |
| 11 | P2 | 覆写频繁时磁盘放大明显：10 分钟 7.2 万次覆写后仍有 12 764 个孤儿文件（约 50 MB） | 宽限期 1 小时内不回收入口 | 配置可调（`OrphanGraceHours` / 间隔）；建议后续在覆写路径同临界区删除旧 blob | 待优化 |

---

## 12. 已知限制与上线建议

**已知限制（架构层面，需排期）**

1. **租户身份 = AccessKeyId**：无法重命名、无法给同一租户发第二把密钥、无法平滑迁移属主。
   建议引入 `Account` 实体（`OwnerId` 指向 Account，密钥挂在 Account 下）。
2. **SQLite 单写者**：4 KB PUT 天花板约 156 rps（2 vCPU）；无法多实例共享同一数据卷。
   需要横向扩展时应上 PostgreSQL 元数据 + 对象锁（Roadmap 中）。
3. **无桶配额/限速/审计日志**：单租户可写满磁盘；无法回答「谁在何时删了哪个对象」。
4. **在线备份非事务一致**：见第 9 节，需运维按文档执行停机备份或 `.backup` + 快照。
5. **管理端 CSP 对 style 使用 `unsafe-inline`**：影响有限，可在后续收紧。

**上线前建议**

- [ ] 部署时设置内存限额 ≥ 256 MB（推荐 512 MB～1 GB），CPU ≥ 2 核；
- [ ] 主机与客户端均启用 NTP：预签名 URL 的过期判定基于服务器时钟，数秒偏差即可让极短有效期的 URL 立刻失效；
- [ ] 把 `Storage:OrphanGraceHours` 与回收周期纳入容量规划（覆写型业务按 1 小时写入量预留空间）；
- [ ] 备份策略：每日停机冷备或 `sqlite3 .backup` + 快照；恢复后用 `check_refs.py` 复核；
- [ ] 监控：`/minio/health/ready`、`/admin/api/overview` 的 `uptimeSeconds`、`workingSetBytes`、磁盘可用量；
- [ ] 访问密钥按租户分发，管理员密钥只用于控制台与运维脚本。

---

## 13. 复现方式

```bash
# 1) 构建与部署（生产硬化参数）
docker build -t s3objectstorage:0.2.0 .
bash scripts/run_container.sh s3c 9500 s3objectstorage:0.2.0 s3c-data

# 2) 功能与协议
python scripts/smoke.py --host 127.0.0.1:9500 --access-key ... --secret ...
S3_ENDPOINT=127.0.0.1:9500 python scripts/capacity_test.py

# 3) 性能
curl -s "http://127.0.0.1:9500/bench/obj4k.bin?<presigned>"   # 预签名 URL
gcc -O2 -pthread -o loadgen scripts/loadgen.c
./loadgen -u "$URL" -t 8 -n 20000

# 4) 安全 / 权限
python scripts/security_probe.py
python scripts/key_permission_test.py

# 5) 备份恢复与引用校验
python3 scripts/snapshot.py 9500 "$ADMIN_TOKEN" baseline.json
python3 scripts/check_refs.py metadata.db blobs.txt

# 6) 长稳
bash scripts/soak.sh s3c 600 get.url put.url body4k.bin
```

脚本清单：`smoke.py`、`capacity_test.py`、`bench.py`、`loadgen.c`、`security_probe.py`、
`key_permission_test.py`、`presign.py`、`snapshot.py`、`check_refs.py`、`set_schema_version.py`、
`soak.sh`、`run_container.sh`。

---

## 14. 环境清理

测试结束后已移除全部测试容器（`s3c`、`s3c-restore`、`s3c-hot`、`s3c-future`、`s3disk`、`s3mem`）、
数据卷（`s3c-data`、`s3c-restore`、`s3c-hot`、`s3c-future`）与 0.1.1–0.1.6 中间镜像，
宿主仅保留最终镜像 `s3objectstorage:0.2.0`（如需彻底清理：`docker rmi s3objectstorage:0.2.0`）。

压测目录 `~/s3bench`、`~/s3objtest` 已删除，未在宿主留下常驻进程或定时任务。
