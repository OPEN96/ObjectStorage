namespace S3ObjectStorage.Domain.Entities;

/// <summary>
/// 对象元数据实体。
/// </summary>
public sealed class ObjectEntity
{
    /// <summary>内部主键。</summary>
    public Guid Id { get; set; }

    public string BucketName { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>
    /// S3 ETag：单 PUT 为内容 MD5 十六进制；分片上传为 "md5-of-part-md5s-N"。
    /// </summary>
    public string ETag { get; set; } = string.Empty;

    public string ContentType { get; set; } = "binary/octet-stream";

    public string? ContentEncoding { get; set; }

    public string? CacheControl { get; set; }

    public string? ContentDisposition { get; set; }

    public string? ContentLanguage { get; set; }

    public string? Expires { get; set; }

    public string StorageClass { get; set; } = "STANDARD";

    /// <summary>用户自定义元数据（x-amz-meta-*），JSON 序列化存储。</summary>
    public string? UserMetadataJson { get; set; }

    /// <summary>除标准头以外的其它 x-amz-* 头，JSON 序列化存储。</summary>
    public string? CustomAmzHeadersJson { get; set; }

    public DateTimeOffset LastModified { get; set; }

    /// <summary>对象体在本地文件系统中的相对路径。</summary>
    public string BlobPath { get; set; } = string.Empty;
}
