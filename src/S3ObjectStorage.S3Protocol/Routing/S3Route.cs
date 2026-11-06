namespace S3ObjectStorage.S3Protocol.Routing;

/// <summary>
/// 单次请求的路由解析结果。
/// </summary>
/// <param name="BucketName">桶名，位于 <c>/</c> 根时为 null。</param>
/// <param name="ObjectKey">对象 key，位于桶级别时为 null。</param>
/// <param name="IsVirtualHostStyle">桶名是否来自 Host 头。</param>
public sealed record S3Route(string? BucketName, string? ObjectKey, bool IsVirtualHostStyle)
{
    public bool IsServiceLevel => BucketName is null;

    public bool IsBucketLevel => BucketName is not null && ObjectKey is null;
}

/// <summary>
/// 已鉴权的调用者身份。
/// </summary>
public sealed record S3Principal(string AccessKeyId, string OwnerId, string DisplayName, bool IsAdmin)
{
    public static readonly S3Principal Anonymous = new("anonymous", "anonymous", "anonymous", false);
}
