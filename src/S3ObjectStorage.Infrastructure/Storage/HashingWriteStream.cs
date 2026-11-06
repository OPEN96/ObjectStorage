using System.Security.Cryptography;
using System.Text;

namespace S3ObjectStorage.Infrastructure.Storage;

/// <summary>
/// 把 MD5 / SHA256 的计算挂在写入目标流的路径上，实现「边写边算」，
/// 避免为了算摘要把整个对象再读一遍。
/// </summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _md5;
    private readonly IncrementalHash _sha256;
    private readonly bool _leaveOpen;

    public HashingWriteStream(Stream inner, bool leaveOpen = false)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
        _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _md5.AppendData(buffer, offset, count);
        _sha256.AppendData(buffer, offset, count);
        BytesWritten += count;
        _inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Append(buffer.Span);
        BytesWritten += buffer.Length;
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Append(buffer);
        BytesWritten += buffer.Length;
        _inner.Write(buffer);
    }

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    private void Append(ReadOnlySpan<byte> span)
    {
        _md5.AppendData(span);
        _sha256.AppendData(span);
    }

    public string GetMd5Hex() => Convert.ToHexStringLower(_md5.GetHashAndReset());

    public string GetSha256Hex() => Convert.ToHexStringLower(_sha256.GetHashAndReset());

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _md5.Dispose();
            _sha256.Dispose();
            if (!_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// 辅助：把任意 uploadId 之类的外部输入映射成安全的目录名。
/// </summary>
internal static class CryptoPath
{
    public static string SafeHex(string raw)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(raw), hash);
        return Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>把 Guid 打成两级目录，例如 <c>ab/cd/abcdef...</c>。</summary>
    public static string Shard(string fileName)
    {
        var h = fileName.Length >= 4 ? fileName : fileName.PadRight(4, '0');
        return Path.Combine(h[..2], h[2..4], fileName);
    }
}
