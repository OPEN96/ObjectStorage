using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace S3ObjectStorage.Infrastructure.Persistence;

/// <summary>
/// 元数据库的 schema 版本守卫。
/// <para>
/// 本项目用 <see cref="RelationalDatabaseFacadeExtensions"/> 的 <c>EnsureCreated</c> 建库，
/// 它只在库不存在时创建，对已有库不做任何变更。因此版本升级必须显式管理：
/// </para>
/// <list type="bullet">
///   <item>记录版本号在 SQLite 的 <c>PRAGMA user_version</c>（无额外表）；</item>
///   <item>低版本 → 顺序执行幂等升级步骤后写回新版本；</item>
///   <item>高版本 → 直接拒绝启动，避免旧代码写坏新结构。</item>
/// </list>
/// </summary>
public static class SchemaGuard
{
    /// <summary>
    /// 当前代码期望的 schema 版本。新增列 / 表时 +1，并在 <see cref="Upgrades"/> 里追加对应步骤。
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// 从旧版本升级到新版本的幂等语句集合，下标为「目标版本 - 2」。
    /// 例：升级到版本 2 的语句放在索引 0，语句必须可重复执行（IF NOT EXISTS）。
    /// </summary>
    private static readonly IReadOnlyList<string[]> Upgrades =
    [
        // v1 -> v2 的占位；当前 schema 仍为 v1，尚未产生变更。
    ];

    public static async Task EnsureAsync(StorageDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        var version = await ReadVersionAsync(db, cancellationToken);

        if (version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"元数据库 schema 版本为 {version}，高于当前程序支持的 {CurrentVersion}。" +
                "这通常意味着数据目录曾被更高版本的服务写入；请改用对应版本的程序启动，或从备份恢复后再启动。");
        }

        // version == 0 有两种情况：全新的库（EnsureCreated 刚建好），或 EnsureCreated 时代遗留的旧库。
        // 两者结构都与当前版本一致（历史版本从未改过表结构），直接盖章即可。
        if (version == CurrentVersion)
        {
            return;
        }

        for (var target = version + 1; target <= CurrentVersion; target++)
        {
            var index = target - 2;
            if (index >= 0 && index < Upgrades.Count)
            {
                foreach (var statement in Upgrades[index])
                {
                    logger.LogInformation("升级元数据库 schema 到 v{Version}：{Statement}", target, statement);
                    await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
                }
            }

            // target 由常量与循环变量推导，取值只可能是 2..CurrentVersion 的整数，不存在注入面。
            var stamp = "PRAGMA user_version=" + target.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
            await db.Database.ExecuteSqlRawAsync(stamp, cancellationToken);
            logger.LogInformation("元数据库 schema 版本已更新为 v{Version}（原 v{Previous}）", target, version);
        }
    }

    private static async Task<int> ReadVersionAsync(StorageDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? 0 : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
