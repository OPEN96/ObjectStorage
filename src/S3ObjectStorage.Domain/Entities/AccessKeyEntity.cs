namespace S3ObjectStorage.Domain.Entities;

/// <summary>
/// 访问密钥（Access Key）。
/// </summary>
public sealed class AccessKeyEntity
{
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
