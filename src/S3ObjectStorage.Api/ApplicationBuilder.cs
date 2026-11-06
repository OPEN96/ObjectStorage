using S3ObjectStorage.Api.Admin;
using S3ObjectStorage.Api.Middleware;
using S3ObjectStorage.Api.Operations;
using S3ObjectStorage.Infrastructure;
using S3ObjectStorage.S3Protocol.Options;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Api;

/// <summary>
/// 主机组装入口：控制台与集成测试共用同一份装配逻辑。
/// </summary>
public static class S3Application
{
    public static WebApplication Create(string[] args, string? urls = null, IDictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration.AddEnvironmentVariables("S3STORAGE_");
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Services.Configure<S3ServerOptions>(builder.Configuration.GetSection(S3ServerOptions.SectionName));
        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));

        // 反向代理：默认关闭。开启后只有显式登记的代理 IP/CIDR 才会被采纳转发头，
        // 否则 X-Forwarded-For 可被任意伪造，登录节流与审计日志都会失真。
        var reverseProxy = builder.Configuration.GetSection(ReverseProxyOptions.SectionName)
            .Get<ReverseProxyOptions>() ?? new ReverseProxyOptions();
        if (reverseProxy.Enabled)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(
                options => ReverseProxyOptions.Apply(options, reverseProxy));
        }

        builder.Services.AddObjectStorage(builder.Configuration);
        builder.Services.AddScoped<SigV4Validator>();
        builder.Services.AddScoped<S3Operations>();

        // 管理端：认证、接口与运行指标。
        builder.Services.AddSingleton<ServerMetrics>();
        builder.Services.AddSingleton<AdminLoginThrottle>();
        builder.Services.AddScoped<AdminAuth>();
        builder.Services.AddScoped<AdminApi>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // 对象存储需要支持任意大小的对象体，并容忍慢速 / 大文件客户端。
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            // 不暴露服务器实现与版本。
            kestrel.AddServerHeader = false;
        });

        if (!string.IsNullOrWhiteSpace(urls))
        {
            builder.WebHost.UseUrls(urls);
        }

        // EF Core 的 SQL 日志太吵，默认只留 Warning 以上。
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        var app = builder.Build();

        // 未显式配置管理端令牌时生成随机令牌并落日志，避免默认口令。
        var adminOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminOptions>>().Value;
        AdminAuth.EnsureToken(
            adminOptions,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AdminAuth"),
            app.Environment.IsDevelopment());

        // 生产环境的危险/缺失配置在这里提前失败，而不是带病上线。
        StartupValidation.Validate(app);

        // 必须在最前面：后续所有中间件（节流、审计、重定向）都依赖正确的来源 IP 与 Scheme。
        if (reverseProxy.Enabled)
        {
            app.UseForwardedHeaders();
        }

        app.UseMiddleware<RequestIdMiddleware>();
        app.UseMiddleware<S3ExceptionMiddleware>();

        // 运行指标需要覆盖所有请求（含 S3 数据面）。
        app.UseMiddleware<MetricsMiddleware>();

        // 管理端必须在 S3 鉴权与分发之前短路，它不走 SigV4。
        app.UseWhen(
            static context => AdminDispatcher.IsAdminRequest(context.Request),
            static branch => branch.Run(AdminDispatcher.HandleAsync));

        // 浏览器打开根路径时引导到管理界面；S3 客户端带签名或非 HTML Accept，不受影响。
        app.UseWhen(
            static context => AdminDispatcher.WantsAdminRedirect(context.Request),
            static branch => branch.Run(static context =>
            {
                context.Response.Redirect("/admin/");
                return Task.CompletedTask;
            }));

        // MinIO 客户端与常见负载均衡器会探测 /minio/health/{live,ready,cluster}。
        // 这些端点没有 S3 签名，且不属于 S3 URL 空间，因此在鉴权之前匿名短路。
        app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/minio/health"),
            static branch => branch.Run(HealthEndpoints.HandleAsync));

        app.UseMiddleware<S3AuthenticationMiddleware>();
        app.Run(S3Dispatcher.HandleAsync);

        return app;
    }
}
