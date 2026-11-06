using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace S3ObjectStorage.S3Protocol.SigV4;

/// <summary>
/// AWS Signature Version 4 的静态工具：百分号编码、规范化请求、派生签名密钥。
/// </summary>
public static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string Service = "s3";
    public const string RequestKind = "aws4_request";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string StreamingPayload = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";
    /// <summary>带尾部校验和的分块负载（AWS SDK v4 默认开启 flexible checksum 时会用到）。</summary>
    public const string StreamingPayloadWithTrailer = "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER";

    /// <summary>
    /// 无签名分块负载：minio-go / mc 在非 TLS 下走这个变体。
    /// canonical request 里用它作为 payload hash，分块头只有十六进制长度（没有 chunk-signature），
    /// 校验和放在终止块之后的 trailer 里。
    /// </summary>
    public const string StreamingUnsignedPayloadTrailer = "STREAMING-UNSIGNED-PAYLOAD-TRAILER";
    public const string EmptySha256Hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    public const string DateFormat = "yyyyMMdd";
    public const string AmzDateFormat = "yyyyMMddTHHmmssZ";

    public static string HmacHex(byte[] key, string data)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data), mac);
        return Convert.ToHexStringLower(mac);
    }

    public static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    public static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region, string service)
    {
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, service);
        return Hmac(key, RequestKind);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string data) => Sha256Hex(Encoding.UTF8.GetBytes(data));

    /// <summary>判断是否为 aws-chunked 流式负载（三种声明值都要解码）。</summary>
    public static bool IsStreamingPayload(string? declared) =>
        string.Equals(declared, StreamingPayload, StringComparison.Ordinal)
        || string.Equals(declared, StreamingPayloadWithTrailer, StringComparison.Ordinal)
        || string.Equals(declared, StreamingUnsignedPayloadTrailer, StringComparison.Ordinal);

    /// <summary>该流式负载的分块是否不带签名链（UNSIGNED 变体）。</summary>
    public static bool IsUnsignedStreamingPayload(string? declared) =>
        string.Equals(declared, StreamingUnsignedPayloadTrailer, StringComparison.Ordinal);

    public static string BuildStringToSign(string amzDateTime, string credentialScope, string canonicalRequestHexHash) =>
        string.Concat(Algorithm, "\n", amzDateTime, "\n", credentialScope, "\n", canonicalRequestHexHash);

    /// <summary>RFC 3986 百分号编码，只保留 unreserved 字符。</summary>
    public static string PercentEncode(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '-' or '_' or '.' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>解码百分号转义（不把 '+' 当空格），用于还原客户端签名前的原文。</summary>
    public static string PercentDecode(string value)
    {
        if (!value.Contains('%'))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length
                && int.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add((byte)b);
                i += 2;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>构造规范化 URI：逐段百分号编码，保留 '/' 分隔。</summary>
    public static string CanonicalUri(string escapedPath)
    {
        if (escapedPath.Length == 0)
        {
            return "/";
        }

        var segments = escapedPath.Split('/');
        var sb = new StringBuilder(escapedPath.Length * 3);
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('/');
            }

            sb.Append(PercentEncode(segments[i]));
        }

        return sb.ToString();
    }

    /// <summary>构造规范化查询串（按名称、值排序），可忽略某些参数（如 X-Amz-Signature）。</summary>
    public static string CanonicalQueryString(string rawQuery, string? excludeParam = null)
    {
        rawQuery = rawQuery.TrimStart('?');
        if (rawQuery.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=', StringComparison.Ordinal);
            var name = idx < 0 ? pair : pair[..idx];
            var rawValue = idx < 0 ? string.Empty : pair[(idx + 1)..];

            if (string.Equals(PercentDecode(name), excludeParam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(PercentEncode(PercentDecode(name)), PercentEncode(PercentDecode(rawValue))));
        }

        pairs.Sort(static (a, b) =>
        {
            var cmp = string.CompareOrdinal(a.Key, b.Key);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Value, b.Value);
        });

        return string.Join('&', pairs.Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>规范化 header 的值：压缩连续空白并去首尾空格。</summary>
    public static string NormalizeHeaderValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value)
        {
            if (c is ' ' or '\t')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    public static string ComputeSignature(byte[] signingKey, string stringToSign) => HmacHex(signingKey, stringToSign);

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
}
