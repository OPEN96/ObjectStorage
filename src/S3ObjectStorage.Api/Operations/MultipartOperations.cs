using System.Globalization;
using S3ObjectStorage.Api.Http;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Results;
using S3ObjectStorage.S3Protocol.Xml;

namespace S3ObjectStorage.Api.Operations;

public sealed partial class S3Operations
{
    public async Task<IResult> InitiateMultipartUploadAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var key = context.GetRoute().ObjectKey!;
        EnsureValidObjectKey(key);

        var principal = context.GetPrincipal();
        var uploadId = Guid.NewGuid().ToString("N");

        await multiparts.CreateAsync(new MultipartUploadEntity
        {
            UploadId = uploadId,
            BucketName = bucket.Name,
            Key = key,
            ContentType = string.IsNullOrEmpty(context.Request.ContentType) ? "application/octet-stream" : context.Request.ContentType,
            ContentEncoding = StripAwsChunkedEncoding(context.Request.Headers["Content-Encoding"].ToString()),
            CacheControl = NullIfEmpty(context.Request.Headers["Cache-Control"].ToString()),
            ContentDisposition = NullIfEmpty(context.Request.Headers["Content-Disposition"].ToString()),
            ContentLanguage = NullIfEmpty(context.Request.Headers["Content-Language"].ToString()),
            UserMetadataJson = SerializeMetadata(ExtractUserMetadata(context.Request)),
            InitiatorId = principal.OwnerId,
            OwnerId = bucket.OwnerId,
            InitiatedAt = DateTimeOffset.UtcNow,
        }, context.RequestAborted);

