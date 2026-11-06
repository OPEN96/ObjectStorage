using S3ObjectStorage.Domain.Entities;

namespace S3ObjectStorage.Domain.Abstractions;

public interface IMultipartStore
{
    Task<MultipartUploadEntity?> GetAsync(string uploadId, CancellationToken cancellationToken = default);

    Task<MultipartUploadEntity> CreateAsync(MultipartUploadEntity upload, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string uploadId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string uploadId, CancellationToken cancellationToken = default);

    Task AddOrReplacePartAsync(UploadedPartEntity part, CancellationToken cancellationToken = default);

    Task DeletePartAsync(string uploadId, int partNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UploadedPartEntity>> ListPartsAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>列举桶内未完成的分片上传。</summary>
    Task<IReadOnlyList<MultipartUploadEntity>> ListUploadsAsync(
        string bucketName,
        string? prefix,
        string? keyMarker,
        string? uploadIdMarker,
        int maxUploads,
        CancellationToken cancellationToken = default);
}

public interface IAccessKeyStore
{
    Task<AccessKeyEntity?> FindAsync(string accessKeyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccessKeyEntity>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(AccessKeyEntity key, CancellationToken cancellationToken = default);

    Task DeleteAsync(string accessKeyId, CancellationToken cancellationToken = default);
}
