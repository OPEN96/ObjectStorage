namespace S3ObjectStorage.Domain.Entities;

/// <summary>
/// 已上传的分片。
/// </summary>
public sealed class UploadedPartEntity
{
    public long Id { get; set; }

    public string UploadId { get; set; } = string.Empty;

    public int PartNumber { get; set; }

    public long Size { get; set; }

    /// <summary>该分片内容的 MD5 十六进制，用于计算最终 ETag。</summary>
    public string ETag { get; set; } = string.Empty;

    public DateTimeOffset LastModified { get; set; }

    public string BlobPath { get; set; } = string.Empty;

    public MultipartUploadEntity? Upload { get; set; }
}
