using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Options;
using S3ObjectStorage.S3Protocol.Routing;

namespace S3ObjectStorage.S3Protocol.SigV4;

/// <summary>
/// AWS SigV4 鉴权：既支持 Authorization 头签名，也支持预签名 URL（Query 参数），
/// 同时处理 <c>aws-chunked</c>（STREAMING-AWS4-HMAC-SHA256-PAYLOAD）负载场景。
/// </summary>
public sealed class SigV4Validator(
    IAccessKeyStore accessKeys,
    IOptions<S3ServerOptions> options,
    ILogger<SigV4Validator> logger)
{
    private readonly S3ServerOptions _options = options.Value;

    public async Task<S3Principal> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var request = context.Request;
        var isPresignedUrl = request.Query.ContainsKey("X-Amz-Signature");

        string? accessKeyId;
        string? credentialScope;
        string? signedHeadersRaw;
        string? providedSignature;
        string? amzDate;

        if (isPresignedUrl)
        {
            var query = request.Query;
            accessKeyId = SplitCredential(query["X-Amz-Credential"].ToString(), out credentialScope);
            signedHeadersRaw = query["X-Amz-SignedHeaders"].ToString();
            providedSignature = query["X-Amz-Signature"].ToString();
            amzDate = query["X-Amz-Date"].ToString();

            if (query["X-Amz-Algorithm"].ToString() is not SigV4.Algorithm)
            {
                throw new S3Exception(S3ErrorCode.InvalidArgument, "X-Amz-Algorithm 必须是 " + SigV4.Algorithm);
            }

            if (!int.TryParse(query["X-Amz-Expires"].ToString(), CultureInfo.InvariantCulture, out var expires))
            {
                expires = 3600;
            }

            expires = Math.Clamp(expires, 1, 604800);
            var timestamp = ParseAmzDate(amzDate);
            var elapsed = (DateTimeOffset.UtcNow - timestamp).TotalSeconds;
            if (elapsed > expires)
            {
                logger.LogWarning("预签名 URL 已过期：{Elapsed}s > {Expires}s", elapsed, expires);
                throw new S3Exception(S3ErrorCode.AccessDenied, "Request has expired");
            }
        }
        else
        {
            if (!request.Headers.TryGetValue("Authorization", out var authHeader) || authHeader.Count == 0)
            {
                throw new S3Exception(S3ErrorCode.MissingSignature, "Missing required header: Authorization");
            }

            var authorization = authHeader.ToString();
            if (!authorization.StartsWith(SigV4.Algorithm + " ", StringComparison.Ordinal))
            {
                throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, $"Algorithm must be {SigV4.Algorithm}");
            }

            var parameters = ParseComposite(authorization[(SigV4.Algorithm.Length + 1)..]);
            if (!parameters.TryGetValue("Credential", out var credential)
                || !parameters.TryGetValue("SignedHeaders", out signedHeadersRaw)
                || !parameters.TryGetValue("Signature", out providedSignature))
            {
                throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "Credential, SignedHeaders 与 Signature 均不可缺失");
            }

            accessKeyId = SplitCredential(credential, out credentialScope);
            amzDate = request.Headers["X-Amz-Date"].ToString();
        }

        if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(credentialScope))
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "Credential 格式非法：应为 ACCESS_KEY/date/region/service/aws4_request");
        }

        ValidateTimestamp(amzDate, out var dateStamp);
        ValidateScope(credentialScope, amzDate!, out var region, out var service);

        if (!string.Equals(service, SigV4.Service, StringComparison.Ordinal))
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, $"Credential 中的 service 必须为 {SigV4.Service}");
        }

        if (_options.EnforceRegion && !string.Equals(region, _options.Region, StringComparison.Ordinal))
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, $"Credential 中的 region 必须为 {_options.Region}");
        }

        var key = await accessKeys.FindAsync(accessKeyId, cancellationToken);
        if (key is null || !key.Enabled)
        {
            throw new S3Exception(S3ErrorCode.InvalidAccessKeyId);
        }

        var canonicalUri = SigV4.CanonicalUri(request.Path.Value ?? "/");
        var canonicalQuery = SigV4.CanonicalQueryString(request.QueryString.Value ?? string.Empty, isPresignedUrl ? "X-Amz-Signature" : null);
        var canonicalHeaders = BuildCanonicalHeaders(request, signedHeadersRaw, out var signedHeaderList);

        var payloadHashCandidates = GetPayloadHashCandidates(request, isPresignedUrl);

        var signingKey = SigV4.DeriveSigningKey(key.SecretAccessKey, dateStamp, region, service);
        var matched = false;

        foreach (var payloadHash in payloadHashCandidates)
        {
            var canonicalRequest = string.Concat(
                request.Method, "\n",
                canonicalUri, "\n",
                canonicalQuery, "\n",
                canonicalHeaders, "\n",
                signedHeaderList, "\n",
                payloadHash);

            var stringToSign = SigV4.BuildStringToSign(amzDate!, credentialScope, SigV4.Sha256Hex(canonicalRequest));
            var expected = SigV4.ComputeSignature(signingKey, stringToSign);

            if (SigV4.FixedTimeEquals(expected, providedSignature ?? string.Empty))
            {
                matched = true;
                break;
            }

            logger.LogDebug(
                "签名不匹配 canonicalUri={CanonicalUri} payloadHash={PayloadHash} expected={Expected}",
                canonicalUri, payloadHash, expected);
        }

        if (!matched)
        {
            logger.LogWarning(
                "签名校验失败 accessKeyId={AccessKeyId} method={Method} path={Path}",
                accessKeyId, request.Method, request.Path);
            throw new S3Exception(S3ErrorCode.SignatureDoesNotMatch);
        }

        // 记录本次签名的上下文，供 aws-chunked 分片签名链与负载哈希校验使用。
        var declared = request.Headers["X-Amz-Content-Sha256"].ToString();
        var seed = new SigV4Seed(signingKey, credentialScope, amzDate!, providedSignature!);
        context.Items[S3RequestItems.SigV4Seed] = seed;
        context.Items[S3RequestItems.DeclaredPayloadHash] = string.IsNullOrEmpty(declared) ? SigV4.UnsignedPayload : declared;

        return new S3Principal(key.AccessKeyId, key.AccessKeyId, string.IsNullOrEmpty(key.DisplayName) ? key.AccessKeyId : key.DisplayName, key.IsAdmin);
    }

    private static string BuildCanonicalHeaders(HttpRequest request, string? signedHeadersRaw, out string signedHeaderList)
    {
        if (string.IsNullOrWhiteSpace(signedHeadersRaw))
        {
            signedHeaderList = string.Empty;
            return string.Empty;
        }

        var names = signedHeadersRaw.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim().ToLowerInvariant())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var sb = new System.Text.StringBuilder();
        foreach (var name in names)
        {
            var value = string.Empty;
            switch (name)
            {
                case "content-length":
                    value = (request.ContentLength ?? 0).ToString(CultureInfo.InvariantCulture);
                    break;
                case "content-type":
                    value = request.ContentType ?? string.Empty;
                    break;
                default:
                    if (request.Headers.TryGetValue(name, out var headerValue))
                    {
                        value = string.Join(',', headerValue.Select(v => v ?? string.Empty));
                    }

                    break;
            }

            sb.Append(name).Append(':').Append(SigV4.NormalizeHeaderValue(value)).Append('\n');
        }

        signedHeaderList = string.Join(';', names);
        return sb.ToString();
    }

    /// <summary>
    /// 负载哈希候选：优先使用客户端声明的值；未声明时按 S3 约定尝试 UNSIGNED-PAYLOAD 与空负载哈希。
    /// </summary>
    private static List<string> GetPayloadHashCandidates(HttpRequest request, bool isPresignedUrl)
    {
        var declared = request.Headers["X-Amz-Content-Sha256"].ToString();
        var candidates = new List<string>();

        if (isPresignedUrl)
        {
            return [SigV4.UnsignedPayload];
        }

        if (!string.IsNullOrEmpty(declared))
        {
            candidates.Add(declared);
            if (string.Equals(declared, SigV4.UnsignedPayload, StringComparison.Ordinal))
            {
                candidates.Add(SigV4.EmptySha256Hex);
            }

            return candidates;
        }

        candidates.Add(SigV4.UnsignedPayload);
        candidates.Add(SigV4.EmptySha256Hex);
        return candidates;
    }

    private void ValidateTimestamp(string? amzDate, out string dateStamp)
    {
        if (string.IsNullOrEmpty(amzDate))
        {
            throw new S3Exception(S3ErrorCode.MissingSecurityHeader, "缺少 X-Amz-Date");
        }

        var timestamp = ParseAmzDate(amzDate);
        var skew = (DateTimeOffset.UtcNow - timestamp).Duration().TotalSeconds;
        if (skew > _options.TimestampSkewSeconds)
        {
            throw new S3Exception(S3ErrorCode.RequestTimeTooSkewed);
        }

        dateStamp = amzDate.Length >= 8 ? amzDate[..8] : string.Empty;
    }

    /// <summary>解析 Credential 的 scope：<c>date/region/service/aws4_request</c>。</summary>
    private static void ValidateScope(string credentialScope, string amzDate, out string region, out string service)
    {
        var parts = credentialScope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "Credential scope 应为 date/region/service/aws4_request");
        }

        if (!string.Equals(parts[0], amzDate[..Math.Min(8, amzDate.Length)], StringComparison.Ordinal))
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "Credential 中的日期与 X-Amz-Date 不一致");
        }

        if (!string.Equals(parts[3], SigV4.RequestKind, StringComparison.Ordinal))
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "Credential scope 必须以 aws4_request 结尾");
        }

        region = parts[1];
        service = parts[2];
    }

    private static DateTimeOffset ParseAmzDate(string? amzDate)
    {
        if (string.IsNullOrEmpty(amzDate)
            || !DateTimeOffset.TryParseExact(amzDate, SigV4.AmzDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new S3Exception(S3ErrorCode.AuthorizationHeaderMalformed, "X-Amz-Date 格式非法，应为 " + SigV4.AmzDateFormat);
        }

        return parsed;
    }

    private static string SplitCredential(string credential, out string scope)
    {
        scope = string.Empty;
        if (string.IsNullOrEmpty(credential))
        {
            return string.Empty;
        }

        var index = credential.IndexOf('/', StringComparison.Ordinal);
        if (index < 0)
        {
            return credential;
        }

        scope = credential[(index + 1)..].Trim();
        return credential[..index].Trim();
    }

    private static Dictionary<string, string> ParseComposite(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            var eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var name = token[..eq].Trim();
            result[name] = token[(eq + 1)..].Trim().Trim('"');
        }

        return result;
    }
}

public static class S3RequestItems
{
    public const string SigV4Seed = "S3.SigV4Seed";
    public const string DeclaredPayloadHash = "S3.DeclaredPayloadHash";
    public const string RequestId = "S3.RequestId";
}
