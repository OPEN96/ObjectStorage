using System.Globalization;
using S3ObjectStorage.Api.Http;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Results;
using S3ObjectStorage.S3Protocol.Routing;
using S3ObjectStorage.S3Protocol.Xml;

namespace S3ObjectStorage.Api.Operations;

public sealed partial class S3Operations
{
    public async Task<IResult> ListBucketsAsync(HttpContext context)
    {
        var principal = context.GetPrincipal();
        var ownedBuckets = await buckets.ListByOwnerAsync(principal.OwnerId, context.RequestAborted);

        var model = new ListAllMyBucketsResult
        {
            Owner = new Owner { Id = principal.OwnerId, DisplayName = principal.DisplayName },
            Buckets = ownedBuckets
                .Select(b => new ListAllMyBucketsResult.BucketItem
                {
                    Name = b.Name,
                    CreationDate = S3Xml.ToS3Timestamp(b.CreatedAt),
                })
                .ToList(),
        };

        return S3Results.Xml(model);
    }

    public async Task<IResult> HeadBucketAsync(HttpContext context)
    {
        await RequireBucketAsync(context, context.RequestAborted);
        return S3Results.Empty(StatusCodes.Status200OK);
    }

    public async Task<IResult> GetBucketLocationAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        return S3Results.Xml(new LocationConstraint { Value = bucket.Region });
    }

    public async Task<IResult> CreateBucketAsync(HttpContext context)
    {
        var principal = context.GetPrincipal();
        var name = context.GetRoute().BucketName!;

        if (!BucketNameValidator.IsValid(name))
        {
            throw new S3Exception(S3ErrorCode.InvalidBucketName, bucketName: name);
        }

        var existing = await buckets.GetAsync(name, context.RequestAborted);
        if (existing is not null)
        {
            throw new S3Exception(
                string.Equals(existing.OwnerId, principal.OwnerId, StringComparison.Ordinal)
                    ? S3ErrorCode.BucketAlreadyOwnedByYou
                    : S3ErrorCode.BucketAlreadyExists,
                bucketName: name);
        }

        if (context.Request.ContentLength > 0)
        {
            try
            {
                var configuration = await S3Xml.DeserializeAsync<CreateBucketConfiguration>(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);
                if (!string.IsNullOrEmpty(configuration.LocationConstraint)
                    && serverOptions.EnforceRegion
                    && !string.Equals(configuration.LocationConstraint, serverOptions.Region, StringComparison.Ordinal))
                {
                    throw new S3Exception(S3ErrorCode.InvalidLocationConstraint);
                }
            }
            catch (System.Xml.XmlException)
            {
                throw new S3Exception(S3ErrorCode.MalformedXML);
            }
        }

        await buckets.CreateAsync(new BucketEntity
        {
            Name = name,
            OwnerId = principal.OwnerId,
            OwnerDisplayName = principal.DisplayName,
            Region = serverOptions.Region,
            CreatedAt = DateTimeOffset.UtcNow,
        }, context.RequestAborted);

        logger.LogInformation("创建桶 {Bucket} (owner={Owner})", name, principal.OwnerId);

        return S3Results.Empty(StatusCodes.Status200OK, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Location"] = "/" + name,
        });
    }

    public async Task<IResult> DeleteBucketAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var usage = await objects.GetBucketUsageAsync(bucket.Name, context.RequestAborted);

        if (usage.ObjectCount > 0)
        {
            throw new S3Exception(S3ErrorCode.BucketNotEmpty, bucketName: bucket.Name);
        }

        await buckets.DeleteAsync(bucket.Name, context.RequestAborted);
        logger.LogInformation("删除桶 {Bucket}", bucket.Name);

        return S3Results.Empty(StatusCodes.Status204NoContent);
    }

    public async Task<IResult> ListObjectsAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var query = context.Request.Query;

        var isVersion2 = query["list-type"].ToString() == "2";
        // MinIO 客户端（mc / minio-go / minio-py / minio-dotnet）列举时会带 encoding-type=url，
        // 要求响应中的 key、prefix 等字段做百分号编码，否则含 '+'、空格、非 ASCII 的 key 会被解码错。
        var encodeKeys = S3UrlEncoding.IsRequested(query["encoding-type"].ToString());
        // mc / MinIO SDK 的 ListObjectsV2 会带 fetch-owner=true，期望 Contents 里带 Owner。
        var fetchOwner = isVersion2
            && string.Equals(query["fetch-owner"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        var principal = fetchOwner ? context.GetPrincipal() : null;
        var prefix = query["prefix"].ToString();
        var delimiter = query["delimiter"].ToString();
        var maxKeysText = query["max-keys"].ToString();
        var maxKeys = string.IsNullOrEmpty(maxKeysText) || !int.TryParse(maxKeysText, CultureInfo.InvariantCulture, out var parsed) ? 1000 : parsed;

        var continuationToken = query["continuation-token"].ToString();
        var startAfter = query["start-after"].ToString();
        var marker = query["marker"].ToString();

        var page = await objects.ListV2Async(
            bucket.Name,
            string.IsNullOrEmpty(prefix) ? null : prefix,
            string.IsNullOrEmpty(delimiter) ? null : delimiter,
            string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
            !string.IsNullOrEmpty(startAfter) ? startAfter : string.IsNullOrEmpty(marker) ? null : marker,
            maxKeys,
            context.RequestAborted);

        var model = new ListBucketResult
        {
            Name = bucket.Name,
            Prefix = S3UrlEncoding.Encode(string.IsNullOrEmpty(prefix) ? null : prefix, encodeKeys),
            Delimiter = S3UrlEncoding.Encode(string.IsNullOrEmpty(delimiter) ? null : delimiter, encodeKeys),
            MaxKeys = maxKeys,
            KeyCount = isVersion2 ? page.Objects.Count + page.CommonPrefixes.Count : 0,
            IsTruncated = page.IsTruncated,
            EncodingType = encodeKeys ? S3UrlEncoding.Url : null,
            // ContinuationToken 是 base64url 令牌，不是对象 key，不能参与 url 编码。
            ContinuationToken = string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
            NextContinuationToken = page.NextContinuationToken,
            StartAfter = S3UrlEncoding.Encode(
                isVersion2 && !string.IsNullOrEmpty(startAfter) ? startAfter : null, encodeKeys),
            Marker = S3UrlEncoding.Encode(
                isVersion2 || string.IsNullOrEmpty(marker) ? null : marker, encodeKeys),
            NextMarker = S3UrlEncoding.Encode(isVersion2 ? null : page.LastKey, encodeKeys),
            Contents = page.Objects
                .Select(o => new ObjectContents
                {
                    Key = S3UrlEncoding.EncodeValue(o.Key, encodeKeys),
                    LastModified = S3Xml.ToS3Timestamp(o.LastModified),
                    ETag = EnsureQuoted(o.ETag),
                    Size = o.Size,
                    StorageClass = o.StorageClass,
                    Owner = principal is null
                        ? null
                        : new Owner { Id = principal.OwnerId, DisplayName = principal.DisplayName },
                })
                .ToList(),
            CommonPrefixes = page.CommonPrefixes
                .Select(p => new CommonPrefix { Prefix = S3UrlEncoding.EncodeValue(p, encodeKeys) })
                .ToList(),
        };

        return S3Results.Xml(model);
    }

    public async Task<IResult> ListMultipartUploadsAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var query = context.Request.Query;

        var prefix = query["prefix"].ToString();
        var keyMarker = query["key-marker"].ToString();
        var uploadIdMarker = query["upload-id-marker"].ToString();
        var encodeKeys = S3UrlEncoding.IsRequested(query["encoding-type"].ToString());
        var maxUploadsText = query["max-uploads"].ToString();
        var maxUploads = string.IsNullOrEmpty(maxUploadsText) || !int.TryParse(maxUploadsText, CultureInfo.InvariantCulture, out var parsed) ? 1000 : parsed;

        var uploads = await multiparts.ListUploadsAsync(
            bucket.Name,
            string.IsNullOrEmpty(prefix) ? null : prefix,
            string.IsNullOrEmpty(keyMarker) ? null : keyMarker,
            string.IsNullOrEmpty(uploadIdMarker) ? null : uploadIdMarker,
            maxUploads,
            context.RequestAborted);

        var last = uploads.Count > 0 ? uploads[^1] : null;
        var isTruncated = uploads.Count >= maxUploads;

        var model = new ListMultipartUploadsResult
        {
            Bucket = bucket.Name,
            Prefix = S3UrlEncoding.Encode(string.IsNullOrEmpty(prefix) ? null : prefix, encodeKeys),
            MaxUploads = maxUploads,
            IsTruncated = isTruncated,
            EncodingType = encodeKeys ? S3UrlEncoding.Url : null,
            KeyMarker = S3UrlEncoding.Encode(string.IsNullOrEmpty(keyMarker) ? null : keyMarker, encodeKeys),
            UploadIdMarker = string.IsNullOrEmpty(uploadIdMarker) ? null : uploadIdMarker,
            NextKeyMarker = S3UrlEncoding.Encode(isTruncated ? last?.Key : null, encodeKeys),
            NextUploadIdMarker = isTruncated ? last?.UploadId : null,
            Uploads = uploads
                .Select(u => new ListMultipartUploadsResult.UploadItem
                {
                    Key = S3UrlEncoding.EncodeValue(u.Key, encodeKeys),
                    UploadId = u.UploadId,
                    StorageClass = "STANDARD",
                    Initiated = S3Xml.ToS3Timestamp(u.InitiatedAt),
                    Initiator = new Owner { Id = u.InitiatorId, DisplayName = u.InitiatorId },
                    Owner = new Owner { Id = u.OwnerId, DisplayName = u.OwnerId },
                })
                .ToList(),
        };

        return S3Results.Xml(model);
    }
}
