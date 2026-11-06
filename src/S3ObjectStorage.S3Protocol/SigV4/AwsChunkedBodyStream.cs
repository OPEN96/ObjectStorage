using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using S3ObjectStorage.Domain.Exceptions;

namespace S3ObjectStorage.S3Protocol.SigV4;

/// <summary>
/// 解码 <c>aws-chunked</c> 请求体，覆盖三种 S3/MinIO 流式负载声明：
/// <list type="bullet">
/// <item>STREAMING-AWS4-HMAC-SHA256-PAYLOAD：每个分块带 chunk-signature，需校验签名链；</item>
/// <item>STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER：同上，终止块后还有 checksum trailer；</item>
/// <item>STREAMING-UNSIGNED-PAYLOAD-TRAILER：minio-go / mc 在 HTTP（非 TLS）下使用，
///       分块头只有十六进制长度、不带签名，校验和放在 trailer 中。</item>
/// </list>
/// 解码后向上层暴露的是纯粹的 payload 字节流，chunk 头、CRLF 与 trailer 都不会写入对象。
/// </summary>
public sealed class AwsChunkedBodyStream : Stream
{
    private const int MaxHeaderLineLength = 1024;
    private const int MaxTrailerLines = 64;

    private readonly Stream _inner;
    private readonly SigV4Seed? _seed;
    private readonly bool _unsigned;
    private readonly byte[] _crlfProbe = new byte[1];

    private byte[] _lineBuffer = new byte[2048];
    private int _bufferStart;
    private int _bufferEnd;
    private long _chunkRemaining;
    private bool _finished;
    private string _previousSignature;
    private string? _pendingSignature;
    private IncrementalHash? _hash;

    private AwsChunkedBodyStream(Stream inner, SigV4Seed? seed, bool unsigned)
    {
        _inner = inner;
        _seed = seed;
        _unsigned = unsigned;
        _previousSignature = seed?.SeedSignature ?? string.Empty;
    }

    /// <summary>按需决定是否用 aws-chunked 解码器包装请求体。</summary>
    public static Stream Wrap(Stream body, string? declaredPayloadHash, SigV4Seed? seed)
    {
        if (!SigV4.IsStreamingPayload(declaredPayloadHash))
        {
            return body;
        }

        var unsigned = SigV4.IsUnsignedStreamingPayload(declaredPayloadHash);
        // 带签名链的模式必须有签名种子；UNSIGNED 变体不需要（也可能来自匿名/降级通路）。
        if (!unsigned && seed is null)
        {
            return body;
        }

        return new AwsChunkedBodyStream(body, seed, unsigned);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => 0;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            return 0;
        }

        while (_chunkRemaining == 0)
        {
            if (!await ReadNextChunkHeaderAsync(cancellationToken).ConfigureAwait(false))
            {
                _finished = true;
                return 0;
            }
        }

        var requested = (int)Math.Min(buffer.Length, _chunkRemaining);

