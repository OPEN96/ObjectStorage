using S3ObjectStorage.Domain.Entities;

namespace S3ObjectStorage.Domain.Abstractions;

/// <summary>
/// ListObjectsV2 的查询结果页。
/// </summary>
/// <param name="Objects">本页返回的对象。</param>
/// <param name="CommonPrefixes">按 delimiter 归并出的公共前缀。</param>
/// <param name="IsTruncated">是否还有下一页。</param>
/// <param name="NextContinuationToken">V2 续订令牌。</param>
/// <param name="LastKey">本次返回涉及的最后一条原始 key，用作 V1 的 NextMarker。</param>
public sealed record ListObjectsPage(
    IReadOnlyList<ObjectEntity> Objects,
    IReadOnlyList<string> CommonPrefixes,
    bool IsTruncated,
    string? NextContinuationToken,
    string? LastKey);

public interface IObjectStore
{
    Task<ObjectEntity?> GetAsync(string bucketName, string key, CancellationToken cancellationToken = default);

    Task UpsertAsync(ObjectEntity entity, CancellationToken cancellationToken = default);

    Task DeleteAsync(string bucketName, string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObjectEntity>> GetManyAsync(string bucketName, IEnumerable<string> keys, CancellationToken cancellationToken = default);

    Task<ListObjectsPage> ListV2Async(
        string bucketName,
        string? prefix,
        string? delimiter,
        string? continuationToken,
        string? startAfter,
        int maxKeys,
        CancellationToken cancellationToken = default);

    Task<(long ObjectCount, long TotalSize)> GetBucketUsageAsync(string bucketName, CancellationToken cancellationToken = default);

    /// <summary>枚举全部对象的 BlobPath，供孤儿回收使用。</summary>
    IAsyncEnumerable<string> EnumerateBlobPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>整个实例的对象数与总容量，供管理端看板使用。</summary>
    Task<(long ObjectCount, long TotalSize)> GetTotalUsageAsync(CancellationToken cancellationToken = default);
}
