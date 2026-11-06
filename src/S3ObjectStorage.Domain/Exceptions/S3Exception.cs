namespace S3ObjectStorage.Domain.Exceptions;

/// <summary>
/// 表示需要以 S3 XML 错误文档形式返回给客户端的业务异常。
/// </summary>
public sealed class S3Exception : Exception
{
    public S3Exception(S3ErrorCode errorCode, string? message = null, string? requestId = null, string? bucketName = null, string? key = null)
        : base(message ?? errorCode.Message)
    {
        ErrorCode = errorCode;
        RequestId = requestId;
        BucketName = bucketName;
        Key = key;
    }

    public S3ErrorCode ErrorCode { get; }

    public string? RequestId { get; set; }

    public string? BucketName { get; set; }

    /// <summary>S3 错误响应里的 ArgumentName。</summary>
    public string? Key { get; set; }

    public int StatusCode => ErrorCode.StatusCode;

    public IDictionary<string, string> ExtraHeaders { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public static S3Exception NotFound() => new(S3ErrorCode.NoSuchKey);

    public static S3Exception NoSuchBucket() => new(S3ErrorCode.NoSuchBucket);

    public static S3Exception AccessDenied() => new(S3ErrorCode.AccessDenied);

    public static S3Exception SignatureDoesNotMatch(string? requestId = null) =>
        new(S3ErrorCode.SignatureDoesNotMatch, requestId: requestId);
}