        int read;
        if (_bufferEnd > _bufferStart)
        {
            read = Math.Min(requested, _bufferEnd - _bufferStart);
            _lineBuffer.AsSpan(_bufferStart, read).CopyTo(buffer.Span);
            _bufferStart += read;
        }
        else
        {
            read = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("aws-chunked 请求体在分块数据处提前结束。");
            }
        }

        _chunkRemaining -= read;
        if (!_unsigned)
        {
            _hash!.AppendData(buffer.Span[..read]);
        }

        if (_chunkRemaining == 0)
        {
            await ExpectCrlfAsync(cancellationToken).ConfigureAwait(false);
            ValidateCurrentChunk();
        }

        return read;
    }

    /// <summary>
    /// 读取下一个块头。带签名的格式为
    /// <c>&lt;hex-size&gt;;chunk-signature=&lt;hex&gt;\r\n</c>，
    /// UNSIGNED 变体为 <c>&lt;hex-size&gt;\r\n</c>；size = 0 表示终止块。
    /// </summary>
    private async Task<bool> ReadNextChunkHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(';', StringComparison.Ordinal);
            var sizeText = separator < 0 ? line : line[..separator];

            if (!long.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
            {
                throw new IOException("aws-chunked 块大小非法。");
            }

            const string signaturePrefix = "chunk-signature=";
            var signatureIndex = line.IndexOf(signaturePrefix, StringComparison.Ordinal);
            _pendingSignature = signatureIndex >= 0
                ? line[(signatureIndex + signaturePrefix.Length)..].Trim()
                : null;

            if (size == 0)
            {
                // 终止块之后可能还有 checksum trailer（以空行结束），必须消费掉，
                // 否则 Kestrel 会在下一个请求复用连接时读到脏数据。
                await ConsumeTrailersAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!_unsigned && string.IsNullOrEmpty(_pendingSignature))
            {
                throw new S3Exception(S3ErrorCode.SignatureDoesNotMatch, "aws-chunked 分块缺少 chunk-signature");
            }

            _chunkRemaining = size;
            if (!_unsigned)
            {
                _hash?.Dispose();
                _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }

            return true;
        }
    }

    private void ValidateCurrentChunk()
    {
        // UNSIGNED 变体没有签名链，完整性依赖 trailer checksum 与 TLS（本服务由客户端自行选择）。
        if (_unsigned || _pendingSignature is null || _seed is null)
        {
            return;
        }

        var currentHash = Convert.ToHexStringLower(_hash!.GetHashAndReset());
        var stringToSign = string.Concat(
            "AWS4-HMAC-SHA256-PAYLOAD", "\n",
            _seed.AmzDate, "\n",
            _seed.Scope, "\n",
            _previousSignature, "\n",
            SigV4.EmptySha256Hex, "\n",
            currentHash);

        var expected = SigV4.ComputeSignature(_seed.SigningKey, stringToSign);
        if (!SigV4.FixedTimeEquals(expected, _pendingSignature))
        {
            throw new S3Exception(S3ErrorCode.SignatureDoesNotMatch);
        }

        _previousSignature = expected;
    }

    /// <summary>消费终止块之后的 trailer 头，直到空行或 EOF。</summary>
    private async Task ConsumeTrailersAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < MaxTrailerLines; i++)
        {
            if (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: 0 })
            {
                return;
            }
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            for (var i = _bufferStart; i < _bufferEnd; i++)
            {
                if (_lineBuffer[i] != (byte)'\n')
                {
                    continue;
                }

                var endExclusive = i > _bufferStart && _lineBuffer[i - 1] == (byte)'\r' ? i - 1 : i;
                var line = Encoding.ASCII.GetString(_lineBuffer, _bufferStart, endExclusive - _bufferStart);
                _bufferStart = i + 1;
                return line;
            }

            if (_bufferEnd - _bufferStart > MaxHeaderLineLength)
            {
                throw new IOException("aws-chunked 块头过长。");
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                var remainder = Encoding.ASCII.GetString(_lineBuffer, _bufferStart, _bufferEnd - _bufferStart);
                _bufferStart = _bufferEnd;
                return remainder;
            }
        }
    }

    /// <summary>消费一个分块结尾的 CRLF。</summary>
    private async Task ExpectCrlfAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 2; i++)
        {
            if (_bufferEnd > _bufferStart)
            {
                _bufferStart++;
                continue;
            }

            var read = await _inner.ReadAsync(_crlfProbe, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_bufferStart > 0)
        {
            Buffer.BlockCopy(_lineBuffer, _bufferStart, _lineBuffer, 0, _bufferEnd - _bufferStart);
            _bufferEnd -= _bufferStart;
            _bufferStart = 0;
        }

        if (_lineBuffer.Length - _bufferEnd < 256)
        {
            Array.Resize(ref _lineBuffer, Math.Min(_lineBuffer.Length * 2, 64 * 1024));
        }

        var read = await _inner.ReadAsync(_lineBuffer.AsMemory(_bufferEnd), cancellationToken).ConfigureAwait(false);
        _bufferEnd += read;
        return read > 0;
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
            _hash?.Dispose();
        }

        base.Dispose(disposing);
    }
}
