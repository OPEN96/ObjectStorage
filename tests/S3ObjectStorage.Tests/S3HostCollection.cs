namespace S3ObjectStorage.Tests;

/// <summary>
/// 所有集成测试共用一个 Kestrel 宿主与一份 SQLite 元数据。
/// 之前每个测试类各自 IClassFixture 会并行启动多个宿主，GetFreePort() 存在
/// 端口复用竞态，偶尔出现请求打到另一个实例、报 NoSuchBucket 的随机失败。
/// 各测试用例都使用随机桶名，共享宿主不会互相干扰。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class S3HostCollection : ICollectionFixture<S3TestHost>
{
    public const string Name = "s3-host";
}
