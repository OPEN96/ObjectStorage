using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Infrastructure.Options;

namespace S3ObjectStorage.Infrastructure.Storage;

/// <summary>
/// 本地文件系统实现的对象体存储。
/// 目录结构：<c>root/blobs/aa/bb/&lt;id&gt;.bin</c>、<c>root/parts/&lt;hex(uploadId)&gt;/&lt;partNumber&gt;.part</c>、<c>root/tmp/</c>。
/// </summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;
    private readonly int _bufferSize;
    private readonly ILogger<FileSystemBlobStore> _logger;

    public FileSystemBlobStore(IOptions<StorageOptions> options, ILogger<FileSystemBlobStore> logger)
    {
        _logger = logger;
        _bufferSize = options.Value.BufferSize <= 0 ? 128 * 1024 : options.Value.BufferSize;
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(Path.Combine(_root, "blobs"));
        Directory.CreateDirectory(Path.Combine(_root, "parts"));
        Directory.CreateDirectory(Path.Combine(_root, "tmp"));
    }

    public async Task<BlobWriteResult> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var tempPath = CreateTempFile();
        try
        {
            string md5;
            string sha256;
            long size;

            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, _bufferSize, FileOptions.Asynchronous))
            await using (var hashing = new HashingWriteStream(file, leaveOpen: true))
            {
                await source.CopyToAsync(hashing, _bufferSize, cancellationToken).ConfigureAwait(false);
                await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
                md5 = hashing.GetMd5Hex();
                sha256 = hashing.GetSha256Hex();
                size = hashing.BytesWritten;
            }

            var finalPath = MoveToBlob(tempPath);
            return new BlobWriteResult(finalPath, size, md5, sha256);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public async Task<BlobWriteResult> WriteToPartAsync(string uploadId, int partNumber, Stream source, CancellationToken cancellationToken = default)
    {
        if (partNumber is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        var partDir = GetPartDirectory(uploadId);
        Directory.CreateDirectory(partDir);
        var partPath = Path.Combine(partDir, partNumber.ToString("D5", CultureInfo.InvariantCulture) + ".part");
        var tempPath = CreateTempFile();

        try
        {
            string md5;
            long size;

            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, _bufferSize, FileOptions.Asynchronous))
            await using (var hashing = new HashingWriteStream(file, leaveOpen: true))
            {
                await source.CopyToAsync(hashing, _bufferSize, cancellationToken).ConfigureAwait(false);
                await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
                md5 = hashing.GetMd5Hex();
                size = hashing.BytesWritten;
            }

            File.Move(tempPath, partPath, overwrite: true);
            return new BlobWriteResult(ToRelative(partPath), size, md5, string.Empty);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public async Task<(string BlobPath, long Size)> ConcatPartsAsync(
        string uploadId,
        IEnumerable<string> orderedPartBlobPaths,
        CancellationToken cancellationToken = default)
    {
        var outTemp = CreateTempFile();
        long total = 0;
        try
        {
            await using (var output = new FileStream(outTemp, FileMode.Create, FileAccess.Write, FileShare.None, _bufferSize, FileOptions.Asynchronous))
            {
                foreach (var rel in orderedPartBlobPaths)
                {
                    var abs = Resolve(rel);
                    if (!File.Exists(abs))
                    {
                        throw new FileNotFoundException($"Part blob not found: {rel}", abs);
                    }

                    await using var input = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, _bufferSize, cancellationToken).ConfigureAwait(false);
                    total += input.Length;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var finalPath = MoveToBlob(outTemp);
            return (finalPath, total);
        }
        finally
        {
            TryDeleteFile(outTemp);
        }
    }

    public Task<Stream> OpenReadAsync(string blobPath, long? start = null, long? endInclusive = null, CancellationToken cancellationToken = default)
    {
        var abs = Resolve(blobPath);
        if (!File.Exists(abs))
        {
            throw new FileNotFoundException("Object data file is missing.", abs);
        }

        var stream = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (start is not { } offset)
        {
            return Task.FromResult<Stream>(stream);
        }

        stream.Seek(Math.Clamp(offset, 0, stream.Length), SeekOrigin.Begin);

        if (endInclusive is { } endInclusiveOffset)
        {
            var length = Math.Clamp(endInclusiveOffset - offset + 1, 0, stream.Length - stream.Position);
            return Task.FromResult<Stream>(new RangeReadStream(stream, length));
        }

        return Task.FromResult<Stream>(stream);
    }

    public bool Exists(string blobPath) => File.Exists(Resolve(blobPath));

    public Task DeleteAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        TryDeleteFile(Resolve(blobPath));
        return Task.CompletedTask;
    }

    public Task DeletePartsAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var dir = GetPartDirectory(uploadId);
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除分片目录失败: {Dir}", dir);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeletePartAsync(string uploadId, int partNumber, CancellationToken cancellationToken = default)
    {
        TryDeleteFile(Path.Combine(GetPartDirectory(uploadId), partNumber.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".part"));
        return Task.CompletedTask;
    }

    public Task CleanupTempAsync(CancellationToken cancellationToken = default)
    {
        var tmp = Path.Combine(_root, "tmp");
        if (!Directory.Exists(tmp))
        {
            return Task.CompletedTask;
        }

        var cutoff = DateTime.UtcNow.AddHours(-6);
        foreach (var file in Directory.EnumerateFiles(tmp))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理临时文件失败: {File}", file);
            }
        }

        return Task.CompletedTask;
    }

    public IEnumerable<string> EnumerateBlobPaths()
    {
        var blobs = Path.Combine(_root, "blobs");
        if (!Directory.Exists(blobs))
        {
            yield break;
        }

        foreach (var first in Directory.EnumerateDirectories(blobs))
        {
            foreach (var second in Directory.EnumerateDirectories(first))
            {
                foreach (var file in Directory.EnumerateFiles(second, "*.bin"))
                {
                    yield return Path.GetRelativePath(_root, file).Replace('\\', '/');
                }
            }
        }
    }

    public DateTimeOffset? GetLastWriteTimeUtc(string blobPath)
    {
        try
        {
            var info = new FileInfo(Resolve(blobPath));
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string CreateTempFile() => Path.Combine(_root, "tmp", Guid.NewGuid().ToString("N") + ".tmp");

    private string MoveToBlob(string tempPath)
    {
        var fileName = Guid.NewGuid().ToString("N") + ".bin";
        var rel = Path.Combine("blobs", CryptoPath.Shard(fileName)).Replace('\\', '/');
        var abs = Resolve(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.Move(tempPath, abs, overwrite: true);
        return rel;
    }

    private string GetPartDirectory(string uploadId) => Path.Combine(_root, "parts", CryptoPath.SafeHex(uploadId));

    private string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Blob 路径不能为空。", nameof(relativePath));
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var abs = Path.GetFullPath(Path.Combine(_root, normalized));

        // 必须落在根目录之内：前缀比较要带上目录分隔符，避免 /data2 被 /data 放行。
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;

        if (!abs.StartsWith(rootWithSeparator, comparison) && !string.Equals(abs, _root, comparison))
        {
            throw new UnauthorizedAccessException("Attempted to access a path outside the storage root.");
        }

        return abs;
    }

    private string ToRelative(string absolutePath) => Path.GetRelativePath(_root, absolutePath).Replace('\\', '/');

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "删除文件失败: {Path}", path);
        }
    }

    /// <summary>限制可读字节数的只读包装流。</summary>
    private sealed class RangeReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public RangeReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _remaining;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var span = new Span<byte>(buffer, offset, count);
            return Read(span);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var requested = (int)Math.Min(buffer.Length, _remaining);
            var read = _inner.Read(buffer[..requested]);
            _remaining -= read;
            return read;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var requested = (int)Math.Min(buffer.Length, _remaining);
            var read = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
