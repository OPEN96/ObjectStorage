using System.Globalization;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 为测试手写 SigV4 Authorization 头。只签名 host / x-amz-content-sha256 / x-amz-date，
/// 便于在不依赖 SDK 的情况下构造边界请求（例如错误的 Content-MD5、超大 XML）。
/// </summary>
internal static class RawSigner
{
    public static void Sign(
        HttpRequestMessage request,
        string serviceUrl,
        string method,
        string path,
        string rawQuery,
        string payloadHash)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString(SigV4.AmzDateFormat, CultureInfo.InvariantCulture);
        var dateStamp = now.ToString(SigV4.DateFormat, CultureInfo.InvariantCulture);
        const string region = "us-east-1";
        var scope = $"{dateStamp}/{region}/{SigV4.Service}/{SigV4.RequestKind}";
        var host = new Uri(serviceUrl).Authority;

        var signedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };

        var canonicalHeaders = string.Concat(signedHeaders.Select(p => $"{p.Key}:{SigV4.NormalizeHeaderValue(p.Value)}\n"));
        var signedHeaderList = string.Join(';', signedHeaders.Keys);
        var canonicalRequest = string.Concat(
            method, "\n",
            SigV4.CanonicalUri(path), "\n",
            SigV4.CanonicalQueryString(rawQuery), "\n",
            canonicalHeaders, "\n",
            signedHeaderList, "\n",
            payloadHash);

        var stringToSign = SigV4.BuildStringToSign(amzDate, scope, SigV4.Sha256Hex(canonicalRequest));
        var signingKey = SigV4.DeriveSigningKey(S3TestHost.SecretKey, dateStamp, region, SigV4.Service);
        var signature = SigV4.ComputeSignature(signingKey, stringToSign);

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{SigV4.Algorithm} Credential={S3TestHost.AccessKey}/{scope}, SignedHeaders={signedHeaderList}, Signature={signature}");
    }
}
