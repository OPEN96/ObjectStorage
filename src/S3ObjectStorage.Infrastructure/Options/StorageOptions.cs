namespace S3ObjectStorage.Infrastructure.Options;

/// <summary>
/// 存储引擎配置。
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>数据根目录：元数据 SQLite 库与对象体都放在这里。</summary>
    public string RootPath { get; set; } = Path.Combine("data", "objectstorage");

    /// <summary>元数据数据库连接串，留空则使用 RootPath 下的 metadata.db。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>是否校验 x-amz-content-sha256；生产环境建议保持 true。</summary>
    public bool VerifyPayloadHash { get; set; } = true;

    /// <summary>单 PUT 上传的最大体积（字节），0 表示不限制。</summary>
    public long MaxSinglePutBytes { get; set; }

    /// <summary>上传时硬盘写缓冲大小。</summary>
    public int BufferSize { get; set; } = 128 * 1024;

    /// <summary>是否启用孤儿对象体回收（并发覆盖或崩溃会留下未被引用的 blob）。</summary>
    public bool OrphanSweepEnabled { get; set; } = true;

    /// <summary>孤儿回收的执行间隔（小时）。</summary>
    public int OrphanSweepIntervalHours { get; set; } = 6;

    /// <summary>启动后首次回收的延迟（秒），留出建库与预热时间。</summary>
    public int OrphanSweepInitialDelaySeconds { get; set; } = 60;

    /// <summary>宽限期（小时）：只删除超过该时长且仍未被引用的 blob，保护正在上传的文件。</summary>
    public int OrphanGraceHours { get; set; } = 1;

    /// <summary>启用回收的最大对象数上限，超过则跳过整轮扫描以控制内存占用。</summary>
    public int OrphanSweepMaxObjects { get; set; } = 500_000;
}
