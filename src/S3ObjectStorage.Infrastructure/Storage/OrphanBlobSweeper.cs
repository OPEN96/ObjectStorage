using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using S3ObjectStorage.Domain.Abstractions;
using S3ObjectStorage.Infrastructure.Options;
using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Infrastructure.Storage;

/// <summary>
/// 孤儿对象体回收。
/// <para>
/// 正常情况下覆盖写会删除旧 blob，但有两种情况会留下没有任何元数据引用的文件：
/// 写入 blob 之后、提交元数据之前进程崩溃；以及多个请求并发覆盖同一个 key 时，
/// 各自读到的「上一版本」相同，最终只有一个新 blob 被引用，其余成为孤儿。
/// </para>
/// <para>
/// 这里定期把元数据库中的 BlobPath 全部读入内存，与磁盘上的 blob 文件做差集，
/// 只删除「未被引用且已超过宽限期」的文件 —— 宽限期用于保护正在写入、
/// 尚未提交元数据的 blob。
/// </para>
/// </summary>
public sealed class OrphanBlobSweeper(
    IBlobStore blobs,
    IServiceScopeFactory scopeFactory,
    IOptions<StorageOptions> options,
    ILogger<OrphanBlobSweeper> logger)
{
    private readonly StorageOptions _options = options.Value;

    /// <summary>执行一次回收，返回 (扫描文件数, 删除文件数, 释放字节数)。</summary>
    public async Task<(long Scanned, long Deleted, long Freed)> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.OrphanSweepEnabled)
        {
            return (0, 0, 0);
        }

        // 每次扫描用独立的 DbContext，避免与请求作用域的生命周期纠缠。
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StorageDbContext>();

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var path in db.Objects.AsNoTracking()
                           .Select(o => o.BlobPath)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            referenced.Add(path);
            if (referenced.Count > _options.OrphanSweepMaxObjects)
            {
                logger.LogWarning(
                    "孤儿回收已跳过：对象数超过上限 {Limit}，请调大 Storage:OrphanSweepMaxObjects 或改用离线工具",
                    _options.OrphanSweepMaxObjects);
                return (0, 0, 0);
            }
        }

        var grace = TimeSpan.FromHours(Math.Max(0, _options.OrphanGraceHours));
        var cutoff = DateTimeOffset.UtcNow - grace;
        long scanned = 0, deleted = 0, freed = 0;

        foreach (var path in blobs.EnumerateBlobPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            if (referenced.Contains(path))
            {
                continue;
            }

            var lastWrite = blobs.GetLastWriteTimeUtc(path);
            if (lastWrite is null || lastWrite > cutoff)
            {
                continue;
            }

            try
            {
                var size = new FileInfo(Path.Combine(_options.RootPath, path)).Length;
                await blobs.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                deleted++;
                freed += size;
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "删除孤儿 blob 失败: {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogDebug(ex, "删除孤儿 blob 无权限: {Path}", path);
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("孤儿回收完成：扫描 {Scanned} 个文件，删除 {Deleted} 个，释放 {Freed} 字节",
                scanned, deleted, freed);
        }

        return (scanned, deleted, freed);
    }
}

/// <summary>按 <see cref="StorageOptions.OrphanSweepIntervalHours"/> 周期触发 <see cref="OrphanBlobSweeper"/>。</summary>
public sealed class OrphanBlobSweepWorker(
    OrphanBlobSweeper sweeper,
    IOptions<StorageOptions> options,
    ILogger<OrphanBlobSweepWorker> logger) : BackgroundService
{
    private readonly StorageOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.OrphanSweepEnabled)
        {
            logger.LogInformation("孤儿回收未启用（Storage:OrphanSweepEnabled=false）");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.OrphanSweepIntervalHours));
        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(_options.OrphanSweepInitialDelaySeconds, 0, 3600));

        try
        {
            if (initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(initialDelay, stoppingToken).ConfigureAwait(false);
            }

            using var timer = new PeriodicTimer(interval);
            do
            {
                try
                {
                    await sweeper.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "孤儿回收失败，等待下一轮");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }
}
