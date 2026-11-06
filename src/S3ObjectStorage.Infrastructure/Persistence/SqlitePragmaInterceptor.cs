using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace S3ObjectStorage.Infrastructure.Persistence;

/// <summary>
/// 为连接池中每条 SQLite 连接设置 PRAGMA。
/// <para>
/// <c>PRAGMA synchronous</c> 与 <c>PRAGMA busy_timeout</c> 都是<b>连接级</b>设置，
/// 只在启动时执行一次并不能覆盖连接池后续新建的连接 —— 那些连接仍是 SQLite 默认的
/// <c>synchronous=FULL</c>，于是每次 <c>SaveChanges</c> 都要 fsync 一次 WAL，
/// 小对象 PUT 因此被磁盘刷盘支配（实测单次 PUT 触发约 5 次 flush）。
/// </para>
/// <para>
/// <c>journal_mode=WAL</c> 是数据库文件级、写入文件头后持久生效，无需每次设置。
/// </para>
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragma = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
