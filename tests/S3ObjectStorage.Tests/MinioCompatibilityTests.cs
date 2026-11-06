using System.Text;
using Minio;
using Minio.DataModel.Args;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 用 MinIO 官方 .NET SDK（Minio 6.x）打真实 Kestrel，验证 MinIO 生态客户端的兼容性。
/// MinIO SDK 与 AWS SDK 的签名习惯不同：非 TLS 下默认 UNSIGNED-PAYLOAD，
/// 列举时带 encoding-type=url，且大文件走自动分片上传。
/// </summary>
[Collection(S3HostCollection.Name)]
public sealed class MinioCompatibilityTests
{
    private readonly S3TestHost _host;
    private readonly IMinioClient _minio;

    public MinioCompatibilityTests(S3TestHost host)
    {
        _host = host;
        var uri = new Uri(host.ServiceUrl);
        _minio = new MinioClient()
            .WithEndpoint(uri.Host, uri.Port)
            .WithCredentials(S3TestHost.AccessKey, S3TestHost.SecretKey)
            .WithSSL(false)
            .WithRegion("us-east-1")
            .Build();
    }

    [Fact]
    public async Task MinioSdk_BucketAndObjectRoundTrip_ShouldWork()
    {
        var bucket = NewBucketName();
        await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));

        Assert.True(await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket)));

        const string payload = "hello from minio dotnet sdk";
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
        {
            await _minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject("greeting.txt")
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType("text/plain"));
        }

        var stat = await _minio.StatObjectAsync(new StatObjectArgs()
            .WithBucket(bucket)
            .WithObject("greeting.txt"));
        Assert.Equal(payload.Length, stat.Size);

        var downloaded = await DownloadAsync(bucket, "greeting.txt");
        Assert.Equal(payload, Encoding.UTF8.GetString(downloaded));

        await _minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject("greeting.txt"));
        await _minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
    }

    [Fact]
    public async Task MinioSdk_ListObjects_WithEncodingTypeUrl_ShouldDecodeKeys()
    {
        var bucket = NewBucketName();
        await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));

        // 含空格、中文、'+'、'%' 的 key：专门考验 encoding-type=url 的编解码。
        var keys = new[] { "docs/2026 报告 (final)+100%.txt", "docs/plain.txt", "root.bin" };
        foreach (var key in keys)
        {
            await using var stream = new MemoryStream([1, 2, 3]);
            await _minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject(key)
                .WithStreamData(stream)
                .WithObjectSize(3));
        }

        var listed = new List<string>();
        await foreach (var item in _minio.ListObjectsEnumAsync(
                           new ListObjectsArgs().WithBucket(bucket).WithRecursive(true)))
        {
            listed.Add(item.Key);
        }

        foreach (var key in keys)
        {
            Assert.Contains(key, listed);
        }

        foreach (var key in keys)
        {
            await _minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(key));
        }

        await _minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
    }

    [Fact]
    public async Task MinioSdk_LargeObject_ShouldUseMultipartUpload()
    {
        var bucket = NewBucketName();
        await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));

        // MinIO .NET SDK 在对象超过其分片阈值（默认 16 MiB）时自动启用分片上传。
        var payload = new byte[20 * 1024 * 1024 + 17];
        new Random(20261104).NextBytes(payload);

        await using (var stream = new MemoryStream(payload))
        {
            await _minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject("big.bin")
                .WithStreamData(stream)
                .WithObjectSize(stream.Length));
        }

        var downloaded = await DownloadAsync(bucket, "big.bin");
        Assert.Equal(payload, downloaded);

        await _minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject("big.bin"));
        await _minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
    }

    [Fact]
    public async Task MinioSdk_PresignedGetObject_ShouldAllowAnonymousDownload()
    {
        var bucket = NewBucketName();
        await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));

        var payload = Encoding.UTF8.GetBytes("presigned payload");
        await using (var stream = new MemoryStream(payload))
        {
            await _minio.PutObjectAsync(new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject("signed.txt")
                .WithStreamData(stream)
                .WithObjectSize(stream.Length));
        }

        var url = await _minio.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(bucket)
            .WithObject("signed.txt")
            .WithExpiry(600));

        using var http = new HttpClient();
        var body = await http.GetByteArrayAsync(url);
        Assert.Equal(payload, body);

        await _minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject("signed.txt"));
        await _minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
    }

    [Fact]
    public async Task MinioSdk_PresignedPutObject_ShouldAllowAnonymousUpload()
    {
        var bucket = NewBucketName();
        await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));

        var url = await _minio.PresignedPutObjectAsync(new PresignedPutObjectArgs()
            .WithBucket(bucket)
            .WithObject("uploaded.txt")
            .WithExpiry(600));

        var payload = Encoding.UTF8.GetBytes("uploaded through presigned put");
        using var http = new HttpClient();
        using var response = await http.PutAsync(url, new ByteArrayContent(payload));
        Assert.True(response.IsSuccessStatusCode, $"status={(int)response.StatusCode}");
        Assert.Equal(payload, await DownloadAsync(bucket, "uploaded.txt"));

        await _minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject("uploaded.txt"));
        await _minio.RemoveBucketAsync(new RemoveBucketArgs().WithBucket(bucket));
    }

    private async Task<byte[]> DownloadAsync(string bucket, string key)
    {
        using var output = new MemoryStream();
        await _minio.GetObjectAsync(new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(key)
            .WithCallbackStream(async (stream, ct) => await stream.CopyToAsync(output, ct)));
        return output.ToArray();
    }

    private static string NewBucketName() => "minio-it-" + Guid.NewGuid().ToString("N")[..10];
}
