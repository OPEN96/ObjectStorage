namespace S3ObjectStorage.Domain.Abstractions;

/// <summary>
/// 写入对象体后的结果。
/// </summary>
/// <param name="BlobPath">对象体的相对路径。</param>
/// <param name="Size">字节数。</param>
/// <param name="ContentMd5Hex">内容的 MD5 十六进制。</param>
/// <param name="ContentSha256Hex">内容的 SHA256 十六进制，用于校验 x-amz-content-sha256。</param>
public sealed record BlobWriteResult(string BlobPath, long Size, string ContentMd5Hex, string ContentSha256Hex);

/// <summary>
/// 对象体（blob）的物理存储抽象。当前实现为本地文件系统，
/// 可替换为自研分布式存储、MinIO、OSS 等。
/// </summary>
public interface IBlobStore
{
    /// <summary>将流完整写入临时文件并返回写入结果（含 MD5/SHA256）。</summary>
    Task<BlobWriteResult> WriteAsync(Stream source, CancellationToken cancellationToken = default);

    /// <summary>把流直接写入指定 blob 路径（用于分片：同一 uploadId 下的 PartNumber 固定对应一个文件，可覆盖重传）。</summary>
    Task<BlobWriteResult> WriteToPartAsync(string uploadId, int partNumber, Stream source, CancellationToken cancellationToken = default);

    /// <summary>按分片顺序拼接成最终对象体。</summary>
    Task<(string BlobPath, long Size)> ConcatPartsAsync(string uploadId, IEnumerable<string> orderedPartBlobPaths, CancellationToken cancellationToken = default);

    /// <summary>读取对象体，可指定闭区间范围。</summary>
    /// <param name="blobPath">对象体相对路径。</param>
    /// <param name="start">起始字节偏移（含）。</param>
    /// <param name="endInclusive">结束字节偏移（含）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<Stream> OpenReadAsync(string blobPath, long? start = null, long? endInclusive = null, CancellationToken cancellationToken = default);

    /// <summary>判断是否物理存在。</summary>
    bool Exists(string blobPath);

    /// <summary>删除对象体。</summary>
    Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default);

    /// <summary>删除某个分片上传的全部临时分片。</summary>
    Task DeletePartsAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>删除单个分片（用于重传失败或 ETag 不匹配时只回滚该分片）。</summary>
    Task DeletePartAsync(string uploadId, int partNumber, CancellationToken cancellationToken = default);

    /// <summary>清理残留的临时文件。</summary>
    Task CleanupTempAsync(CancellationToken cancellationToken = default);

    /// <summary>枚举全部对象体的相对路径，供孤儿回收比对。</summary>
    IEnumerable<string> EnumerateBlobPaths();

    /// <summary>返回对象体的最后写入时间（UTC），不存在时返回 <c>null</c>。</summary>
    DateTimeOffset? GetLastWriteTimeUtc(string blobPath);
}
