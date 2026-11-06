namespace S3ObjectStorage.S3Protocol.SigV4;

/// <summary>
/// 签名链种子：分片上传的 <c>aws-chunked</c> 负载会以 header 签名作为第一个 chunk 的前置签名。
/// </summary>
/// <param name="SigningKey">派生的签名密钥。</param>
/// <param name="Scope">credential scope：date/region/service/aws4_request。</param>
/// <param name="AmzDate">ISO 时间戳。</param>
/// <param name="SeedSignature">Authorization 头里的签名。</param>
public sealed record SigV4Seed(byte[] SigningKey, string Scope, string AmzDate, string SeedSignature);
