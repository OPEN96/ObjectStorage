using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using S3ObjectStorage.Domain.Abstractions;

namespace S3ObjectStorage.Api.Admin;

/// <summary>管理端登录身份。</summary>
/// <param name="Subject">令牌登录为 <c>token</c>，密钥登录为 AccessKeyId。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="ViaAccessKey">是否用 S3 访问密钥登录。</param>
/// <param name="Fingerprint">密钥登录时的凭证指纹，用于把会话绑定到当前 Secret。</param>
public sealed record AdminIdentity(string Subject, string DisplayName, bool ViaAccessKey, string? Fingerprint = null);

/// <summary>
/// 管理端认证：支持两种登录方式。
/// <list type="number">
///   <item>管理员令牌（配置项 <c>Admin:Token</c>），适合单人或运维使用；</item>
///   <item>标记为管理员（<c>IsAdmin</c>）的 S3 访问密钥 + Secret，适合多人各用各的凭证。</item>
/// </list>
/// 登录成功后签发 HMAC 签名的 HttpOnly Cookie，服务端无需保存会话状态。
/// </summary>
public sealed class AdminAuth(
    IOptions<AdminOptions> optionsAccessor,
    IAccessKeyStore accessKeys,
    ILogger<AdminAuth> logger)
{
    public const string CookieName = "s3admin_session";

    private const string CookieVersion = "v1";

    private readonly AdminOptions _options = optionsAccessor.Value;

    private readonly byte[] _signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(optionsAccessor.Value.Token ?? string.Empty));

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// 令牌为空时的处理：开发环境生成随机令牌并打到日志；生产环境直接拒绝启动，
    /// 避免把管理口令写进日志、或以空令牌对外提供服务。
    /// </summary>
    public static string EnsureToken(AdminOptions options, ILogger logger, bool isDevelopment)
    {
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            return options.Token;
        }

        if (!options.Enabled)
        {
            return string.Empty;
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "生产环境必须配置管理端令牌：设置环境变量 S3STORAGE_Admin__Token（至少 24 位随机字符），" +
                "或设置 Admin:Enabled=false 关闭管理端。");
        }

        var generated = "adm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        options.Token = generated;
        logger.LogWarning(
            "未配置 Admin:Token，已生成随机管理端令牌：{Token}（可用 S3STORAGE_Admin__Token 固定下来）",
            generated);

        return generated;
    }

    /// <summary>从请求里解析管理端身份；未登录返回 null。</summary>
    public async Task<AdminIdentity?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var headerToken = ReadHeaderToken(context.Request);
        if (headerToken is not null && FixedTimeEquals(headerToken, _options.Token))
        {
            return new AdminIdentity("token", "管理员令牌", ViaAccessKey: false);
        }

        var cookie = context.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(cookie) && TryReadCookie(cookie, out var payload))
        {
            // 令牌登录的会话与具体密钥无关，直接放行；密钥登录的会话必须回查密钥状态，
            // 否则「禁用 / 删除密钥」「轮换 Secret」都无法撤销已经签发的会话。
            if (!payload.Via)
            {
                return new AdminIdentity(payload.Sub, payload.Name, ViaAccessKey: false);
            }

            var current = await accessKeys.FindAsync(payload.Sub, cancellationToken);
            if (current is null
                || !current.Enabled
                || !current.IsAdmin
                || !string.Equals(payload.Fp, Fingerprint(current.SecretAccessKey), StringComparison.Ordinal))
            {
                logger.LogWarning("管理端会话已失效：密钥不存在、被禁用或 Secret 已轮换 subject={Subject}", payload.Sub);
                return null;
            }

            return new AdminIdentity(current.AccessKeyId, current.DisplayName, ViaAccessKey: true, payload.Fp);
        }

        // 允许直接用管理员 AccessKey 调接口（curl / 自动化脚本），格式：Authorization: Bearer <AccessKeyId>:<SecretAccessKey>
        if (headerToken is not null && headerToken.Contains(':', StringComparison.Ordinal))
        {
            var separator = headerToken.IndexOf(':', StringComparison.Ordinal);
            var keyId = headerToken[..separator];
            var secret = headerToken[(separator + 1)..];

            var key = await accessKeys.FindAsync(keyId, cancellationToken);
            if (key is not null && key.Enabled && key.IsAdmin && FixedTimeEquals(secret, key.SecretAccessKey))
            {
                return new AdminIdentity(key.AccessKeyId, key.DisplayName, ViaAccessKey: true, Fingerprint(key.SecretAccessKey));
            }
        }

        return null;
    }

    /// <summary>用令牌或管理员访问密钥换取会话 Cookie；失败返回 null。</summary>
    public async Task<AdminIdentity?> LoginAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("token", out var tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String)
            {
                var token = tokenElement.GetString() ?? string.Empty;
                if (FixedTimeEquals(token, _options.Token))
                {
                    return new AdminIdentity("token", "管理员令牌", ViaAccessKey: false);
                }

                return null;
            }

            if (root.TryGetProperty("accessKeyId", out var idElement)
                && root.TryGetProperty("secretAccessKey", out var secretElement))
            {
                var keyId = idElement.GetString() ?? string.Empty;
                var secret = secretElement.GetString() ?? string.Empty;
                var key = await accessKeys.FindAsync(keyId, cancellationToken);

                if (key is null || !key.Enabled || !key.IsAdmin)
                {
                    logger.LogWarning("管理端登录失败：{AccessKeyId} 不存在、被禁用或非管理员", keyId);
                    return null;
                }

                if (!FixedTimeEquals(secret, key.SecretAccessKey))
                {
                    logger.LogWarning("管理端登录失败：{AccessKeyId} 密钥不匹配", keyId);
                    return null;
                }

                return new AdminIdentity(key.AccessKeyId, key.DisplayName, ViaAccessKey: true, Fingerprint(key.SecretAccessKey));
            }
        }

        return null;
    }

    public void IssueCookie(HttpContext context, AdminIdentity identity)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(Math.Max(1, _options.SessionHours));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CookiePayload(
            identity.Subject,
            identity.DisplayName,
            expires.ToUnixTimeSeconds(),
            identity.ViaAccessKey,
            identity.Fingerprint ?? string.Empty));
        var body = Base64UrlEncode(payload);
        var signature = Base64UrlEncode(HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(body)));

        context.Response.Cookies.Append(
            CookieName,
            $"{CookieVersion}.{body}.{signature}",
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/",
                Expires = expires,
                IsEssential = true,
            });
    }

    public void ClearCookie(HttpContext context)
        => context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    /// <summary>校验 Cookie 的签名与有效期，并取出负载；密钥状态由调用方回查。</summary>
    private bool TryReadCookie(string value, out CookiePayload payload)
    {
        payload = default!;

        var parts = value.Split('.', 3);
        if (parts.Length != 3 || parts[0] != CookieVersion)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(parts[1]));
        byte[] actual;
        try
        {
            actual = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return false;
        }

        CookiePayload? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<CookiePayload>(Base64UrlDecode(parts[1]));
        }
        catch (JsonException)
        {
            return false;
        }

        if (decoded is null || decoded.Exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return false;
        }

        payload = decoded;
        return true;
    }

    /// <summary>Secret 的短指纹：只用于把会话绑定到签发时的凭证，不泄露密钥本身。</summary>
    private static string Fingerprint(string secretAccessKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secretAccessKey)))[..16];

    private static string? ReadHeaderToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        var direct = request.Headers["X-Admin-Token"].ToString();
        return string.IsNullOrWhiteSpace(direct) ? null : direct.Trim();
    }

    public static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private sealed record CookiePayload(string Sub, string Name, long Exp, bool Via, string Fp = "");
}
