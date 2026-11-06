namespace S3ObjectStorage.Api.Admin;

/// <summary>
/// 进程内的轻量运行指标，供管理端看板展示。
/// 只用 Interlocked 计数，不引入额外的监控依赖。
/// </summary>
public sealed class ServerMetrics
{
    private long _requests;
    private long _clientErrors;
    private long _serverErrors;
    private long _bytesIn;
    private long _bytesOut;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void RecordRequest(HttpContext context)
    {
        Interlocked.Increment(ref _requests);

        var declaredLength = context.Request.ContentLength;
        if (declaredLength is > 0)
        {
            Interlocked.Add(ref _bytesIn, declaredLength.Value);
        }
    }

    public void RecordResponse(HttpContext context)
    {
        var status = context.Response.StatusCode;
        if (status is >= 400 and < 500)
        {
            Interlocked.Increment(ref _clientErrors);
        }
        else if (status >= 500)
        {
            Interlocked.Increment(ref _serverErrors);
        }

        var length = context.Response.ContentLength;
        if (length is > 0)
        {
            Interlocked.Add(ref _bytesOut, length.Value);
        }
    }

    public object Snapshot() => new
    {
        requests = Interlocked.Read(ref _requests),
        clientErrors = Interlocked.Read(ref _clientErrors),
        serverErrors = Interlocked.Read(ref _serverErrors),
        bytesIn = Interlocked.Read(ref _bytesIn),
        bytesOut = Interlocked.Read(ref _bytesOut),
    };
}

/// <summary>统计每个请求的次数与状态码分布。</summary>
public sealed class MetricsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ServerMetrics metrics)
    {
        metrics.RecordRequest(context);
        try
        {
            await next(context);
        }
        finally
        {
            metrics.RecordResponse(context);
        }
    }
}
