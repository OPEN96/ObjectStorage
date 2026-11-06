using System.Text;
using Microsoft.AspNetCore.Http;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Xml;

namespace S3ObjectStorage.S3Protocol.Results;

/// <summary>
/// 组装符合 S3 约定的响应。
/// </summary>
public static class S3Results
{
    public static IResult Xml<T>(T model, int statusCode = StatusCodes.Status200OK, IDictionary<string, string>? headers = null)
        => new XmlResult<T>(model, statusCode, headers, ns: S3Namespaces.S3);

    public static IResult XmlNoNamespace<T>(T model, int statusCode = StatusCodes.Status200OK, IDictionary<string, string>? headers = null)
        => new XmlResult<T>(model, statusCode, headers, ns: null);

    public static IResult Empty(int statusCode = StatusCodes.Status204NoContent, IDictionary<string, string>? headers = null)
        => new EmptyResult(statusCode, headers);

    /// <summary>
    /// 结束响应但不改动已设置的状态码与 Content-Length。
    /// 用于 HEAD / 206 这类已经由调用方写好头部的场景。
    /// </summary>
    public static IResult Completed(int? statusCode = null, IDictionary<string, string>? headers = null)
        => new CompletedResult(statusCode, headers);

    /// <summary>把业务异常转成 S3 标准的 <c>&lt;Error&gt;</c> 文档。</summary>
    public static IResult Error(S3Exception exception, string? requestId = null, string? resource = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in exception.ExtraHeaders)
        {
            headers[header.Key] = header.Value;
        }

        var model = new ErrorResponse
        {
            Code = exception.ErrorCode.Code,
            Message = exception.Message,
            RequestId = exception.RequestId ?? requestId,
            Resource = resource,
            ArgumentName = exception.Key,
        };

        return XmlNoNamespace(model, exception.StatusCode, headers);
    }

    private sealed class XmlResult<T>(T model, int statusCode, IDictionary<string, string>? headers, string? ns) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var body = S3Xml.Serialize(model, ns);

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = S3Xml.ContentType + "; charset=utf-8";
            ApplyHeaders(httpContext, headers);

            await httpContext.Response.WriteAsync(body, new UTF8Encoding(false), httpContext.RequestAborted);
        }
    }

    private sealed class EmptyResult(int statusCode, IDictionary<string, string>? headers) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentLength = 0;
            ApplyHeaders(httpContext, headers);
            return Task.CompletedTask;
        }
    }

    private sealed class CompletedResult(int? statusCode, IDictionary<string, string>? headers) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (statusCode is { } code)
            {
                httpContext.Response.StatusCode = code;
            }

            ApplyHeaders(httpContext, headers);
            return Task.CompletedTask;
        }
    }

    private static void ApplyHeaders(HttpContext httpContext, IDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (name, value) in headers)
        {
            httpContext.Response.Headers[name] = value;
        }
    }
}
