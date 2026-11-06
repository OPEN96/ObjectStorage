# 第三方组件与许可证

本项目以 [MIT](LICENSE) 发布。运行时与测试依赖如下，均为与 MIT 兼容的宽松许可证。

## 运行时依赖

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| [.NET / ASP.NET Core](https://github.com/dotnet/aspnetcore) | 10.0 | MIT | 运行时与 Web 主机 |
| [Entity Framework Core](https://github.com/dotnet/efcore) | 10.0.0 | MIT | ORM |
| [Microsoft.EntityFrameworkCore.Sqlite](https://github.com/dotnet/efcore) | 10.0.0 | MIT | SQLite 元数据存储 |
| [Microsoft.Extensions.*](https://github.com/dotnet/runtime) | 10.0.0 | MIT | 配置、DI、日志 |

## 测试与开发依赖（不随产物分发）

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| [AWSSDK.S3](https://github.com/aws/aws-sdk-net) | 4.0.104.7 | Apache-2.0 | 官方 SDK 集成测试 |
| [Minio .NET SDK](https://github.com/minio/minio-dotnet) | 6.0.4 | Apache-2.0 | MinIO 兼容性测试 |
| [Microsoft.AspNetCore.Mvc.Testing](https://github.com/dotnet/aspnetcore) | 10.0.14 | MIT | 集成测试宿主 |
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 | 测试框架 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 3.1.4 | Apache-2.0 | 测试适配器 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.14.1 | MIT | 测试宿主 |
| [coverlet](https://github.com/coverlet-coverage/coverlet) | 6.0.4 | MIT | 覆盖率收集 |

## 可选的外部工具（不随仓库分发）

- [MinIO Client (mc)](https://github.com/minio/mc)：Apache-2.0，用于 `scripts/mc_compat.sh`。
- [minio-py](https://github.com/minio/minio-py)：Apache-2.0，用于 `scripts/minio_compat.py`。

如果你再分发本项目的容器镜像，请同时保留上述组件各自的许可证声明。
