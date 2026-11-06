using S3ObjectStorage.S3Protocol.Options;
using S3ObjectStorage.S3Protocol.Routing;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Api.Http;

public static class S3Http
{
    public const string RouteItemKey = "S3.Route";
    public const string PrincipalItemKey = "S3.Principal";
    public const string RequestIdItemKey = "S3.RequestId";

    public static S3Route GetRoute(this HttpContext context)
        => context.Items.TryGetValue(RouteItemKey, out var value) && value is S3Route route
            ? route
            : throw new InvalidOperationException("请求尚未经过 S3 路由中间件。");

    public static void SetRoute(this HttpContext context, S3Route route) => context.Items[RouteItemKey] = route;

    public static S3Principal GetPrincipal(this HttpContext context)
        => context.Items.TryGetValue(PrincipalItemKey, out var value) && value is S3Principal principal
            ? principal
            : S3Principal.Anonymous;

    public static void SetPrincipal(this HttpContext context, S3Principal principal) => context.Items[PrincipalItemKey] = principal;

    public static S3ServerOptions GetS3Options(this HttpContext context)
        => context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3ServerOptions>>().Value;

    public static string GetRequestId(this HttpContext context)
        => context.Items.TryGetValue(RequestIdItemKey, out var value) && value is string id ? id : string.Empty;

    /// <summary>客户端声明的 x-amz-content-sha256。</summary>
    public static string? GetDeclaredPayloadHash(this HttpContext context)
        => context.Items.TryGetValue(S3RequestItems.DeclaredPayloadHash, out var value) ? value as string : null;

    /// <summary>本次签名的上下文，用于校验 aws-chunked 负载。</summary>
    public static SigV4Seed? GetSigV4Seed(this HttpContext context)
        => context.Items.TryGetValue(S3RequestItems.SigV4Seed, out var value) ? value as SigV4Seed : null;
}
