namespace S3ObjectStorage.S3Protocol.Options;

/// <summary>
/// 兼容层（协议与鉴权）的服务配置。
/// </summary>
public sealed class S3ServerOptions
{
    public const string SectionName = "S3";

    /// <summary>对外宣称的区域名；aws cli / SDK 需要与之保持一致，除非关闭 EnforceRegion。</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>是否强制校验请求里的区域与 Region 一致。</summary>
    public bool EnforceRegion { get; set; }

    /// <summary>允许的请求时间与服务器时间的最大偏差。</summary>
    public int TimestampSkewSeconds { get; set; } = 15 * 60;

    /// <summary>
    /// 虚拟主机寻址风格的域名后缀，例如 <c>s3.localhost</c>。
    /// 为空时只支持 path-style（<c>/bucket/key</c>）。
    /// </summary>
    public string? VirtualHostSuffix { get; set; }

    /// <summary>是否要求请求必须携带签名；关闭后视为匿名通路（仅供本地调试）。</summary>
    public bool RequireAuthentication { get; set; } = true;

    /// <summary>单 key 的最大长度（S3 限制 1024 字节）。</summary>
    public int MaxKeyLength { get; set; } = 1024;
}
