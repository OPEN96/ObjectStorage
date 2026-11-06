using Microsoft.Extensions.Options;

namespace S3ObjectStorage.Api.Admin;

/// <summary>
/// <c>/admin</c> 分支的入口。
/// S3 是「路径 + 方法 + 子资源」驱动的单一 URL 空间，管理端需要先短路，
/// 否则会被 S3 分发器当成桶名处理。
/// 这里沿用同样的手工分发风格，同时托管 <c>wwwroot/admin</c> 下的静态资源。
/// </summary>
public static class AdminDispatcher
{
    private const string Prefix = "/admin";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".map"] = "application/json; charset=utf-8",
    };

    public static bool IsAdminRequest(HttpRequest request)
        => request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>浏览器直接访问根路径时引导到管理端（S3 客户端不会带 text/html）。</summary>
    public static bool WantsAdminRedirect(HttpRequest request)
        => request.Path == "/"
           && HttpMethods.IsGet(request.Method)
           && !request.Headers.ContainsKey("Authorization")
           && !request.Query.ContainsKey("X-Amz-Signature")
           && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    public static async Task HandleAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (!options.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ApplySecurityHeaders(context.Response);

        var path = context.Request.Path.Value ?? Prefix;
        var relative = path.Length > Prefix.Length ? path[Prefix.Length..] : "/";
        if (!relative.StartsWith('/'))
        {
            relative = "/" + relative;
        }

        if (relative is "/api" or "/api/" || relative.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!IsAllowedCrossOrigin(context.Request))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status403Forbidden, "跨站请求被拒绝。");
                return;
            }

            if (!IsWithinBodyLimit(context.Request))
            {
                await WriteJsonErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "请求体过大。");
                return;
            }

            var api = context.RequestServices.GetRequiredService<AdminApi>();
            await api.HandleAsync(context, relative, context.RequestAborted);
            return;
        }

        await ServeStaticAsync(context, relative);
    }

    /// <summary>管理端的通用安全响应头：禁止被嵌入、禁止 MIME 嗅探、限制脚本来源。</summary>
    private static void ApplySecurityHeaders(HttpResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            "script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    }

    /// <summary>
    /// 会话 Cookie 走 SameSite=Lax，跨站 POST 本来就不会带上；
    /// 这里再校验 Origin / Sec-Fetch-Site 作为纵深防御。
    /// </summary>
    private static bool IsAllowedCrossOrigin(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return true;
        }

        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.Count > 0)
        {
            return !string.Equals(site.ToString(), "cross-site", StringComparison.OrdinalIgnoreCase);
        }

        // 非浏览器客户端（curl / 自动化）不会带这两个头。
        return true;
    }

    private static bool IsWithinBodyLimit(HttpRequest request)
    {
        // 对象上传是流式大文件，交给 Kestrel 与磁盘配额管理；其余 JSON 接口限制在 1 MiB。
        var isObjectUpload = HttpMethods.IsPut(request.Method)
                             && request.Path.Value?.Contains("/objects/", StringComparison.Ordinal) == true;

        return isObjectUpload || request.ContentLength is null or <= 1024 * 1024;
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
    }

    private static async Task ServeStaticAsync(HttpContext context, string relative)
    {
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AdminStatic");

        var root = ResolveAssetRoot(environment);
        if (root is null)
        {
            logger.LogWarning("找不到管理端静态资源目录（wwwroot/admin）");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var requested = relative is "/" or "" ? "/index.html" : relative;
        var candidate = Path.GetFullPath(Path.Combine(root, requested.TrimStart('/')));

        // 目录穿越防护。
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 单页应用：未知路径回退到 index.html。
        if (!File.Exists(candidate))
        {
            if (Path.HasExtension(requested))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            candidate = Path.Combine(root, "index.html");
            if (!File.Exists(candidate))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        context.Response.ContentType = ContentTypes.TryGetValue(Path.GetExtension(candidate), out var contentType)
            ? contentType
            : "application/octet-stream";

        // 管理界面更新后需要立刻生效，静态资源不做长缓存。
        context.Response.Headers.CacheControl = "no-cache, max-age=0";

        await context.Response.SendFileAsync(candidate, context.RequestAborted);
    }

    private static string? ResolveAssetRoot(IWebHostEnvironment environment)
    {
        var candidates = new[]
        {
            environment.WebRootPath is null ? null : Path.Combine(environment.WebRootPath, "admin"),
            Path.Combine(environment.ContentRootPath, "wwwroot", "admin"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin"),
        };

        foreach (var candidate in candidates)
        {
            if (candidate is not null && Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
