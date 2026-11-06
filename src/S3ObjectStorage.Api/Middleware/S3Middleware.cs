using Microsoft.Extensions.Options;
using S3ObjectStorage.Api.Admin;
using S3ObjectStorage.Api.Http;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Options;
using S3ObjectStorage.S3Protocol.Results;
using S3ObjectStorage.S3Protocol.Routing;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Api.Middleware;

/// <summary>给每个请求分配 S3 风格的 request id。</summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid().ToString("N")[..16];
        context.Items[S3Http.RequestIdItemKey] = requestId;
        context.Response.Headers["x-amz-request-id"] = requestId;
        context.Response.Headers["x-amz-id-2"] = Guid.NewGuid().ToString("N")[..32];

        await next(context);
    }
}

/// <summary>把异常统一转成 S3 的 XML 错误文档。</summary>
public sealed class S3ExceptionMiddleware(RequestDelegate next, ILogger<S3ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开连接，无需处理。
        }
        catch (S3Exception exception)
        {
            logger.LogDebug(exception, "S3 业务异常：{Code}", exception.ErrorCode.Code);
            await WriteAsync(context, exception);
        }
        catch (System.Xml.XmlException)
        {
            await WriteAsync(context, new S3Exception(S3ErrorCode.MalformedXML, requestId: context.GetRequestId()));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "处理请求时发生未预期的错误");
            await WriteAsync(context, new S3Exception(S3ErrorCode.InternalError, requestId: context.GetRequestId()));
        }
    }

    private static async Task WriteAsync(HttpContext context, S3Exception exception)
    {
        if (context.Response.HasStarted)
        {
            // 响应已开始写入，无法再改写状态码，只能中断连接。
            context.Abort();
            return;
        }

        context.Response.Clear();

        // 管理端接口返回 JSON，不能给前端甩一份 S3 XML 错误文档。
        if (AdminDispatcher.IsAdminRequest(context.Request))
        {
            context.Response.StatusCode = exception.ErrorCode.StatusCode >= 500
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            return;
        }

        var result = S3Results.Error(exception, context.GetRequestId(), resource: context.Request.Path.Value);
        await result.ExecuteAsync(context);
    }
}

/// <summary>SigV4 鉴权中间件：把解析出的身份放进 HttpContext。</summary>
public sealed class S3AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SigV4Validator validator, IOptions<S3ServerOptions> options)
    {
        var serverOptions = options.Value;

        // OPTIONS 预检请求与显式关闭鉴权的场景直接放行。
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.SetPrincipal(S3Principal.Anonymous);
            await next(context);
            return;
        }

        var hasCredentials = context.Request.Headers.ContainsKey("Authorization")
            || context.Request.Query.ContainsKey("X-Amz-Signature");

        if (!hasCredentials && !serverOptions.RequireAuthentication)
        {
            context.SetPrincipal(S3Principal.Anonymous);
            await next(context);
            return;
        }

        var principal = await validator.AuthenticateAsync(context, context.RequestAborted);
        context.SetPrincipal(principal);

        await next(context);
    }
}
