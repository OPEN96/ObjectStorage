using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using S3ObjectStorage.S3Protocol.Options;

namespace S3ObjectStorage.S3Protocol.Routing;

/// <summary>
/// 把 HTTP 请求解析成 S3 的 (bucket, key)，同时支持 path-style 与 virtual-hosted-style。
/// </summary>
public static class S3Routing
{
    public static S3Route Resolve(HttpRequest request, S3ServerOptions options)
    {
        string? hostBucket = null;
        if (!string.IsNullOrWhiteSpace(options.VirtualHostSuffix)
            && request.Headers.TryGetValue("Host", out StringValues hostValue))
        {
            var host = hostValue.ToString().Split(':')[0].TrimEnd('.');
            var suffix = options.VirtualHostSuffix.Trim('.');

            if (host.Length > suffix.Length
                && host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase))
            {
                hostBucket = host[..(host.Length - suffix.Length - 1)];
            }
        }

        var path = request.Path.Value ?? "/";
        var segments = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? pathBucket = segments.Length > 0 ? Uri.UnescapeDataString(segments[0]) : null;

        string? bucket;
        IEnumerable<string> keySegments;
        var virtualHostStyle = false;

        if (hostBucket is null)
        {
            bucket = pathBucket;
            keySegments = segments.Skip(1);
        }
        else
        {
            bucket = hostBucket;
            keySegments = segments;
            virtualHostStyle = true;
        }

        var keys = keySegments.Select(Uri.UnescapeDataString).ToList();
        var key = keys.Count > 0 ? string.Join('/', keys) : null;

        return new S3Route(
            string.IsNullOrEmpty(bucket) ? null : bucket,
            string.IsNullOrEmpty(key) ? null : key,
            virtualHostStyle);
    }
}

/// <summary>S3 桶名规则校验（RFC 风格简化版）。</summary>
public static class BucketNameValidator
{
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Length is < 3 or > 63)
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(name, out _))
        {
            return false;
        }

        foreach (var c in name)
        {
            // S3 桶名必须全小写；大写会让客户端与虚拟主机寻址、URL 规范化产生歧义。
            if (c is >= 'A' and <= 'Z')
            {
                return false;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            {
                continue;
            }

            return false;
        }

        // 连续的 '.' 不是合法的主机名片段（AWS 明确禁止）。
        return !name.Contains("..", StringComparison.Ordinal);
    }
}
