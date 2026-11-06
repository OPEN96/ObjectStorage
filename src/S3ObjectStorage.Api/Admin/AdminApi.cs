using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Infrastructure.Options;
using S3ObjectStorage.S3Protocol.Options;
using S3ObjectStorage.S3Protocol.Routing;

namespace S3ObjectStorage.Api.Admin;

/// <summary>管理端接口返回的错误。</summary>
public sealed class AdminApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// 网页管理端的后端接口。
/// 与 S3 数据面完全隔离：走 <c>/admin/api/*</c>，使用 Cookie / Bearer 认证，
/// 返回 JSON，不参与 SigV4 校验。
/// </summary>
public sealed class AdminApi(
    IBucketStore buckets,
    IObjectStore objects,
    IMultipartStore multiparts,
    IBlobStore blobs,
    IAccessKeyStore accessKeys,
    IOptions<StorageOptions> storageOptions,
    IOptions<S3ServerOptions> s3Options,
    ServerMetrics metrics,
    AdminAuth auth,
    AdminLoginThrottle loginThrottle,
    ILogger<AdminApi> logger)
{
    private const int MaxListKeys = 1000;

    /// <summary>Secret 轮换时的最小长度，与启动期对引导密钥的要求一致。</summary>
    private const int MinSecretLength = 16;

    public async Task HandleAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchAsync(context, path, cancellationToken);
        }
        catch (AdminApiException exception)
        {
            await WriteErrorAsync(context, exception.Status, exception.Message, cancellationToken);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "请求体不是合法的 JSON。", cancellationToken);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端已断开。
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "管理端接口处理失败 path={Path}", path);
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "服务端内部错误，请查看日志。", cancellationToken);
        }
    }

    private async Task DispatchAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        var method = context.Request.Method;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "api", StringComparison.Ordinal))
        {
            throw new AdminApiException(StatusCodes.Status404NotFound, "接口不存在。");
        }

        var resource = segments[1];

        // 健康检查与登录不需要已登录身份。
        if (resource == "health" && HttpMethods.IsGet(method))
        {
            await WriteJsonAsync(context, StatusCodes.Status200OK, new
            {
                status = "ok",
                time = DateTimeOffset.UtcNow,
                version = Version,
            }, cancellationToken);
            return;
        }

        if (resource == "auth" && segments.Length == 3 && segments[2] == "login" && HttpMethods.IsPost(method))
        {
            await LoginAsync(context, cancellationToken);
            return;
        }

        var identity = await auth.AuthenticateAsync(context, cancellationToken);
        if (identity is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "未登录或会话已过期。", cancellationToken);
            return;
        }

        if (resource == "auth")
        {
            if (segments.Length == 3 && segments[2] == "me" && HttpMethods.IsGet(method))
            {
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    subject = identity.Subject,
                    displayName = identity.DisplayName,
                    viaAccessKey = identity.ViaAccessKey,
                }, cancellationToken);
                return;
            }

            if (segments.Length == 3 && segments[2] == "logout" && HttpMethods.IsPost(method))
            {
                auth.ClearCookie(context);
                await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true }, cancellationToken);
                return;
            }
        }

        if (resource == "overview" && segments.Length == 2 && HttpMethods.IsGet(method))
        {
            await GetOverviewAsync(context, cancellationToken);
            return;
        }

        if (resource == "buckets")
        {
            await RouteBucketsAsync(context, segments, method, identity, cancellationToken);
            return;
        }

        if (resource == "access-keys")
        {
            await RouteAccessKeysAsync(context, segments, method, identity, cancellationToken);
            return;
        }

        throw new AdminApiException(StatusCodes.Status404NotFound, "接口不存在。");
    }

    // ---------------------------------------------------------------- 登录

    private async Task LoginAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (loginThrottle.IsBlocked(source, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            await WriteErrorAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                "登录尝试过于频繁，请稍后再试。",
                cancellationToken);
            return;
        }

        var identity = await auth.LoginAsync(context.Request, cancellationToken);
        if (identity is null)
        {
            loginThrottle.RecordFailure(source);

            // 固定的小延迟，抬高自动化撞库的成本。
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "登录令牌或访问密钥不正确。", cancellationToken);
            return;
        }

        loginThrottle.Reset(source);
        auth.IssueCookie(context, identity);
        logger.LogInformation("管理端登录成功 subject={Subject}", identity.Subject);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            subject = identity.Subject,
            displayName = identity.DisplayName,
            viaAccessKey = identity.ViaAccessKey,
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- 概览

    private async Task GetOverviewAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var allBuckets = await buckets.ListAllAsync(cancellationToken);
        var usage = await objects.GetTotalUsageAsync(cancellationToken);
        var keys = await accessKeys.ListAsync(cancellationToken);

        var activeUploads = 0;
        long pendingUploadBytes = 0;
        var bucketSummaries = new List<object>(allBuckets.Count);

        foreach (var bucket in allBuckets)
        {
            var bucketUsage = await objects.GetBucketUsageAsync(bucket.Name, cancellationToken);
            var uploads = await multiparts.ListUploadsAsync(bucket.Name, null, null, null, MaxListKeys, cancellationToken);
            activeUploads += uploads.Count;
            foreach (var upload in uploads)
            {
                pendingUploadBytes += upload.Parts.Sum(p => p.Size);
            }

            bucketSummaries.Add(new
            {
                name = bucket.Name,
                ownerId = bucket.OwnerId,
                ownerDisplayName = bucket.OwnerDisplayName,
                region = bucket.Region,
                createdAt = bucket.CreatedAt,
                objectCount = bucketUsage.ObjectCount,
                totalSize = bucketUsage.TotalSize,
            });
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            buckets = allBuckets.Count,
            objects = usage.ObjectCount,
            totalBytes = usage.TotalSize,
            activeUploads,
            pendingUploadBytes,
            accessKeys = keys.Count,
            disk = GetDiskInfo(),
            server = new
            {
                version = Version,
                runtime = Environment.Version.ToString(),
                region = s3Options.Value.Region,
                endpoint = context.Request.Scheme + "://" + context.Request.Host,
                startedAt = metrics.StartedAt,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - metrics.StartedAt).TotalSeconds,
                workingSetBytes = Environment.WorkingSet,
                rootPath = Path.GetFullPath(storageOptions.Value.RootPath),
            },
            metrics = metrics.Snapshot(),
            bucketSummaries,
        }, cancellationToken);
    }

    private object GetDiskInfo()
    {
        try
        {
            var root = Path.GetFullPath(storageOptions.Value.RootPath);
            var drive = new DriveInfo(Path.GetPathRoot(root) ?? root);
            if (!drive.IsReady)
            {
                return new { available = false };
            }

            return new
            {
                available = true,
                totalBytes = drive.TotalSize,
                freeBytes = drive.AvailableFreeSpace,
                usedBytes = drive.TotalSize - drive.AvailableFreeSpace,
            };
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "读取磁盘容量失败");
            return new { available = false };
        }
    }

    // ---------------------------------------------------------------- 桶

    private async Task RouteBucketsAsync(
        HttpContext context,
        string[] segments,
        string method,
        AdminIdentity identity,
        CancellationToken cancellationToken)
    {
        if (segments.Length == 2)
        {
            if (HttpMethods.IsGet(method))
            {
                await ListBucketsAsync(context, cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(method))
            {
                await CreateBucketAsync(context, identity, cancellationToken);
                return;
            }

            throw new AdminApiException(StatusCodes.Status405MethodNotAllowed, "不支持的请求方法。");
        }

        var bucketName = segments[2];

        if (segments.Length == 3)
        {
            if (HttpMethods.IsGet(method))
            {
                await GetBucketAsync(context, bucketName, cancellationToken);
                return;
            }

            if (HttpMethods.IsDelete(method))
            {
                await DeleteBucketAsync(context, bucketName, cancellationToken);
                return;
            }

            throw new AdminApiException(StatusCodes.Status405MethodNotAllowed, "不支持的请求方法。");
        }

        var subResource = segments[3];

        if (subResource == "objects")
        {
            // /api/buckets/{bucket}/objects/delete 是批量删除
            if (segments.Length == 5 && segments[4] == "delete" && HttpMethods.IsPost(method))
            {
                await DeleteObjectsAsync(context, bucketName, cancellationToken);
                return;
            }

            // /api/buckets/{bucket}/objects/{**key}
            var key = segments.Length > 4 ? ExtractObjectKey(context) : string.Empty;

            if (HttpMethods.IsPut(method))
            {
                await UploadObjectAsync(context, bucketName, key, cancellationToken);
                return;
            }

            if (HttpMethods.IsGet(method))
            {
                if (string.IsNullOrEmpty(key))
                {
                    await ListObjectsAsync(context, bucketName, cancellationToken);
                }
                else
                {
                    await DownloadObjectAsync(context, bucketName, key, cancellationToken);
                }

                return;
            }

            if (HttpMethods.IsDelete(method))
            {
                await DeleteObjectAsync(context, bucketName, key, cancellationToken);
                return;
            }

            throw new AdminApiException(StatusCodes.Status405MethodNotAllowed, "不支持的请求方法。");
        }

        if (subResource == "uploads")
        {
            if (segments.Length == 4 && HttpMethods.IsGet(method))
            {
                await ListUploadsAsync(context, bucketName, cancellationToken);
                return;
            }

            if (segments.Length == 5 && HttpMethods.IsDelete(method))
            {
                await AbortUploadAsync(context, bucketName, segments[4], cancellationToken);
                return;
            }
        }

        throw new AdminApiException(StatusCodes.Status404NotFound, "接口不存在。");
    }

    private async Task ListBucketsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var all = await buckets.ListAllAsync(cancellationToken);
        var result = new List<object>(all.Count);

        foreach (var bucket in all)
        {
            var usage = await objects.GetBucketUsageAsync(bucket.Name, cancellationToken);
            result.Add(new
            {
                name = bucket.Name,
                ownerId = bucket.OwnerId,
                ownerDisplayName = bucket.OwnerDisplayName,
                region = bucket.Region,
                createdAt = bucket.CreatedAt,
                objectCount = usage.ObjectCount,
                totalSize = usage.TotalSize,
            });
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { buckets = result }, cancellationToken);
    }

    private async Task GetBucketAsync(HttpContext context, string bucketName, CancellationToken cancellationToken)
    {
        var bucket = await RequireBucketAsync(bucketName, cancellationToken);
        var usage = await objects.GetBucketUsageAsync(bucketName, cancellationToken);
        var uploads = await multiparts.ListUploadsAsync(bucketName, null, null, null, MaxListKeys, cancellationToken);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            name = bucket.Name,
            ownerId = bucket.OwnerId,
            ownerDisplayName = bucket.OwnerDisplayName,
            region = bucket.Region,
            createdAt = bucket.CreatedAt,
            objectCount = usage.ObjectCount,
            totalSize = usage.TotalSize,
            activeUploads = uploads.Count,
        }, cancellationToken);
    }

    private async Task CreateBucketAsync(HttpContext context, AdminIdentity identity, CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<CreateBucketRequest>(context, cancellationToken);
        var name = request.Name?.Trim() ?? string.Empty;

        if (!BucketNameValidator.IsValid(name))
        {
            throw new AdminApiException(
                StatusCodes.Status400BadRequest,
                "桶名不合法：3-63 位，仅小写字母、数字、'-'、'.'，需以字母或数字开头结尾。");
        }

        if (await buckets.ExistsAsync(name, cancellationToken))
        {
            throw new AdminApiException(StatusCodes.Status409Conflict, "同名存储桶已存在。");
        }

        var ownerId = request.OwnerId;
        var ownerName = ownerId;

        if (string.IsNullOrWhiteSpace(ownerId))
        {
            if (identity.ViaAccessKey)
            {
                ownerId = identity.Subject;
                ownerName = identity.DisplayName;
            }
            else
            {
                var adminKey = (await accessKeys.ListAsync(cancellationToken))
                    .FirstOrDefault(k => k.IsAdmin && k.Enabled);
                ownerId = adminKey?.AccessKeyId ?? "admin";
                ownerName = adminKey?.DisplayName ?? "管理员";
            }
        }

        await buckets.CreateAsync(new BucketEntity
        {
            Name = name,
            OwnerId = ownerId!,
            OwnerDisplayName = ownerName ?? ownerId!,
            Region = s3Options.Value.Region,
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        logger.LogInformation("管理端创建桶 {Bucket} (owner={Owner})", name, ownerId);

        await WriteJsonAsync(context, StatusCodes.Status201Created, new
        {
            name,
            ownerId,
            ownerDisplayName = ownerName,
            region = s3Options.Value.Region,
            createdAt = DateTimeOffset.UtcNow,
            objectCount = 0,
            totalSize = 0,
        }, cancellationToken);
    }

    private async Task DeleteBucketAsync(HttpContext context, string bucketName, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var usage = await objects.GetBucketUsageAsync(bucketName, cancellationToken);
        var force = string.Equals(context.Request.Query["force"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

        if (usage.ObjectCount > 0 && !force)
        {
            throw new AdminApiException(
                StatusCodes.Status409Conflict,
                $"桶内还有 {usage.ObjectCount} 个对象，需勾选「强制删除」才能清空。");
        }

        var deleted = 0;
        if (force)
        {
            deleted = await PurgeObjectsAsync(bucketName, cancellationToken);
        }

        // 未完成的分片上传一并清理，否则临时分片会一直占盘。
        var uploads = await multiparts.ListUploadsAsync(bucketName, null, null, null, MaxListKeys, cancellationToken);
        foreach (var upload in uploads)
        {
            await multiparts.DeleteAsync(upload.UploadId, cancellationToken);
            await blobs.DeletePartsAsync(upload.UploadId, cancellationToken);
        }

        await buckets.DeleteAsync(bucketName, cancellationToken);
        logger.LogInformation("管理端删除桶 {Bucket}（清空对象 {Count} 个）", bucketName, deleted);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true, deletedObjects = deleted }, cancellationToken);
    }

    private async Task<int> PurgeObjectsAsync(string bucketName, CancellationToken cancellationToken)
    {
        var deleted = 0;
        string? continuation = null;

        do
        {
            var page = await objects.ListV2Async(bucketName, null, null, continuation, null, MaxListKeys, cancellationToken);
            foreach (var entity in page.Objects)
            {
                await objects.DeleteAsync(bucketName, entity.Key, cancellationToken);
                await blobs.DeleteAsync(entity.BlobPath, cancellationToken);
                deleted++;
            }

            continuation = page.IsTruncated ? page.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuation));

        return deleted;
    }

    // ---------------------------------------------------------------- 对象

    private async Task ListObjectsAsync(HttpContext context, string bucketName, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var query = context.Request.Query;
        var prefix = query["prefix"].ToString();
        var delimiter = query["delimiter"].ToString();
        var continuation = query["continuation"].ToString();
        var maxKeys = ParseMaxKeys(query["maxKeys"].ToString());

        var page = await objects.ListV2Async(
            bucketName,
            string.IsNullOrEmpty(prefix) ? null : prefix,
            string.IsNullOrEmpty(delimiter) ? null : delimiter,
            string.IsNullOrEmpty(continuation) ? null : continuation,
            null,
            maxKeys,
            cancellationToken);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            bucket = bucketName,
            prefix,
            delimiter,
            isTruncated = page.IsTruncated,
            nextContinuationToken = page.NextContinuationToken,
            objects = page.Objects.Select(o => new
            {
                key = o.Key,
                size = o.Size,
                etag = o.ETag,
                contentType = o.ContentType,
                lastModified = o.LastModified,
            }),
            commonPrefixes = page.CommonPrefixes,
        }, cancellationToken);
    }

    private async Task UploadObjectAsync(HttpContext context, string bucketName, string key, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        if (string.IsNullOrEmpty(key))
        {
            throw new AdminApiException(StatusCodes.Status400BadRequest, "对象 key 不能为空。");
        }

        if (key.Length > s3Options.Value.MaxKeyLength)
        {
            throw new AdminApiException(StatusCodes.Status400BadRequest, "对象 key 过长。");
        }

        var existing = await objects.GetAsync(bucketName, key, cancellationToken);

        var contentType = context.Request.ContentType;
        if (string.IsNullOrWhiteSpace(contentType)
            || string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            var fromQuery = context.Request.Query["contentType"].ToString();
            if (!string.IsNullOrWhiteSpace(fromQuery))
            {
                contentType = fromQuery;
            }
        }

        var write = await blobs.WriteAsync(context.Request.Body, cancellationToken);

        var entity = new ObjectEntity
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            BucketName = bucketName,
            Key = key,
            Size = write.Size,
            ETag = write.ContentMd5Hex,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            LastModified = DateTimeOffset.UtcNow,
            BlobPath = write.BlobPath,
        };

        await objects.UpsertAsync(entity, cancellationToken);
        await buckets.UpdateStatsAsync(
            bucketName,
            existing is null ? 1 : 0,
            write.Size - (existing?.Size ?? 0),
            cancellationToken);

        if (existing is not null && !string.Equals(existing.BlobPath, write.BlobPath, StringComparison.Ordinal))
        {
            await blobs.DeleteAsync(existing.BlobPath, cancellationToken);
        }

        logger.LogInformation("管理端上传 {Bucket}/{Key} size={Size} etag={ETag}", bucketName, key, write.Size, write.ContentMd5Hex);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            key,
            size = write.Size,
            etag = write.ContentMd5Hex,
            contentType = entity.ContentType,
            lastModified = entity.LastModified,
        }, cancellationToken);
    }

    private async Task DownloadObjectAsync(HttpContext context, string bucketName, string key, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var entity = await objects.GetAsync(bucketName, key, cancellationToken)
            ?? throw new AdminApiException(StatusCodes.Status404NotFound, "对象不存在。");

        var download = string.Equals(context.Request.Query["download"].ToString(), "1", StringComparison.Ordinal);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = entity.ContentType;
        context.Response.ContentLength = entity.Size;
        context.Response.Headers.ETag = "\"" + entity.ETag + "\"";
        context.Response.Headers["Last-Modified"] = entity.LastModified.ToString("R", CultureInfo.InvariantCulture);

        if (download)
        {
            var fileName = entity.Key.Split('/').Last();
            var encoded = Uri.EscapeDataString(fileName);
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{encoded}\"; filename*=UTF-8''{encoded}";
        }

        await using var stream = await blobs.OpenReadAsync(entity.BlobPath, null, null, cancellationToken);
        await stream.CopyToAsync(context.Response.Body, 128 * 1024, cancellationToken);
    }

    private async Task DeleteObjectAsync(HttpContext context, string bucketName, string key, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var entity = await objects.GetAsync(bucketName, key, cancellationToken);
        if (entity is null)
        {
            await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true, existed = false }, cancellationToken);
            return;
        }

        await objects.DeleteAsync(bucketName, key, cancellationToken);
        await blobs.DeleteAsync(entity.BlobPath, cancellationToken);
        await buckets.UpdateStatsAsync(bucketName, -1, -entity.Size, cancellationToken);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true, existed = true }, cancellationToken);
    }

    private async Task DeleteObjectsAsync(HttpContext context, string bucketName, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var request = await ReadJsonAsync<AdminDeleteObjectsRequest>(context, cancellationToken);
        var keys = (request.Keys ?? []).Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        if (keys.Count == 0)
        {
            throw new AdminApiException(StatusCodes.Status400BadRequest, "keys 不能为空。");
        }

        var entities = await objects.GetManyAsync(bucketName, keys, cancellationToken);
        var byKey = entities.ToDictionary(e => e.Key, StringComparer.Ordinal);

        long sizeDelta = 0;
        var deleted = 0;

        foreach (var key in keys)
        {
            if (!byKey.TryGetValue(key, out var entity))
            {
                continue;
            }

            await objects.DeleteAsync(bucketName, key, cancellationToken);
            await blobs.DeleteAsync(entity.BlobPath, cancellationToken);
            sizeDelta -= entity.Size;
            deleted++;
        }

        if (deleted > 0)
        {
            await buckets.UpdateStatsAsync(bucketName, -deleted, sizeDelta, cancellationToken);
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { deleted }, cancellationToken);
    }

    // ---------------------------------------------------------------- 分片任务

    private async Task ListUploadsAsync(HttpContext context, string bucketName, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var uploads = await multiparts.ListUploadsAsync(bucketName, null, null, null, MaxListKeys, cancellationToken);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            uploads = uploads.Select(u => new
            {
                uploadId = u.UploadId,
                key = u.Key,
                contentType = u.ContentType,
                initiatedAt = u.InitiatedAt,
                initiatorId = u.InitiatorId,
                uploadedParts = u.Parts.Count,
                uploadedBytes = u.Parts.Sum(p => p.Size),
            }),
        }, cancellationToken);
    }

    private async Task AbortUploadAsync(HttpContext context, string bucketName, string uploadId, CancellationToken cancellationToken)
    {
        await RequireBucketAsync(bucketName, cancellationToken);

        var upload = await multiparts.GetAsync(uploadId, cancellationToken);
        if (upload is null || !string.Equals(upload.BucketName, bucketName, StringComparison.Ordinal))
        {
            throw new AdminApiException(StatusCodes.Status404NotFound, "分片上传任务不存在。");
        }

        await multiparts.DeleteAsync(uploadId, cancellationToken);
        await blobs.DeletePartsAsync(uploadId, cancellationToken);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true }, cancellationToken);
    }

    // ---------------------------------------------------------------- 访问密钥

    private async Task RouteAccessKeysAsync(
        HttpContext context,
        string[] segments,
        string method,
        AdminIdentity identity,
        CancellationToken cancellationToken)
    {
        if (segments.Length == 2)
        {
            if (HttpMethods.IsGet(method))
            {
                var keys = await accessKeys.ListAsync(cancellationToken);
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    accessKeys = keys
                        .OrderBy(k => k.CreatedAt)
                        .Select(k => new
                        {
                            accessKeyId = k.AccessKeyId,
                            displayName = k.DisplayName,
                            isAdmin = k.IsAdmin,
                            enabled = k.Enabled,
                            createdAt = k.CreatedAt,
                            isCurrent = identity.ViaAccessKey && k.AccessKeyId == identity.Subject,
                        }),
                }, cancellationToken);
                return;
            }

            if (HttpMethods.IsPost(method))
            {
                await CreateAccessKeyAsync(context, cancellationToken);
                return;
            }

            throw new AdminApiException(StatusCodes.Status405MethodNotAllowed, "不支持的请求方法。");
        }

        if (segments.Length == 3)
        {
            var keyId = segments[2];

            if (HttpMethods.IsPatch(method))
            {
                await UpdateAccessKeyAsync(context, keyId, cancellationToken);
                return;
            }

            if (HttpMethods.IsDelete(method))
            {
                await DeleteAccessKeyAsync(context, keyId, cancellationToken);
                return;
            }
        }

        throw new AdminApiException(StatusCodes.Status404NotFound, "接口不存在。");
    }

    private async Task CreateAccessKeyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<CreateAccessKeyRequest>(context, cancellationToken);

        string accessKeyId;
        do
        {
            accessKeyId = GenerateAccessKeyId();
        }
        while (await accessKeys.FindAsync(accessKeyId, cancellationToken) is not null);

        var secret = GenerateSecret();
        var entity = new AccessKeyEntity
        {
            AccessKeyId = accessKeyId,
            SecretAccessKey = secret,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? accessKeyId : request.DisplayName.Trim(),
            IsAdmin = request.IsAdmin,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await accessKeys.UpsertAsync(entity, cancellationToken);
        logger.LogInformation("管理端创建访问密钥 {AccessKeyId} (admin={IsAdmin})", accessKeyId, entity.IsAdmin);

        // Secret 只在这里返回一次。
        await WriteJsonAsync(context, StatusCodes.Status201Created, new
        {
            accessKeyId,
            secretAccessKey = secret,
            displayName = entity.DisplayName,
            isAdmin = entity.IsAdmin,
            enabled = true,
            createdAt = entity.CreatedAt,
        }, cancellationToken);
    }

    private async Task UpdateAccessKeyAsync(HttpContext context, string keyId, CancellationToken cancellationToken)
    {
        var existing = await accessKeys.FindAsync(keyId, cancellationToken)
            ?? throw new AdminApiException(StatusCodes.Status404NotFound, "访问密钥不存在。");

        var request = await ReadJsonAsync<UpdateAccessKeyRequest>(context, cancellationToken);

        if (request.Enabled is { } enabled && !enabled && existing.IsAdmin && await IsLastAdminAsync(keyId, cancellationToken))
        {
            throw new AdminApiException(StatusCodes.Status409Conflict, "不能禁用最后一个可用的管理员密钥。");
        }

        if (request.Enabled is { } value)
        {
            existing.Enabled = value;
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            existing.DisplayName = request.DisplayName.Trim();
        }

        // 原地轮换 Secret：AccessKeyId 不变，因此桶属主（OwnerId = AccessKeyId）与客户端配置都不受影响。
        var rotated = false;
        if (!string.IsNullOrWhiteSpace(request.SecretAccessKey))
        {
            var secret = request.SecretAccessKey.Trim();
            if (secret.Length < MinSecretLength)
            {
                throw new AdminApiException(
                    StatusCodes.Status400BadRequest,
                    $"secretAccessKey 至少需要 {MinSecretLength} 位随机字符。");
            }

            if (!string.Equals(secret, existing.SecretAccessKey, StringComparison.Ordinal))
            {
                existing.SecretAccessKey = secret;
                rotated = true;
            }
        }

        await accessKeys.UpsertAsync(existing, cancellationToken);

        if (rotated)
        {
            logger.LogInformation("管理端轮换访问密钥 Secret {AccessKeyId}", existing.AccessKeyId);
        }

        await WriteJsonAsync(context, StatusCodes.Status200OK, new
        {
            accessKeyId = existing.AccessKeyId,
            displayName = existing.DisplayName,
            isAdmin = existing.IsAdmin,
            enabled = existing.Enabled,
            createdAt = existing.CreatedAt,
            rotated,
        }, cancellationToken);
    }

    private async Task DeleteAccessKeyAsync(HttpContext context, string keyId, CancellationToken cancellationToken)
    {
        var existing = await accessKeys.FindAsync(keyId, cancellationToken)
            ?? throw new AdminApiException(StatusCodes.Status404NotFound, "访问密钥不存在。");

        if (existing.IsAdmin && existing.Enabled && await IsLastAdminAsync(keyId, cancellationToken))
        {
            throw new AdminApiException(StatusCodes.Status409Conflict, "不能删除最后一个可用的管理员密钥。");
        }

        await accessKeys.DeleteAsync(keyId, cancellationToken);
        logger.LogInformation("管理端删除访问密钥 {AccessKeyId}", keyId);

        await WriteJsonAsync(context, StatusCodes.Status200OK, new { ok = true }, cancellationToken);
    }

    private async Task<bool> IsLastAdminAsync(string keyId, CancellationToken cancellationToken)
    {
        var keys = await accessKeys.ListAsync(cancellationToken);
        var others = keys.Count(k => k.IsAdmin && k.Enabled && k.AccessKeyId != keyId);
        return others == 0;
    }

    // ---------------------------------------------------------------- 工具

    private async Task<BucketEntity> RequireBucketAsync(string bucketName, CancellationToken cancellationToken)
        => await buckets.GetAsync(bucketName, cancellationToken)
           ?? throw new AdminApiException(StatusCodes.Status404NotFound, "存储桶不存在。");

    /// <summary>
    /// 从原始请求目标里取对象 key。
    /// Kestrel 不会把 <c>%2F</c> 解码成路径分隔符，直接用 Path 分段会把「key 里含斜杠」
    /// 与「目录分隔」混在一起；这里对原始编码串做一次整体解码，得到真实 key。
    /// </summary>
    private static string ExtractObjectKey(HttpContext context)
    {
        const string marker = "/objects/";

        var raw = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
                  ?? context.Request.Path.Value
                  ?? string.Empty;

        var queryIndex = raw.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            raw = raw[..queryIndex];
        }

        var markerIndex = raw.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        return Uri.UnescapeDataString(raw[(markerIndex + marker.Length)..]);
    }

    private static int ParseMaxKeys(string? raw)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return Math.Clamp(value, 1, MaxListKeys);
        }

        return 200;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        var body = await context.Request.ReadFromJsonAsync<T>(cancellationToken);
        return body ?? throw new AdminApiException(StatusCodes.Status400BadRequest, "请求体不能为空。");
    }

    private static Task WriteJsonAsync(HttpContext context, int status, object payload, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(payload, cancellationToken);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string message, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        // 不能调用 Response.Clear()：它会连同 CSP、nosniff 等安全头和 Retry-After 一起抹掉，
        // 让错误响应的防护强度低于正常响应（曾导致 429 缺少 Retry-After）。
        context.Response.ContentLength = null;
        return WriteJsonAsync(context, status, new { error = message }, cancellationToken);
    }

    private static string GenerateAccessKeyId()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        Span<char> buffer = stackalloc char[16];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return "AKIA" + new string(buffer);
    }

    private static string GenerateSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(30))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string Version =>
        typeof(AdminApi).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private sealed record CreateBucketRequest(string? Name, string? OwnerId);

    private sealed record CreateAccessKeyRequest(string? DisplayName, bool IsAdmin);

    private sealed record UpdateAccessKeyRequest(bool? Enabled, string? DisplayName, string? SecretAccessKey);

    private sealed record AdminDeleteObjectsRequest(List<string>? Keys);
}
