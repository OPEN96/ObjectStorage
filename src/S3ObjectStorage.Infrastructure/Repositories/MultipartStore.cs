using Microsoft.EntityFrameworkCore;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Domain.Entities;
using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Infrastructure.Repositories;

internal sealed class MultipartStore : IMultipartStore
{
    private readonly StorageDbContext _db;

    public MultipartStore(StorageDbContext db) => _db = db;

    public Task<MultipartUploadEntity?> GetAsync(string uploadId, CancellationToken cancellationToken = default)
        => _db.MultipartUploads.AsNoTracking()
            .Include(u => u.Parts)
            .FirstOrDefaultAsync(u => u.UploadId == uploadId, cancellationToken);

    public async Task<MultipartUploadEntity> CreateAsync(MultipartUploadEntity upload, CancellationToken cancellationToken = default)
    {
        _db.MultipartUploads.Add(upload);
        await _db.SaveChangesAsync(cancellationToken);
        return upload;
    }

    public Task<bool> ExistsAsync(string uploadId, CancellationToken cancellationToken = default)
        => _db.MultipartUploads.AnyAsync(u => u.UploadId == uploadId, cancellationToken);

    public async Task DeleteAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var entity = await _db.MultipartUploads.FirstOrDefaultAsync(u => u.UploadId == uploadId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        _db.MultipartUploads.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddOrReplacePartAsync(UploadedPartEntity part, CancellationToken cancellationToken = default)
    {
        var existing = await _db.UploadedParts.FirstOrDefaultAsync(
            p => p.UploadId == part.UploadId && p.PartNumber == part.PartNumber, cancellationToken);

        if (existing is null)
        {
            _db.UploadedParts.Add(part);
        }
        else
        {
            existing.Size = part.Size;
            existing.ETag = part.ETag;
            existing.BlobPath = part.BlobPath;
            existing.LastModified = part.LastModified;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeletePartAsync(string uploadId, int partNumber, CancellationToken cancellationToken = default)
    {
        var existing = await _db.UploadedParts.FirstOrDefaultAsync(
            p => p.UploadId == uploadId && p.PartNumber == partNumber, cancellationToken);

        if (existing is null)
        {
            return;
        }

        _db.UploadedParts.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UploadedPartEntity>> ListPartsAsync(string uploadId, CancellationToken cancellationToken = default)
        => await _db.UploadedParts.AsNoTracking()
            .Where(p => p.UploadId == uploadId)
            .OrderBy(p => p.PartNumber)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MultipartUploadEntity>> ListUploadsAsync(
        string bucketName,
        string? prefix,
        string? keyMarker,
        string? uploadIdMarker,
        int maxUploads,
        CancellationToken cancellationToken = default)
    {
        var query = _db.MultipartUploads.AsNoTracking()
            .Include(u => u.Parts)
            .Where(u => u.BucketName == bucketName);

        if (!string.IsNullOrEmpty(prefix))
        {
            query = query.Where(u => u.Key.StartsWith(prefix));
        }

        if (!string.IsNullOrEmpty(keyMarker))
        {
            if (string.IsNullOrEmpty(uploadIdMarker))
            {
                query = query.Where(u => u.Key.CompareTo(keyMarker) > 0);
            }
            else
            {
                query = query.Where(u => u.Key.CompareTo(keyMarker) > 0
                                         || (u.Key == keyMarker && u.UploadId.CompareTo(uploadIdMarker) > 0));
            }
        }

        return await query
            .OrderBy(u => u.Key)
            .ThenBy(u => u.UploadId)
            .Take(Math.Clamp(maxUploads <= 0 ? 1000 : maxUploads, 1, 1000))
            .ToListAsync(cancellationToken);
    }
}
