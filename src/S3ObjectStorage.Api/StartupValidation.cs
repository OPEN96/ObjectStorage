using Microsoft.Extensions.Options;
using S3ObjectStorage.Api.Admin;
using S3ObjectStorage.Infrastructure.Options;
using S3ObjectStorage.S3Protocol.Options;

namespace S3ObjectStorage.Api;

/// <summary>
/// 启动期配置校验：把「能启动但线上必出事」的配置挡在启动阶段。
/// 开发环境只告警，生产环境直接失败并给出可执行的修复提示。
/// </summary>
public static class StartupValidation
{
    private static readonly HashSet<string> KnownWeakSecrets = new(StringComparer.Ordinal)
    {
        "local-development-secret-please-change-me",
        "change-me-to-a-long-random-secret",
        "change-me-to-a-long-random-string",
        "secret",
        "password",
        "minioadmin",
    };

    public static void Validate(WebApplication app)
    {
        var environment = app.Environment;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        var storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        var s3 = app.Services.GetRequiredService<IOptions<S3ServerOptions>>().Value;
        var admin = app.Services.GetRequiredService<IOptions<AdminOptions>>().Value;

        if (storage.BufferSize is < 4096 or > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("Storage:BufferSize 必须在 4 KiB 与 4 MiB 之间。");
        }

        if (storage.MaxSinglePutBytes < 0)
        {
            throw new InvalidOperationException("Storage:MaxSinglePutBytes 不能为负数。");
        }

        if (s3.MaxKeyLength is < 1 or > 4096)
        {
            throw new InvalidOperationException("S3:MaxKeyLength 必须在 1 与 4096 之间（S3 规范上限为 1024）。");
        }

        if (string.IsNullOrWhiteSpace(s3.Region))
        {
            throw new InvalidOperationException("S3:Region 不能为空。");
        }

        if (!s3.RequireAuthentication)
        {
            logger.LogWarning("S3:RequireAuthentication=false：所有匿名请求都会被接受，仅可用于本地调试。");
        }

        if (!storage.VerifyPayloadHash)
        {
            logger.LogWarning("Storage:VerifyPayloadHash=false：将不再校验上传内容的 SHA256，存在数据被篡改的风险。");
        }

        if (!admin.Enabled)
        {
            logger.LogWarning("Admin:Enabled=false：网页管理端已关闭，只能通过 S3 API 管理。");
        }

        if (environment.IsDevelopment())
        {
            return;
        }

        if (admin.Enabled && (admin.Token.Length < 24 || KnownWeakSecrets.Contains(admin.Token)))
        {
            throw new InvalidOperationException(
                "管理端令牌过短或仍是示例值。请设置至少 24 位的 S3STORAGE_Admin__Token（推荐 openssl rand -base64 48）。");
        }

        var bootstrapKeys = app.Configuration.GetSection("Storage:BootstrapAccessKeys").GetChildren().ToList();
        foreach (var key in bootstrapKeys)
        {
            var keyId = key["AccessKeyId"] ?? "(未命名)";
            var secret = key["SecretAccessKey"] ?? string.Empty;

            if (secret.Length < 16 || KnownWeakSecrets.Contains(secret))
            {
                throw new InvalidOperationException(
                    $"Storage:BootstrapAccessKeys 中的 {keyId} 使用了空/过短/示例密钥。生产环境请换成至少 16 位的随机密钥，" +
                    "或改用管理端创建访问密钥。");
            }
        }

        if (bootstrapKeys.Count == 0)
        {
            logger.LogInformation(
                "未配置 Storage:BootstrapAccessKeys：若元数据库为空，首次启动将拒绝启动；已初始化过的实例不受影响。");
        }
    }
}
