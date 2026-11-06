using Microsoft.EntityFrameworkCore;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Infrastructure.Repositories;

internal sealed class AccessKeyStore : IAccessKeyStore
{
    private readonly StorageDbContext _db;

    public AccessKeyStore(StorageDbContext db) => _db = db;

    public Task<AccessKeyEntity?> FindAsync(string accessKeyId, CancellationToken cancellationToken = default)
        => _db.AccessKeys.AsNoTracking().FirstOrDefaultAsync(k => k.AccessKeyId == accessKeyId, cancellationToken);

    public async Task<IReadOnlyList<AccessKeyEntity>> ListAsync(CancellationToken cancellationToken = default)
        => await _db.AccessKeys.AsNoTracking().OrderBy(k => k.AccessKeyId).ToListAsync(cancellationToken);

    public async Task UpsertAsync(AccessKeyEntity key, CancellationToken cancellationToken = default)
    {
        var existing = await _db.AccessKeys.FirstOrDefaultAsync(k => k.AccessKeyId == key.AccessKeyId, cancellationToken);
        if (existing is null)
        {
            _db.AccessKeys.Add(key);
        }
        else
        {
            existing.SecretAccessKey = key.SecretAccessKey;
            existing.DisplayName = key.DisplayName;
            existing.IsAdmin = key.IsAdmin;
            existing.Enabled = key.Enabled;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string accessKeyId, CancellationToken cancellationToken = default)
    {
        var existing = await _db.AccessKeys.FirstOrDefaultAsync(k => k.AccessKeyId == accessKeyId, cancellationToken);
        if (existing is null)
        {
            return;
        }

        _db.AccessKeys.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
