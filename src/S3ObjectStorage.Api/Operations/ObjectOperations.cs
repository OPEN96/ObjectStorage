using System.Globalization;
using System.Security.Cryptography;
using S3ObjectStorage.Api.Http;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Results;
using S3ObjectStorage.S3Protocol.Xml;

namespace S3ObjectStorage.Api.Operations;

public sealed partial class S3Operations
{
    public Task<IResult> HeadObjectAsync(HttpContext context) => RetrieveObjectAsync(context, writeBody: false);

    public Task<IResult> GetObjectAsync(HttpContext context) => RetrieveObjectAsync(context, writeBody: true);

    private async Task<IResult> RetrieveObjectAsync(HttpContext context, bool writeBody)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var key = context.GetRoute().ObjectKey!;
        EnsureValidObjectKey(key);

        var principal = context.GetPrincipal();
        var entity = await objects.GetAsync(bucket.Name, key, context.RequestAborted)
            ?? throw new S3Exception(S3ErrorCode.NoSuchKey, requestId: context.GetRequestId(), key: key);

        if (!principal.IsAdmin && !string.Equals(bucket.OwnerId, principal.OwnerId, StringComparison.Ordinal))
        {
            throw S3Exception.AccessDenied();
        }

        var conditional = CheckConditionalHeaders(context.Request, entity);
        if (conditional is not null)
        {
            return conditional;
        }

        WriteObjectHeaders(context.Response, entity, includeMetadata: true);
        ApplyResponseHeaderOverrides(context, entity);

        var size = entity.Size;
        var start = 0L;
        var end = size - 1;

        var rangeHeader = context.Request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            var range = TryParseRange(rangeHeader, size);
            if (range is null)
            {
                context.Response.Headers["Content-Range"] = FormattableString.Invariant($"bytes */{size}");
                throw new S3Exception(S3ErrorCode.InvalidRange, requestId: context.GetRequestId(), key: key);
            }

