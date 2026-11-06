using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using S3ObjectStorage.Api.Http;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Options;
using S3ObjectStorage.S3Protocol.Results;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Api.Operations;

/// <summary>
/// S3 操作的共享上下文与工具方法。
/// 具体的桶 / 对象 / 分片操作在分部类的另外三个文件中。
/// </summary>
public sealed partial class S3Operations(
    Domain.Abstractions.IBucketStore buckets,
    Domain.Abstractions.IObjectStore objects,
    Domain.Abstractions.IMultipartStore multiparts,
    Domain.Abstractions.IBlobStore blobs,
    Microsoft.Extensions.Options.IOptions<Infrastructure.Options.StorageOptions> storageOptionsAccessor,
    Microsoft.Extensions.Options.IOptions<S3ServerOptions> serverOptionsAccessor,
    ILogger<S3Operations> logger)
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerOptions.Default);

    private readonly Infrastructure.Options.StorageOptions storageOptions = storageOptionsAccessor.Value;

    private readonly S3ServerOptions serverOptions = serverOptionsAccessor.Value;

    /// <summary>S3 规定的分片数量上限。</summary>
    private const int MaxPartNumber = 10_000;

    /// <summary>取出本次请求的 body 流；必要时用 aws-chunked 解码器包装。</summary>
    private static Stream GetRequestBody(HttpContext context)
    {
        var declared = context.GetDeclaredPayloadHash();
        return AwsChunkedBodyStream.Wrap(context.Request.Body, declared, context.GetSigV4Seed());
    }

    private async Task<BucketEntity> RequireBucketAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var route = context.GetRoute();
        var principal = context.GetPrincipal();
        var bucket = await buckets.GetAsync(route.BucketName!, cancellationToken)
            ?? throw new S3Exception(S3ErrorCode.NoSuchBucket, bucketName: route.BucketName);

        if (!principal.IsAdmin && !string.Equals(bucket.OwnerId, principal.OwnerId, StringComparison.Ordinal))
        {
            throw S3Exception.AccessDenied();
        }

        return bucket;
    }

    /// <summary>
    /// 取出分片上传任务，并确认它属于当前桶（以及当前对象 key）。
    /// 否则同实例的其它租户只要猜到 uploadId 就能续传、完成或终止别人的上传。
    /// </summary>
    private async Task<MultipartUploadEntity> RequireUploadAsync(
        string bucketName,
        string uploadId,
        string? key,
        CancellationToken cancellationToken)
    {
        var upload = await multiparts.GetAsync(uploadId, cancellationToken)
            ?? throw new S3Exception(S3ErrorCode.NoSuchUpload);

        if (!string.Equals(upload.BucketName, bucketName, StringComparison.Ordinal)
            || (key is not null && !string.Equals(upload.Key, key, StringComparison.Ordinal)))
        {
            // 不暴露「uploadId 存在但属于别人」，统一按不存在处理。
            throw new S3Exception(S3ErrorCode.NoSuchUpload);
        }

        return upload;
    }

    private void EnsureValidObjectKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new S3Exception(S3ErrorCode.InvalidRequest, "对象 key 不能为空。");
        }

        if (key.Length > serverOptions.MaxKeyLength)
        {
            throw new S3Exception(S3ErrorCode.KeyTooLongError);
        }
    }

    /// <summary>把 <c>x-amz-meta-*</c> 头收集成字典，序列化为 JSON 存库。AWS 限制：用户自定义元数据（键+值）总大小不超过 2 KiB。</summary>
    internal const int MaxUserMetadataBytes = 2048;

    private static Dictionary<string, string> ExtractUserMetadata(HttpRequest request)
    {
        const string prefix = "x-amz-meta-";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;

        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = header.Value.ToString();
                result[header.Key[prefix.Length..]] = value;
                totalBytes += System.Text.Encoding.UTF8.GetByteCount(header.Key) + System.Text.Encoding.UTF8.GetByteCount(value);
            }
        }

        // 元数据会被写入元数据库并在每次 GET/HEAD 时回显，必须有上限，
        // 否则可被用来放大存储占用与响应头体积。
        if (totalBytes > MaxUserMetadataBytes)
        {
            throw new S3Exception(S3ErrorCode.MetadataTooLarge, $"用户元数据 {totalBytes} 字节，超过 {MaxUserMetadataBytes} 字节上限");
        }

        return result;
    }

    /// <summary>需要随对象一起持久化的其它 x-amz-* 头（例如 x-amz-server-side-encryption）。</summary>
    private static Dictionary<string, string> ExtractCustomAmzHeaders(HttpRequest request)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "x-amz-content-sha256", "x-amz-date", "x-amz-acl", "x-amz-copy-source", "x-amz-request-id",
            // aws-chunked 的传输层头不应作为对象元数据回显给 GET / HEAD。
            "x-amz-trailer", "x-amz-decoded-content-length",
        };

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            if (!header.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                || ignored.Contains(header.Key))
            {
                continue;
            }

            result[header.Key] = header.Value.ToString();
        }

        return result;
    }

    private static string SerializeMetadata(IDictionary<string, string> metadata) =>
        JsonSerializer.Serialize(metadata, MetadataJsonOptions);

    private static Dictionary<string, string> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, MetadataJsonOptions)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把对象的标准响应头写回客户端。</summary>
    private static void WriteObjectHeaders(HttpResponse response, ObjectEntity entity, bool includeMetadata)
    {
        response.Headers["Last-Modified"] = entity.LastModified.ToString("R", CultureInfo.InvariantCulture);
        response.Headers["ETag"] = EnsureQuoted(entity.ETag);
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["x-amz-storage-class"] = entity.StorageClass;

        if (!string.IsNullOrEmpty(entity.ContentEncoding))
        {
            response.Headers["Content-Encoding"] = entity.ContentEncoding;
        }

        if (!string.IsNullOrEmpty(entity.CacheControl))
        {
            response.Headers["Cache-Control"] = entity.CacheControl;
        }

        if (!string.IsNullOrEmpty(entity.ContentDisposition))
        {
            response.Headers["Content-Disposition"] = entity.ContentDisposition;
        }

        if (!string.IsNullOrEmpty(entity.ContentLanguage))
        {
            response.Headers["Content-Language"] = entity.ContentLanguage;
        }

        if (!string.IsNullOrEmpty(entity.Expires))
        {
            response.Headers["Expires"] = entity.Expires;
        }

        if (!includeMetadata)
        {
            return;
        }

        foreach (var (name, value) in DeserializeMetadata(entity.UserMetadataJson))
        {
            response.Headers["x-amz-meta-" + name] = value;
        }

        foreach (var (name, value) in DeserializeMetadata(entity.CustomAmzHeadersJson))
        {
            response.Headers[name] = value;
        }
    }

    /// <summary>
    /// 从 Content-Encoding 中去掉 aws-chunked 传输编码。
    /// 客户端（尤其是 minio-go / mc）上传时会带 <c>Content-Encoding: aws-chunked</c>，
    /// 这只是 SigV4 流式负载的封装，不属于对象本身；若不剥离，GET 会把 aws-chunked
    /// 回给客户端，导致下载端误以为响应体仍是分块格式。
    /// </summary>
    private static string? StripAwsChunkedEncoding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.Equals(part, "aws-chunked", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string EnsureQuoted(string etag) =>
        etag.Length >= 2 && etag[0] == '"' && etag[^1] == '"' ? etag : "\"" + etag + "\"";

    private static string StripQuotes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    /// <summary>解析 <c>Range: bytes=start-end</c>，失败时返回 null（调用方应返回 416）。</summary>
    private static (long Start, long End)? TryParseRange(string? rangeHeader, long size)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var spec = rangeHeader["bytes=".Length..];
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return null;
        }

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];

        long start;
        long end;

        if (startText.Length == 0)
        {
            // bytes=-N：最后 N 个字节
            if (!long.TryParse(endText, CultureInfo.InvariantCulture, out var suffixLength) || suffixLength <= 0)
            {
                return null;
            }

            start = Math.Max(0, size - suffixLength);
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(startText, CultureInfo.InvariantCulture, out start))
            {
                return null;
            }

            end = endText.Length == 0
                ? size - 1
                : long.TryParse(endText, CultureInfo.InvariantCulture, out end) ? Math.Min(end, size - 1) : -1;
        }

        if (end >= size)
        {
            end = size - 1;
        }

        if (start > end || start >= size || size == 0)
        {
            return null;
        }

        return (start, end);
    }

    /// <summary>把对象体流式写入响应。</summary>
    private static async Task WriteBodyAsync(HttpResponse response, Stream source, CancellationToken cancellationToken)
    {
        const int bufferSize = 128 * 1024;
        await using (source)
        {
            await source.CopyToAsync(response.Body, bufferSize, cancellationToken).ConfigureAwait(false);
        }

        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验 If-Match / If-None-Match / If-Modified-Since / If-Unmodified-Since。</summary>
    private static IResult? CheckConditionalHeaders(HttpRequest request, ObjectEntity entity)
    {
        if (request.Headers.TryGetValue("If-Match", out var ifMatch)
            && !ifMatch.ToString().Split(',').Any(v => string.Equals(StripQuotes(v.Trim()), StripQuotes(entity.ETag), StringComparison.Ordinal) || v.Trim() == "*"))
        {
            return S3Results.Empty(StatusCodes.Status412PreconditionFailed);
        }

        if (request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch)
            && ifNoneMatch.ToString().Split(',').Any(v => string.Equals(StripQuotes(v.Trim()), StripQuotes(entity.ETag), StringComparison.Ordinal) || v.Trim() == "*"))
        {
            return S3Results.Empty(StatusCodes.Status304NotModified);
        }

        if (request.Headers.TryGetValue("If-Modified-Since", out var modifiedSince)
            && DateTimeOffset.TryParseExact(modifiedSince.ToString(), "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since)
            && since >= entity.LastModified.ToUniversalTime())
        {
            return S3Results.Empty(StatusCodes.Status304NotModified);
        }

        if (request.Headers.TryGetValue("If-Unmodified-Since", out var unmodifiedSince)
            && DateTimeOffset.TryParseExact(unmodifiedSince.ToString(), "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until)
            && until < entity.LastModified.ToUniversalTime())
        {
            return S3Results.Empty(StatusCodes.Status412PreconditionFailed);
        }

        return null;
    }

    /// <summary>校验 x-amz-content-sha256 / Content-MD5 与实际内容的一致性。</summary>
    private void VerifyPayloadIntegrity(HttpContext context, Domain.Abstractions.BlobWriteResult writeResult)
    {
        var declaredSha = context.GetDeclaredPayloadHash();

        if (storageOptions.VerifyPayloadHash
            && !string.IsNullOrEmpty(declaredSha)
            && declaredSha != SigV4.UnsignedPayload
            && !SigV4.IsStreamingPayload(declaredSha)
            && !string.Equals(declaredSha, writeResult.ContentSha256Hex, StringComparison.Ordinal))
        {
            throw new S3Exception(
                S3ErrorCode.XAmzContentSHA256Mismatch,
                $"declared={declaredSha} computed={writeResult.ContentSha256Hex} size={writeResult.Size}");
        }

        var contentMd5 = context.Request.Headers["Content-MD5"].ToString();
        if (string.IsNullOrEmpty(contentMd5))
        {
            return;
        }

        var expected = Convert.ToBase64String(Convert.FromHexString(writeResult.ContentMd5Hex));
        if (!string.Equals(expected, contentMd5, StringComparison.Ordinal))
        {
            throw new S3Exception(S3ErrorCode.BadDigest);
        }
    }

    /// <summary>更新桶统计（对象数 + 容量）。</summary>
    private async Task UpdateBucketStatsAsync(string bucketName, long objectCountDelta, long sizeDelta, CancellationToken cancellationToken)
    {

        try
        {
            await buckets.UpdateStatsAsync(bucketName, objectCountDelta, sizeDelta, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "更新桶 {Bucket} 统计信息失败", bucketName);
        }
    }

    private static string BuildLocation(HttpContext context, string bucketName, string? key)
    {
        var scheme = context.Request.Scheme;
        var host = context.Request.Host.Value;
        var path = string.IsNullOrEmpty(key) ? "/" + bucketName : "/" + bucketName + "/" + key;
        return scheme + "://" + host + path;
    }

    /// <summary>分片上传最终的 ETag：拼接各分片 MD5 二进制后再取 MD5。</summary>
    private static string ComputeMultipartEtag(IEnumerable<string> partEtagsHex)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var count = 0;
        foreach (var etag in partEtagsHex)
        {
            md5.AppendData(Convert.FromHexString(StripQuotes(etag)));
            count++;
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset()) + "-" + count.ToString(CultureInfo.InvariantCulture);
    }
}
