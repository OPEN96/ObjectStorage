using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Api;

/// <summary>
/// MinIO 风格的匿名健康检查端点。
/// <c>live</c> 只表示进程存活；<c>ready</c> 会实际探测元数据库，供负载均衡器摘除故障实例。
/// </summary>
public static class HealthEndpoints
{
    public static async Task HandleAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        var isReadyProbe = context.Request.Path.Value?.EndsWith("/ready", StringComparison.OrdinalIgnoreCase) == true;
        if (!isReadyProbe)
        {
            await context.Response.WriteAsync("{\"status\":\"ok\"}", context.RequestAborted);
            return;
        }

        try
        {
            await using var scope = context.RequestServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
            var ok = await db.Database.CanConnectAsync(context.RequestAborted);

            context.Response.StatusCode = ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(ok ? "{\"status\":\"ok\"}" : "{\"status\":\"unavailable\"}", context.RequestAborted);
        }
        catch (Exception)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("{\"status\":\"unavailable\"}", context.RequestAborted);
        }
    }
}