        return S3Results.Xml(new InitiateMultipartUploadResult
        {
            Bucket = bucket.Name,
            Key = key,
            UploadId = uploadId,
        });
    }

    public async Task<IResult> UploadPartAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var query = context.Request.Query;

        var uploadId = query["uploadId"].ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "缺少 uploadId");
        }

        if (!int.TryParse(query["partNumber"].ToString(), CultureInfo.InvariantCulture, out var partNumber)
            || partNumber is < 1 or > MaxPartNumber)
        {
            throw new S3Exception(S3ErrorCode.InvalidPartNumber, key: "partNumber");
        }

        var upload = await RequireUploadAsync(bucket.Name, uploadId, context.GetRoute().ObjectKey, context.RequestAborted);

        var writeResult = await blobs.WriteToPartAsync(uploadId, partNumber, GetRequestBody(context), context.RequestAborted);

        var declaredMd5 = context.Request.Headers["Content-MD5"].ToString();
        if (!string.IsNullOrEmpty(declaredMd5))
        {
            var actual = Convert.ToBase64String(Convert.FromHexString(writeResult.ContentMd5Hex));
            if (!string.Equals(actual, declaredMd5.Trim(), StringComparison.Ordinal))
            {
                // 只回滚本次写入的分片，不能把同一 uploadId 下已成功的其它分片一起删掉。
                await multiparts.DeletePartAsync(uploadId, partNumber, context.RequestAborted);
                await blobs.DeletePartAsync(uploadId, partNumber, context.RequestAborted);
                throw new S3Exception(S3ErrorCode.BadDigest);
            }
        }

        await multiparts.AddOrReplacePartAsync(new UploadedPartEntity
        {
            UploadId = uploadId,
            PartNumber = partNumber,
            Size = writeResult.Size,
            ETag = writeResult.ContentMd5Hex,
            BlobPath = writeResult.BlobPath,
            LastModified = DateTimeOffset.UtcNow,
        }, context.RequestAborted);

        logger.LogDebug(
            "UploadPart bucket={Bucket} key={Key} uploadId={UploadId} part={Part} size={Size}",
            bucket.Name, upload.Key, uploadId, partNumber, writeResult.Size);

        return S3Results.Empty(
            StatusCodes.Status200OK,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ETag"] = EnsureQuoted(writeResult.ContentMd5Hex) });
    }

    public async Task<IResult> CompleteMultipartUploadAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var key = context.GetRoute().ObjectKey!;
        var uploadId = context.Request.Query["uploadId"].ToString();

        if (string.IsNullOrEmpty(uploadId))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "缺少 uploadId");
        }

        var upload = await RequireUploadAsync(bucket.Name, uploadId, key, context.RequestAborted);

        CompleteMultipartUploadRequest request;
        try
        {
            request = await S3Xml.DeserializeAsync<CompleteMultipartUploadRequest>(
                context.Request.Body,
                cancellationToken: context.RequestAborted);
        }
        catch (System.Xml.XmlException)
        {
            throw new S3Exception(S3ErrorCode.MalformedXML);
        }

        if (request.Parts.Count == 0)
        {
            throw new S3Exception(S3ErrorCode.InvalidRequest, "分片列表不能为空。");
        }

        var storedParts = upload.Parts.ToDictionary(p => p.PartNumber, p => p);
        var orderedParts = new List<UploadedPartEntity>(request.Parts.Count);
        var previousNumber = 0;

        foreach (var spec in request.Parts)
        {
            if (spec.PartNumber <= previousNumber)
            {
                throw new S3Exception(S3ErrorCode.InvalidPartOrder);
            }

            previousNumber = spec.PartNumber;

            if (!storedParts.TryGetValue(spec.PartNumber, out var stored))
            {
                throw new S3Exception(S3ErrorCode.InvalidPart);
            }

            if (!string.IsNullOrEmpty(spec.ETag)
                && !string.Equals(StripQuotes(spec.ETag), stored.ETag, StringComparison.OrdinalIgnoreCase))
            {
                throw new S3Exception(S3ErrorCode.InvalidPart);
            }

            orderedParts.Add(stored);
        }

        var (blobPath, size) = await blobs.ConcatPartsAsync(
            uploadId,
            orderedParts.Select(p => p.BlobPath),
            context.RequestAborted);

        var etag = ComputeMultipartEtag(orderedParts.Select(p => p.ETag));
        var previous = await objects.GetAsync(bucket.Name, key, context.RequestAborted);

        await objects.UpsertAsync(new ObjectEntity
        {
            Id = Guid.NewGuid(),
            BucketName = bucket.Name,
            Key = key,
            Size = size,
            ETag = etag,
            ContentType = upload.ContentType,
            ContentEncoding = upload.ContentEncoding,
            CacheControl = upload.CacheControl,
            ContentDisposition = upload.ContentDisposition,
            ContentLanguage = upload.ContentLanguage,
            StorageClass = "STANDARD",
            UserMetadataJson = upload.UserMetadataJson,
            LastModified = DateTimeOffset.UtcNow,
            BlobPath = blobPath,
        }, context.RequestAborted);

        await multiparts.DeleteAsync(uploadId, context.RequestAborted);
        await blobs.DeletePartsAsync(uploadId, context.RequestAborted);

        if (previous is not null)
        {
            await blobs.DeleteAsync(previous.BlobPath, context.RequestAborted);
        }

        await UpdateBucketStatsAsync(bucket.Name, previous is null ? 1 : 0, size - (previous?.Size ?? 0), context.RequestAborted);

        logger.LogInformation(
            "CompleteMultipart bucket={Bucket} key={Key} parts={Parts} size={Size}",
            bucket.Name, key, orderedParts.Count, size);

        return S3Results.Xml(new CompleteMultipartUploadResult
        {
            Location = BuildLocation(context, bucket.Name, key),
            Bucket = bucket.Name,
            Key = key,
            ETag = EnsureQuoted(etag),
        });
    }

    public async Task<IResult> AbortMultipartUploadAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var key = context.GetRoute().ObjectKey;

        var uploadId = context.Request.Query["uploadId"].ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "缺少 uploadId");
        }

        _ = await RequireUploadAsync(bucket.Name, uploadId, key, context.RequestAborted);

        await multiparts.DeleteAsync(uploadId, context.RequestAborted);
        await blobs.DeletePartsAsync(uploadId, context.RequestAborted);

        return S3Results.Empty(StatusCodes.Status204NoContent);
    }

    public async Task<IResult> ListPartsAsync(HttpContext context)
    {
        var bucket = await RequireBucketAsync(context, context.RequestAborted);
        var query = context.Request.Query;

        var uploadId = query["uploadId"].ToString();
        if (string.IsNullOrEmpty(uploadId))
        {
            throw new S3Exception(S3ErrorCode.InvalidArgument, "缺少 uploadId");
        }

        var upload = await RequireUploadAsync(
            bucket.Name,
            uploadId,
            context.GetRoute().ObjectKey,
            context.RequestAborted);

        var maxPartsText = query["max-parts"].ToString();
        var maxParts = string.IsNullOrEmpty(maxPartsText) || !int.TryParse(maxPartsText, CultureInfo.InvariantCulture, out var parsed)
            ? 1000
            : Math.Clamp(parsed, 1, 1000);

        var markerText = query["part-number-marker"].ToString();
        _ = int.TryParse(markerText, CultureInfo.InvariantCulture, out var marker);

        var allParts = upload.Parts
            .Where(p => p.PartNumber > marker)
            .OrderBy(p => p.PartNumber)
            .ToList();

        var page = allParts.Take(maxParts).ToList();
        var isTruncated = allParts.Count > maxParts;

        var model = new ListPartsResult
        {
            Bucket = bucket.Name,
            Key = upload.Key,
            UploadId = uploadId,
            MaxParts = maxParts,
            PartNumberMarker = marker,
            NextPartNumberMarker = isTruncated ? page[^1].PartNumber : 0,
            IsTruncated = isTruncated,
            StorageClass = "STANDARD",
            Initiator = new Owner { Id = upload.InitiatorId, DisplayName = upload.InitiatorId },
            Owner = new Owner { Id = upload.OwnerId, DisplayName = upload.OwnerId },
            Parts = page
                .Select(p => new ListPartsResult.PartItem
                {
                    PartNumber = p.PartNumber,
                    LastModified = S3Xml.ToS3Timestamp(p.LastModified),
                    ETag = EnsureQuoted(p.ETag),
                    Size = p.Size,
                })
                .ToList(),
        };

        return S3Results.Xml(model);
    }
}
