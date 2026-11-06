using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using S3ObjectStorage.Infrastructure.Persistence;

namespace S3ObjectStorage.Tests;

/// <summary>
/// schema 版本守卫：在 <c>EnsureCreated</c> 之上补一个显式的版本门槛，
/// 防止「旧代码打开新库」或「新代码静默跑在旧结构上」。
/// </summary>
public sealed class SchemaGuardTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"s3schema-{Guid.NewGuid():N}.db");

    private StorageDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<StorageDbContext>()
            .UseSqlite($"Data Source={_path}")
            .Options;
        return new StorageDbContext(options);
    }

    [Fact]
    public async Task Ensure_ShouldStampCurrentVersion_OnFreshDatabase()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        await SchemaGuard.EnsureAsync(db, NullLogger.Instance);

        Assert.Equal(SchemaGuard.CurrentVersion, await ReadVersionAsync(db));
    }

    [Fact]
    public async Task Ensure_ShouldBeIdempotent()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        await SchemaGuard.EnsureAsync(db, NullLogger.Instance);
        await SchemaGuard.EnsureAsync(db, NullLogger.Instance);

        Assert.Equal(SchemaGuard.CurrentVersion, await ReadVersionAsync(db));
    }

    [Fact]
    public async Task Ensure_ShouldRefuseDatabaseFromNewerVersion()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync($"PRAGMA user_version={SchemaGuard.CurrentVersion + 5};");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SchemaGuard.EnsureAsync(db, NullLogger.Instance));

        Assert.Contains("高于当前程序支持", error.Message);
    }

    private static async Task<int> ReadVersionAsync(StorageDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        // SQLite 连接池会短暂持有文件句柄，删不掉就留给临时目录清理。
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                var file = _path + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
