using S3ObjectStorage.Domain.Entities;

namespace S3ObjectStorage.Domain.Abstractions;

public interface IBucketStore
{
    Task<IReadOnlyList<BucketEntity>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>列出全部桶，供管理端使用（不做租户过滤）。</summary>
    Task<IReadOnlyList<BucketEntity>> ListAllAsync(CancellationToken cancellationToken = default);

    Task<BucketEntity?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    Task CreateAsync(BucketEntity bucket, CancellationToken cancellationToken = default);

    Task DeleteAsync(string name, CancellationToken cancellationToken = default);

    Task UpdateStatsAsync(string name, long objectCountDelta, long sizeDelta, CancellationToken cancellationToken = default);
}
