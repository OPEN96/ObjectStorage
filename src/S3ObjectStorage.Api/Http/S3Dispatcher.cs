using S3ObjectStorage.Api.Http;
using S3ObjectStorage.Api.Operations;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Routing;

namespace S3ObjectStorage.Api;

/// <summary>
/// S3 只有一个 URL 空间，行为由「路径 + HTTP 方法 + 查询子资源」共同决定，
/// 因此这里用一个终端处理器统一分发，而不是传统的路由到多个 Controller。
/// </summary>
public static class S3Dispatcher
{
    /// <summary>当前版本尚未实现的子资源，返回 501 而不是被误判成列举对象。</summary>
    private static readonly HashSet<string> UnsupportedSubresources = new(StringComparer.OrdinalIgnoreCase)
    {
        "acl", "policy", "cors", "lifecycle", "versioning", "replication", "encryption", "tagging",
        "logging", "website", "notification", "requestPayment", "analytics", "inventory", "metrics",
        "ownershipControls", "publicAccessBlock", "object-lock", "legal-hold", "retention", "torrent",
        "select", "accelerate", "status", "corsStatus", "versions", "torrentStatus", "websiteStatus",
    };

    private static readonly HashSet<string> SupportedSubresources = new(StringComparer.OrdinalIgnoreCase)
    {
        "prefix", "delimiter", "max-keys", "marker", "continuation-token", "start-after", "list-type",
        "uploads", "upload-id-marker", "key-marker", "max-uploads", "location", "uploadId", "partNumber",
        "part-number-marker", "max-parts", "delete", "versionId", "fetch-owner", "encoding-type",
    };

    public static async Task HandleAsync(HttpContext context)
    {
        var options = context.GetS3Options();
        var route = S3Routing.Resolve(context.Request, options);
        context.SetRoute(route);

        var operations = context.RequestServices.GetRequiredService<S3Operations>();
        var result = await ExecuteAsync(context, operations, route);
        await result.ExecuteAsync(context);
    }

    private static Task<IResult> ExecuteAsync(HttpContext context, S3Operations operations, S3Route route)
    {
        EnsureSupportedSubresources(context);

        if (route.IsServiceLevel)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                throw new S3Exception(S3ErrorCode.MethodNotAllowed);
            }

            return operations.ListBucketsAsync(context);
        }

        if (route.IsBucketLevel)
        {
            return DispatchBucketLevelAsync(context, operations);
        }

        return DispatchObjectLevelAsync(context, operations);
    }

    private static Task<IResult> DispatchBucketLevelAsync(HttpContext context, S3Operations operations)
    {
        var request = context.Request;
        var query = request.Query;

        if (HttpMethods.IsGet(request.Method))
        {
            if (query.ContainsKey("uploads"))
            {
                return operations.ListMultipartUploadsAsync(context);
            }

            if (query.ContainsKey("location"))
            {
                return operations.GetBucketLocationAsync(context);
            }

            return operations.ListObjectsAsync(context);
        }

        if (HttpMethods.IsHead(request.Method))
        {
            return operations.HeadBucketAsync(context);
        }

        if (HttpMethods.IsPut(request.Method))
        {
            return operations.CreateBucketAsync(context);
        }

        if (HttpMethods.IsDelete(request.Method))
        {
            return operations.DeleteBucketAsync(context);
        }

        if (HttpMethods.IsPost(request.Method) && query.ContainsKey("delete"))
        {
            return operations.DeleteObjectsAsync(context);
        }

        throw new S3Exception(S3ErrorCode.MethodNotAllowed);
    }

    private static Task<IResult> DispatchObjectLevelAsync(HttpContext context, S3Operations operations)
    {
        var request = context.Request;
        var query = request.Query;

        if (HttpMethods.IsGet(request.Method))
        {
            return query.ContainsKey("uploadId")
                ? operations.ListPartsAsync(context)
                : operations.GetObjectAsync(context);
        }

        if (HttpMethods.IsHead(request.Method))
        {
            return operations.HeadObjectAsync(context);
        }

        if (HttpMethods.IsPut(request.Method))
        {
            return query.ContainsKey("uploadId") || query.ContainsKey("partNumber")
                ? operations.UploadPartAsync(context)
                : operations.PutObjectAsync(context);
        }

        if (HttpMethods.IsDelete(request.Method))
        {
            return query.ContainsKey("uploadId")
                ? operations.AbortMultipartUploadAsync(context)
                : operations.DeleteObjectAsync(context);
        }

        if (HttpMethods.IsPost(request.Method))
        {
            if (query.ContainsKey("uploadId"))
            {
                return operations.CompleteMultipartUploadAsync(context);
            }

            if (query.ContainsKey("uploads"))
            {
                return operations.InitiateMultipartUploadAsync(context);
            }
        }

        throw new S3Exception(S3ErrorCode.MethodNotAllowed);
    }

    private static void EnsureSupportedSubresources(HttpContext context)
    {
        foreach (var parameter in context.Request.Query.Keys)
        {
            if (parameter.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
                || parameter.StartsWith("response-", StringComparison.OrdinalIgnoreCase)
                || SupportedSubresources.Contains(parameter))
            {
                continue;
            }

            if (UnsupportedSubresources.Contains(parameter))
            {
                throw new S3Exception(S3ErrorCode.NotImplemented, $"暂不支持该子资源：{parameter}", key: parameter);
            }

            throw new S3Exception(S3ErrorCode.NotImplemented, $"未知的查询参数：{parameter}", key: parameter);
        }
    }
}
