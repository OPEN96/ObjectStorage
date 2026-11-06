using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace S3ObjectStorage.Api;

/// <summary>
/// 反向代理（TLS 终止 / 负载均衡）相关配置。
/// </summary>
/// <remarks>
/// 默认关闭。开启后只有直连对端命中 <see cref="KnownProxies"/> 时才采纳
/// <c>X-Forwarded-For/Proto/Host</c>，否则任何客户端都能伪造来源 IP，
/// 既会绕过登录节流，也会让审计日志失真。
/// </remarks>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>是否信任 <c>X-Forwarded-*</c> 请求头。</summary>
    public bool Enabled { get; set; }

    /// <summary>可信代理的 IP 或 CIDR，例如 <c>127.0.0.1</c>、<c>172.17.0.0/16</c>。</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>最多信任几层代理转发，默认 1（只信任最靠近服务端的那一层）。</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// 把本配置映射到 ASP.NET Core 的转发头选项：先清空框架自带的 loopback 白名单，
    /// 再按 <see cref="KnownProxies"/> 逐条加入，避免"默认信任本机"在容器里被误用。
    /// </summary>
    /// <exception cref="InvalidOperationException">条目不是合法 IP/CIDR，或一条都没配。</exception>
    public static void Apply(ForwardedHeadersOptions target, ReverseProxyOptions source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        target.ForwardLimit = source.ForwardLimit > 0 ? source.ForwardLimit : 1;
        target.KnownProxies.Clear();
        target.KnownIPNetworks.Clear();

        foreach (var raw in source.KnownProxies)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (!TryParseCidr(entry, out var address, out var prefix))
                {
                    throw new InvalidOperationException(
                        $"ReverseProxy:KnownProxies 中的 '{entry}' 不是合法的 CIDR。");
                }

                target.KnownIPNetworks.Add(new System.Net.IPNetwork(address, prefix));
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                target.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"ReverseProxy:KnownProxies 中的 '{entry}' 不是合法的 IP 或 CIDR。");
            }
        }

        if (target.KnownProxies.Count == 0 && target.KnownIPNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "ReverseProxy:Enabled=true 时必须至少配置一个 ReverseProxy:KnownProxies 条目"
                + "（例如部署在宿主机 nginx 后面时填 127.0.0.1，部署在 Compose 内则填代理容器的网段）。");
        }
    }

    private static bool TryParseCidr(string value, out IPAddress address, out int prefix)
    {
        address = IPAddress.None;
        prefix = 0;

        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0
            || !IPAddress.TryParse(value[..separator], out var parsed)
            || !int.TryParse(value[(separator + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var bits))
        {
            return false;
        }

        var maxBits = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        // 前缀 0（0.0.0.0/0、::/0）等于信任所有来源，会让任何客户端伪造 X-Forwarded-For，直接拒绝。
        if (bits <= 0 || bits > maxBits)
        {
            return false;
        }

        address = parsed;
        prefix = bits;
        return true;
    }
}
