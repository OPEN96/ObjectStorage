namespace S3ObjectStorage.Domain.Entities;

/// <summary>
/// 进行中的分片上传任务。
/// </summary>
public sealed class MultipartUploadEntity
{
    public string UploadId { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string ContentType { get; set; } = "binary/octet-stream";

    public string? CacheControl { get; set; }

    public string? ContentDisposition { get; set; }

    public string? ContentEncoding { get; set; }

    public string? ContentLanguage { get; set; }

    public string? Expires { get; set; }

    public string? UserMetadataJson { get; set; }

    public string InitiatorId { get; set; } = string.Empty;

    public string OwnerId { get; set; } = string.Empty;

    public DateTimeOffset InitiatedAt { get; set; }

    public List<UploadedPartEntity> Parts { get; set; } = [];
}
