using System.Text;
using Microsoft.EntityFrameworkCore;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Infrastructure.Repositories;

internal sealed class ObjectStore : IObjectStore
{
    private const int MaxAllowedKeys = 1000;

    private readonly StorageDbContext _db;

    public ObjectStore(StorageDbContext db) => _db = db;

    public Task<ObjectEntity?> GetAsync(string bucketName, string key, CancellationToken cancellationToken = default)
        => _db.Objects.AsNoTracking()
            .FirstOrDefaultAsync(o => o.BucketName == bucketName && o.Key == key, cancellationToken);

    public async Task UpsertAsync(ObjectEntity entity, CancellationToken cancellationToken = default)
    {
        // 并发写同一个 key 时，两个请求都可能判断「不存在」后同时插入，
        // 后插入的会撞上 (BucketName, Key) 唯一索引。这里重试一次，改为覆盖已存在的记录。
        for (var attempt = 0; ; attempt++)
        {
            var existing = await _db.Objects.FirstOrDefaultAsync(
                o => o.BucketName == entity.BucketName && o.Key == entity.Key, cancellationToken);

            if (existing is null)
            {
                _db.Objects.Add(entity);
            }
            else
            {
                _db.Entry(existing).State = EntityState.Detached;
                entity.Id = existing.Id;
                entity.LastModified = DateTimeOffset.UtcNow;
                _db.Objects.Update(entity);
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    public async Task DeleteAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Objects.FirstOrDefaultAsync(o => o.BucketName == bucketName && o.Key == key, cancellationToken);
        if (entity is null)
        {
            return;
        }

        _db.Objects.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ObjectEntity>> GetManyAsync(string bucketName, IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var keyList = keys.Distinct().ToList();
        if (keyList.Count == 0)
        {
            return [];
        }

        return await _db.Objects.AsNoTracking()
            .Where(o => o.BucketName == bucketName && keyList.Contains(o.Key))
            .ToListAsync(cancellationToken);
    }

    public async Task<ListObjectsPage> ListV2Async(
        string bucketName,
        string? prefix,
        string? delimiter,
        string? continuationToken,
        string? startAfter,
        int maxKeys,
        CancellationToken cancellationToken = default)
    {
        maxKeys = Math.Clamp(maxKeys <= 0 ? 1000 : maxKeys, 1, MaxAllowedKeys);

        var query = _db.Objects.AsNoTracking().Where(o => o.BucketName == bucketName);

        if (!string.IsNullOrEmpty(prefix))
        {
            query = query.Where(o => o.Key.StartsWith(prefix));
        }

        // continuation-token（Base64Url 编码的上次位置）与 start-after（原始 key）语义一致：都取严格大于该 key 的记录。
        var marker = !string.IsNullOrEmpty(continuationToken)
            ? TryDecodeBase64Url(continuationToken)
            : startAfter;

        if (!string.IsNullOrEmpty(marker))
        {
            query = query.Where(o => o.Key.CompareTo(marker) > 0);
        }

        query = query.OrderBy(o => o.Key);

        var objects = new List<ObjectEntity>();
        var commonPrefixes = new List<string>();
        var emitted = 0;
        var sawExtraRow = false;
        string? lastAcceptedKey = null;

        await foreach (var obj in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var candidate = GetCommonPrefix(obj.Key, prefix, delimiter);

            if (candidate is not null)
            {
                if (commonPrefixes.Contains(candidate))
                {
                    // 同一分组的后续 key：只更新游标，保证续订 token 落在分组最后一项上。
                    lastAcceptedKey = obj.Key;
                    continue;
                }

                if (emitted >= maxKeys)
                {
                    sawExtraRow = true;
                    break;
                }

                commonPrefixes.Add(candidate);
                emitted++;
                lastAcceptedKey = obj.Key;
                continue;
            }

            if (emitted >= maxKeys)
            {
                sawExtraRow = true;
                break;
            }

            objects.Add(obj);
            emitted++;
            lastAcceptedKey = obj.Key;
        }

        return new ListObjectsPage(
            objects,
            commonPrefixes,
            sawExtraRow,
            sawExtraRow && lastAcceptedKey is not null ? Base64Url(lastAcceptedKey) : null,
            sawExtraRow ? lastAcceptedKey : null);
    }

    public async Task<(long ObjectCount, long TotalSize)> GetBucketUsageAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        var objectCount = await _db.Objects.AsNoTracking()
            .CountAsync(o => o.BucketName == bucketName, cancellationToken);

        if (objectCount == 0)
        {
            return (0, 0);
        }

        var totalSize = await _db.Objects.AsNoTracking()
            .Where(o => o.BucketName == bucketName)
            .SumAsync(o => (long?)o.Size, cancellationToken) ?? 0;

        return (objectCount, totalSize);
    }
    public IAsyncEnumerable<string> EnumerateBlobPathsAsync(CancellationToken cancellationToken = default)
        => _db.Objects.AsNoTracking().Select(o => o.BlobPath).AsAsyncEnumerable();


    public async Task<(long ObjectCount, long TotalSize)> GetTotalUsageAsync(CancellationToken cancellationToken = default)
    {
        var objectCount = await _db.Objects.AsNoTracking().CountAsync(cancellationToken);
        if (objectCount == 0)
        {
            return (0, 0);
        }

        var totalSize = await _db.Objects.AsNoTracking()
            .SumAsync(o => (long?)o.Size, cancellationToken) ?? 0;

        return (objectCount, totalSize);
    }

    /// <summary>
    /// 若 key 在 prefix 之后的部分包含 delimiter，则返回该 key 所属的 <c>CommonPrefix</c>。
    /// </summary>
    private static string? GetCommonPrefix(string key, string? prefix, string? delimiter)
    {
        if (string.IsNullOrEmpty(delimiter))
        {
            return null;
        }

        var searchFrom = string.IsNullOrEmpty(prefix) ? 0 : key.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : 0;
        var index = key.IndexOf(delimiter, Math.Min(searchFrom, key.Length), StringComparison.Ordinal);
        return index < 0 ? null : key[..(index + delimiter.Length)];
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>解码 continuation-token；若客户端传了原文 key 也能容错处理。</summary>
    private static string? TryDecodeBase64Url(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            // 客户端可能直接回传了原始 key，按原样使用。
            return token;
        }
    }
}
