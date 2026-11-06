using System.Text;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using S3ObjectStorage.Infrastructure.Storage;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 孤儿 blob 回收：并发覆盖同一 key 或进程崩溃会留下未被元数据引用的对象体，
/// 回收任务必须在「不影响在用对象」的前提下把它们删掉。
/// </summary>
[Collection(S3HostCollection.Name)]
public sealed class OrphanSweepTests
{
    private readonly S3TestHost _host;

    public OrphanSweepTests(S3TestHost host) => _host = host;

    [Fact]
    public async Task Sweep_ShouldDeleteAgedOrphan_AndKeepLiveObjects()
    {
        var bucket = "sweep-" + Guid.NewGuid().ToString("N")[..12];
        await _host.Client.PutBucketAsync(bucket);

        var payload = Encoding.UTF8.GetBytes(new string('l', 4096));
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "live.bin",
            InputStream = new MemoryStream(payload),
        });

        var orphan = CreateOrphanBlob("aged", DateTime.UtcNow.AddHours(-3));
        var fresh = CreateOrphanBlob("fresh", DateTime.UtcNow);

        var sweeper = _host.App.Services.GetRequiredService<OrphanBlobSweeper>();
        var (scanned, deleted, freed) = await sweeper.SweepAsync();

        Assert.True(scanned > 0);
        Assert.False(File.Exists(orphan), "超过宽限期的孤儿 blob 应被删除");
        Assert.True(File.Exists(fresh), "宽限期内的 blob 可能正在上传，不能删除");
        Assert.True(deleted >= 1);
        Assert.True(freed >= 512);

        // 在用对象不受影响
        using var response = await _host.Client.GetObjectAsync(bucket, "live.bin");
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task Sweep_ShouldBeNoOp_WhenDisabled()
    {
        var orphan = CreateOrphanBlob("disabled", DateTime.UtcNow.AddHours(-3));
        var options = _host.App.Services.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<S3ObjectStorage.Infrastructure.Options.StorageOptions>>();
        var original = options.Value.OrphanSweepEnabled;
        options.Value.OrphanSweepEnabled = false;
        try
        {
            var sweeper = _host.App.Services.GetRequiredService<OrphanBlobSweeper>();
            var (scanned, _, _) = await sweeper.SweepAsync();
            Assert.Equal(0, scanned);
            Assert.True(File.Exists(orphan));
        }
        finally
        {
            options.Value.OrphanSweepEnabled = original;
            if (File.Exists(orphan))
            {
                File.Delete(orphan);
            }
        }
    }

    private string CreateOrphanBlob(string name, DateTime lastWriteUtc)
    {
        var dir = Path.Combine(_host.RootPath, "blobs", "zz", "zz");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
