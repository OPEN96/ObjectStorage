namespace S3ObjectStorage.Domain.Entities;

/// <summary>
/// 桶实体。
/// </summary>
public sealed class BucketEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>拥有者的 AccessKeyId。</summary>
    public string OwnerId { get; set; } = string.Empty;

    public string OwnerDisplayName { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public long ObjectCount { get; set; }

    public long TotalSize { get; set; }
}
