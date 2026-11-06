using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Infrastructure.Options;
using S3ObjectStorage.Infrastructure.Persistence;
using S3ObjectStorage.Infrastructure.Repositories;
using S3ObjectStorage.Infrastructure.Storage;

namespace S3ObjectStorage.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        var options = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        var root = Path.GetFullPath(options.RootPath);
        Directory.CreateDirectory(root);

        var connectionString = string.IsNullOrWhiteSpace(options.ConnectionString)
            ? $"Data Source={Path.Combine(root, "metadata.db")}"
            : options.ConnectionString;

        services.AddDbContext<StorageDbContext>(db => db
            .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30))
            .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddScoped<IBucketStore, BucketStore>();
        services.AddScoped<IObjectStore, ObjectStore>();
        services.AddScoped<IMultipartStore, MultipartStore>();
        services.AddScoped<IAccessKeyStore, AccessKeyStore>();
        services.AddSingleton<IBlobStore, FileSystemBlobStore>();
        services.AddSingleton<OrphanBlobSweeper>();
        services.AddHostedService<OrphanBlobSweepWorker>();
        services.AddSingleton<TempCleanupWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<TempCleanupWorker>());

        return services;
    }

    /// <summary>
    /// 建库 + 初始化初始密钥，应在 Web 主机启动之前调用。
    /// </summary>
    public static async Task InitializeStorageAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<StorageDbContext>>();

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await SchemaGuard.EnsureAsync(db, logger, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken);

        await SeedAccessKeysAsync(db, configuration, environment, logger, cancellationToken);
    }

    private static async Task SeedAccessKeysAsync(
        StorageDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await db.AccessKeys.AnyAsync(cancellationToken))
        {
            return;
        }

        var configured = configuration.GetSection("Storage:BootstrapAccessKeys").Get<BootstrapKey[]>() ?? [];

        if (configured.Length == 0)
        {
            if (!environment.IsDevelopment())
            {
                // 生产环境不能在日志里打印密钥，也不能以「无凭证」状态对外提供服务。
                throw new InvalidOperationException(
                    "元数据库为空且未配置 Storage:BootstrapAccessKeys。请通过环境变量注入初始访问密钥" +
                    "（S3STORAGE_Storage__BootstrapAccessKeys__0__AccessKeyId / ...SecretAccessKey），再启动服务。");
            }

            var generatedSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            configured =
            [
                new BootstrapKey
                {
                    AccessKeyId = "AKIDLOCAL000000000001",
                    SecretAccessKey = generatedSecret,
                    DisplayName = "local-dev",
                    IsAdmin = true,
                },
            ];

            logger.LogWarning(
                "未配置 Storage:BootstrapAccessKeys，已生成开发用密钥 AccessKeyId={AccessKeyId} SecretAccessKey={SecretAccessKey}（仅本次启动有效，生产环境禁止使用）",
                configured[0].AccessKeyId,
                generatedSecret);
        }

        foreach (var key in configured)
        {
            if (string.IsNullOrWhiteSpace(key.AccessKeyId) || string.IsNullOrWhiteSpace(key.SecretAccessKey))
            {
                continue;
            }

            db.AccessKeys.Add(new Domain.Entities.AccessKeyEntity
            {
                AccessKeyId = key.AccessKeyId,
                SecretAccessKey = key.SecretAccessKey,
                DisplayName = key.DisplayName ?? key.AccessKeyId,
                IsAdmin = key.IsAdmin,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            logger.LogInformation("已初始化密钥 {AccessKeyId} ({DisplayName})", key.AccessKeyId, key.DisplayName ?? key.AccessKeyId);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    internal sealed class BootstrapKey
    {
        public string AccessKeyId { get; set; } = string.Empty;

        public string SecretAccessKey { get; set; } = string.Empty;

        public string? DisplayName { get; set; }

        public bool IsAdmin { get; set; } = true;
    }
}

/// <summary>定期清理写入中断残留的临时文件。</summary>
internal sealed class TempCleanupWorker(IBlobStore blobStore, ILogger<TempCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await blobStore.CleanupTempAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "临时文件清理失败");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