            start = range.Value.Start;
            end = range.Value.End;
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers["Content-Range"] = FormattableString.Invariant($"bytes {start}-{end}/{size}");
        }

        context.Response.ContentLength = Math.Max(0, end - start + 1);

        if (!writeBody || size == 0)
        {
            // 这里绝不能再用「写入 Content-Length: 0」的结果，
            // 否则 HEAD 会把真实对象大小覆盖成 0，Range 请求也会丢掉 206。
            return S3Results.Completed();
        }

        try
        {
            var stream = await blobs.OpenReadAsync(entity.BlobPath, start, end, context.RequestAborted);
            await WriteBodyAsync(context.Response, stream, context.RequestAborted);
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError(ex, "对象数据缺失 bucket={Bucket} key={Key} blobPath={BlobPath}", entity.BucketName, entity.Key, entity.BlobPath);
            throw new S3Exception(S3ErrorCode.NoSuchKey, key: key);
        }

        return S3Results.Completed();
    }

    private static void ApplyResponseHeaderOverrides(HttpContext context, ObjectEntity entity)
    {
        var query = context.Request.Query;

        var contentType = query["response-content-type"].ToString();
        context.Response.Headers["Content-Type"] = string.IsNullOrEmpty(contentType)
            ? string.IsNullOrEmpty(entity.ContentType) ? "application/octet-stream" : entity.ContentType
            : contentType;

        void Apply(string parameter, string header)
        {
            var value = query[parameter].ToString();
            if (!string.IsNullOrEmpty(value))
            {
                context.Response.Headers[header] = value;
            }
        }

        Apply("response-content-language", "Content-Language");
        Apply("response-expires", "Expires");
        Apply("response-cache-control", "Cache-Control");
        Apply("response-content-disposition", "Content-Disposition");
        Apply("response-content-encoding", "Content-Encoding");
    }

    public async Task<IResult> PutObjectAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var key = context.GetRoute().ObjectKey!;
        EnsureValidObjectKey(key);

        if (context.Request.Headers.TryGetValue("x-amz-copy-source", out var copySource))
        {
            return await CopyObjectAsync(context, bucket, key, copySource.ToString(), context.RequestAborted);
        }

        if (context.Request.ContentLength is null
            && !context.Request.Headers.ContainsKey("Transfer-Encoding")
            && context.Request.Headers["X-Amz-Decoded-Content-Length"].ToString() is { Length: 0 })
        {
            throw new S3Exception(S3ErrorCode.MissingContentLength);
        }

        var declaredSize = ResolveDeclaredUploadSize(context);
        if (storageOptions.MaxSinglePutBytes > 0 && declaredSize > storageOptions.MaxSinglePutBytes)
        {
            throw new S3Exception(S3ErrorCode.EntityTooLarge);
        }

        var previous = await objects.GetAsync(bucket.Name, key, context.RequestAborted);

        var writeResult = await blobs.WriteAsync(GetRequestBody(context), context.RequestAborted);
        VerifyPayloadIntegrity(context, writeResult);

        var entity = new ObjectEntity
        {
            Id = Guid.NewGuid(),
            BucketName = bucket.Name,
            Key = key,
            Size = writeResult.Size,
            ETag = writeResult.ContentMd5Hex,
            ContentType = string.IsNullOrEmpty(context.Request.ContentType) ? "application/octet-stream" : context.Request.ContentType,
            ContentEncoding = StripAwsChunkedEncoding(context.Request.Headers["Content-Encoding"].ToString()),
            CacheControl = NullIfEmpty(context.Request.Headers["Cache-Control"].ToString()),
            ContentDisposition = NullIfEmpty(context.Request.Headers["Content-Disposition"].ToString()),
            ContentLanguage = NullIfEmpty(context.Request.Headers["Content-Language"].ToString()),
            Expires = NullIfEmpty(context.Request.Headers["Expires"].ToString()),
            StorageClass = "STANDARD",
            UserMetadataJson = SerializeMetadata(ExtractUserMetadata(context.Request)),
            CustomAmzHeadersJson = SerializeMetadata(ExtractCustomAmzHeaders(context.Request)),
            LastModified = DateTimeOffset.UtcNow,
            BlobPath = writeResult.BlobPath,
        };

        await objects.UpsertAsync(entity, context.RequestAborted);

        if (previous is not null && !string.Equals(previous.BlobPath, entity.BlobPath, StringComparison.Ordinal))
        {
            await blobs.DeleteAsync(previous.BlobPath, context.RequestAborted);
        }

        await UpdateBucketStatsAsync(bucket.Name, previous is null ? 1 : 0, entity.Size - (previous?.Size ?? 0), context.RequestAborted);

        logger.LogDebug("PUT {Bucket}/{Key} size={Size} etag={ETag}", bucket.Name, key, entity.Size, entity.ETag);

        return S3Results.Empty(
            StatusCodes.Status200OK,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ETag"] = EnsureQuoted(entity.ETag) });
    }

    public async Task<IResult> DeleteObjectAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var key = context.GetRoute().ObjectKey!;
        EnsureValidObjectKey(key);

        var entity = await objects.GetAsync(bucket.Name, key, context.RequestAborted);
        if (entity is null)
        {
            // S3 删除不存在的对象是幂等的，返回 204。
            return S3Results.Empty(StatusCodes.Status204NoContent);
        }

        await objects.DeleteAsync(bucket.Name, key, context.RequestAborted);
        await blobs.DeleteAsync(entity.BlobPath, context.RequestAborted);
        await UpdateBucketStatsAsync(bucket.Name, -1, -entity.Size, context.RequestAborted);

        return S3Results.Empty(StatusCodes.Status204NoContent);
    }

    public async Task<IResult> DeleteObjectsAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);

        if (context.Request.ContentLength is null or 0)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML);
        }

        const int maxDeleteBodyBytes = 1024 * 1024;
        if (context.Request.ContentLength > maxDeleteBodyBytes)
        {
            throw new S3Exception(S3ErrorCode.EntityTooLarge, "批量删除请求体不得超过 1 MiB。");
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxDeleteBodyBytes)
            {
                throw new S3Exception(S3ErrorCode.EntityTooLarge, "批量删除请求体不得超过 1 MiB。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }

        buffer.Position = 0;

        var declaredMd5 = context.Request.Headers["Content-MD5"].ToString();
        if (!string.IsNullOrEmpty(declaredMd5))
        {
            var actual = Convert.ToBase64String(MD5.HashData(buffer.ToArray()));
            if (!string.Equals(actual, declaredMd5, StringComparison.Ordinal))
            {
                throw new S3Exception(S3ErrorCode.BadDigest);
            }
        }

        buffer.Position = 0;
        DeleteObjectsRequest request;
        try
        {
            request = S3Xml.Deserialize<DeleteObjectsRequest>(buffer);
        }
        catch (System.Xml.XmlException)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML);
        }

        if (request.Objects.Count > 1000)
        {
            throw new S3Exception(S3ErrorCode.InvalidRequest, "单次批量删除最多支持 1000 个对象。");
        }

        var result = new DeleteResult();
        var freedSize = 0L;
        var removedCount = 0;

        foreach (var identifier in request.Objects)
        {
            var entity = await objects.GetAsync(bucket.Name, identifier.Key, context.RequestAborted);
            if (entity is not null)
            {
                await objects.DeleteAsync(bucket.Name, identifier.Key, context.RequestAborted);
                await blobs.DeleteAsync(entity.BlobPath, context.RequestAborted);
                freedSize += entity.Size;
                removedCount++;
            }

            result.Deleted.Add(new DeleteResult.DeletedObject { Key = identifier.Key });
        }

        await UpdateBucketStatsAsync(bucket.Name, -removedCount, -freedSize, context.RequestAborted);

        if (request.Quiet)
        {
            result.Deleted.Clear();
        }

        return S3Results.Xml(result);
    }

    private async Task<IResult> CopyObjectAsync(HttpContext context, BucketEntity destinationBucket, string destinationKey, string rawSource, CancellationToken cancellationToken)
    {
        var source = rawSource.TrimStart('/');
        var queryIndex = source.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            source = source[..queryIndex];
        }

        source = Uri.UnescapeDataString(source);
        var slash = source.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "x-amz-copy-source 必须为 bucket/key");
        }

        var sourceBucketName = source[..slash];
        var sourceKey = source[(slash + 1)..];

        var sourceBucket = await buckets.GetAsync(sourceBucketName, cancellationToken)
            ?? throw new S3Exception(S3ErrorCode.NoSuchBucket, bucketName: sourceBucketName);

        var principal = context.GetPrincipal();
        if (!principal.IsAdmin && !string.Equals(sourceBucket.OwnerId, principal.OwnerId, StringComparison.Ordinal))
        {
            throw S3Exception.AccessDenied();
        }

        var sourceObject = await objects.GetAsync(sourceBucketName, sourceKey, cancellationToken)
            ?? throw new S3Exception(S3ErrorCode.NoSuchKey, key: sourceKey);

        EnsureValidObjectKey(destinationKey);

        var previous = await objects.GetAsync(destinationBucket.Name, destinationKey, cancellationToken);
        await using var sourceStream = await blobs.OpenReadAsync(sourceObject.BlobPath, cancellationToken: cancellationToken);
        var writeResult = await blobs.WriteAsync(sourceStream, cancellationToken);

        var metadataDirective = context.Request.Headers["x-amz-metadata-directive"].ToString();
        var replaceMetadata = string.Equals(metadataDirective, "REPLACE", StringComparison.OrdinalIgnoreCase);

        var copied = new ObjectEntity
        {
            Id = Guid.NewGuid(),
            BucketName = destinationBucket.Name,
            Key = destinationKey,
            Size = writeResult.Size,
            ETag = writeResult.ContentMd5Hex,
            ContentType = replaceMetadata
                ? context.Request.ContentType ?? sourceObject.ContentType
                : sourceObject.ContentType,
            ContentEncoding = replaceMetadata
                ? StripAwsChunkedEncoding(context.Request.Headers["Content-Encoding"].ToString())
                : sourceObject.ContentEncoding,
            CacheControl = replaceMetadata
                ? NullIfEmpty(context.Request.Headers["Cache-Control"].ToString())
                : sourceObject.CacheControl,
            ContentDisposition = replaceMetadata
                ? NullIfEmpty(context.Request.Headers["Content-Disposition"].ToString())
                : sourceObject.ContentDisposition,
            ContentLanguage = replaceMetadata
                ? NullIfEmpty(context.Request.Headers["Content-Language"].ToString())
                : sourceObject.ContentLanguage,
            Expires = replaceMetadata
                ? NullIfEmpty(context.Request.Headers["Expires"].ToString())
                : sourceObject.Expires,
            StorageClass = sourceObject.StorageClass,
            UserMetadataJson = replaceMetadata
                ? SerializeMetadata(ExtractUserMetadata(context.Request))
                : sourceObject.UserMetadataJson,
            CustomAmzHeadersJson = replaceMetadata
                ? SerializeMetadata(ExtractCustomAmzHeaders(context.Request))
                : sourceObject.CustomAmzHeadersJson,
            LastModified = DateTimeOffset.UtcNow,
            BlobPath = writeResult.BlobPath,
        };

        await objects.UpsertAsync(copied, cancellationToken);

        if (previous is not null)
        {
            await blobs.DeleteAsync(previous.BlobPath, cancellationToken);
        }

        await UpdateBucketStatsAsync(destinationBucket.Name, previous is null ? 1 : 0, copied.Size - (previous?.Size ?? 0), cancellationToken);

        return S3Results.Xml(new CopyObjectResult
        {
            LastModified = S3Xml.ToS3Timestamp(copied.LastModified),
            ETag = EnsureQuoted(copied.ETag),
        });
    }

    private static long ResolveDeclaredUploadSize(HttpContext context)
    {
        var decodedLength = context.Request.Headers["X-Amz-Decoded-Content-Length"].ToString();
        if (long.TryParse(decodedLength, CultureInfo.InvariantCulture, out var decoded) && decoded > 0)
        {
            return decoded;
        }

        return context.Request.ContentLength ?? 0;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
