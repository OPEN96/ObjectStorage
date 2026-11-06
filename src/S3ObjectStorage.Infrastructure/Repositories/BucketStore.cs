using Microsoft.EntityFrameworkCore;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Infrastructure.Repositories;

internal sealed class BucketStore : IBucketStore
{
    private readonly StorageDbContext _db;

    public BucketStore(StorageDbContext db) => _db = db;

    public async Task<IReadOnlyList<BucketEntity>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
        => await _db.Buckets.AsNoTracking()
            .Where(b => b.OwnerId == ownerId)
            .OrderBy(b => b.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BucketEntity>> ListAllAsync(CancellationToken cancellationToken = default)
        => await _db.Buckets.AsNoTracking()
            .OrderBy(b => b.Name)
            .ToListAsync(cancellationToken);

    public Task<BucketEntity?> GetAsync(string name, CancellationToken cancellationToken = default)
        => _db.Buckets.AsNoTracking().FirstOrDefaultAsync(b => b.Name == name, cancellationToken);

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        => _db.Buckets.AnyAsync(b => b.Name == name, cancellationToken);

    public async Task CreateAsync(BucketEntity bucket, CancellationToken cancellationToken = default)
    {
        _db.Buckets.Add(bucket);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Buckets.FirstOrDefaultAsync(b => b.Name == name, cancellationToken);
        if (entity is null)
        {
            return;
        }

        _db.Buckets.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateStatsAsync(string name, long objectCountDelta, long sizeDelta, CancellationToken cancellationToken = default)
        => await _db.Buckets
            .Where(b => b.Name == name)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ObjectCount, b => b.ObjectCount + objectCountDelta)
                .SetProperty(b => b.TotalSize, b => b.TotalSize + sizeDelta), cancellationToken);
}
