namespace S3ObjectStorage.S3Protocol.Xml;

/// <summary>
/// S3 的 <c>encoding-type=url</c> 支持。
/// 客户端（mc / MinIO SDK / AWS SDK）在列举请求里带上该参数时，期望响应中的
/// Key、Prefix、Delimiter 等字段按 RFC 3986 百分号编码，并回带
/// <c>&lt;EncodingType&gt;url&lt;/EncodingType&gt;</c>。
///
/// 特别注意：空格必须编码为 %20、'+' 必须编码为 %2B。否则部分客户端会用
/// form-urlencoded 规则解码，把 '+' 还原成空格，导致对象 key 被静默改写。
/// </summary>
public static class S3UrlEncoding
{
    public const string Url = "url";

    /// <summary>判断请求是否要求 url 编码（大小写不敏感，其它值按不编码处理）。</summary>
    public static bool IsRequested(string? encodingType) =>
        string.Equals(encodingType, Url, StringComparison.OrdinalIgnoreCase);

    /// <summary>按需编码；未启用或值为 null 时原样返回。</summary>
    public static string? Encode(string? value, bool enabled) =>
        enabled && value is not null ? Encode(value) : value;

    /// <summary>按需编码非空字符串。</summary>
    public static string EncodeValue(string value, bool enabled) => enabled ? Encode(value) : value;

    /// <summary>
    /// RFC 3986 百分号编码，但保留 key 中作为层级分隔符的 '/'
    /// （S3 的 encoding-type=url 不对 '/' 编码）。
    /// </summary>
    public static string Encode(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
    }
}
