using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3ObjectStorage.Tests;

[Collection(S3HostCollection.Name)]
public sealed class ObjectApiTests
{
    private readonly S3TestHost _host;

    public ObjectApiTests(S3TestHost host) => _host = host;

    [Fact]
    public async Task PutAndGetObject_ShouldRoundTripExactly()
    {
        var bucket = await NewBucketAsync();
        var content = new string('x', 1024 * 64);
        using var sha = SHA256.Create();
        var expected = Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(content)));

        var put = await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "dir/object.txt",
            ContentBody = content,
            ContentType = "text/plain",
        });

        Assert.False(string.IsNullOrEmpty(put.ETag));

        using var get = await _host.Client.GetObjectAsync(bucket, "dir/object.txt");
        using var reader = new StreamReader(get.ResponseStream);
        var actual = await reader.ReadToEndAsync();

        Assert.Equal(content, actual);
        Assert.Equal("text/plain", get.Headers.ContentType);
        Assert.Equal(1024 * 64, get.ContentLength);
        Assert.Equal(expected, Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(actual))));
    }

    [Fact]
    public async Task PutObjectWithUserMetadata_ShouldBeReturnedOnGet()
    {
        var bucket = await NewBucketAsync();

        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "meta.txt",
            ContentBody = "metadata",
            Metadata =
            {
                ["author"] = "workbuddy",
                ["version"] = "1",
            },
        });

        using var get = await _host.Client.GetObjectAsync(bucket, "meta.txt");
        Assert.Equal("workbuddy", get.Metadata["x-amz-meta-author"]);
        Assert.Equal("1", get.Metadata["x-amz-meta-version"]);
    }

    [Fact]
    public async Task HeadObject_ShouldReturnSizeWithoutBody()
    {
        var bucket = await NewBucketAsync();
        var payload = new byte[2048];
        Random.Shared.NextBytes(payload);

        await PutAsync(bucket, "bin/blob", payload, "application/octet-stream");

        var meta = await _host.Client.GetObjectMetadataAsync(bucket, "bin/blob");
        Assert.Equal(2048, meta.ContentLength);
        Assert.NotEmpty(meta.ETag);
        Assert.True(meta.LastModified > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task GetObjectMissing_ShouldReturnNoSuchKey()
    {
        var bucket = await NewBucketAsync();
        var exception = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => _host.Client.GetObjectAsync(bucket, "not-exists.txt"));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("NoSuchKey", exception.ErrorCode);
    }

    [Fact]
    public async Task RangeGet_ShouldReturnRequestedBytesOnly()
    {
        var bucket = await NewBucketAsync();
        var payload = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        await PutAsync(bucket, "range.bin", payload, "application/octet-stream");

        using var ranged = await _host.Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = "range.bin",
            ByteRange = new ByteRange(4, 9),
        });

        Assert.Equal(6, ranged.ContentLength);
        using var reader = new StreamReader(ranged.ResponseStream);
        Assert.Equal("456789", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ListObjectsV2_ShouldHonorPrefixDelimiterAndPaging()
    {
        var bucket = await NewBucketAsync();
        foreach (var key in new[] { "a/1.txt", "a/2.txt", "b/1.txt", "root.txt" })
        {
            await _host.Client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" });
        }

        var page = await _host.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            MaxKeys = 1,
        });
        Assert.True(page.IsTruncated);
        Assert.Single(page.S3Objects);
        Assert.NotNull(page.NextContinuationToken);

        var second = await _host.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            ContinuationToken = page.NextContinuationToken,
        });
        Assert.NotEmpty(second.S3Objects);

        var grouped = await _host.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Delimiter = "/",
        });
        Assert.Equal(["a/", "b/"], grouped.CommonPrefixes.OrderBy(x => x).ToArray());
        Assert.Single(grouped.S3Objects);
        Assert.Equal("root.txt", grouped.S3Objects[0].Key);
    }

    [Fact]
    public async Task CopyObject_ShouldDuplicateContent()
    {
        var bucket = await NewBucketAsync();
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "source.txt",
            ContentBody = "copy me",
        });

        await _host.Client.CopyObjectAsync(bucket, "source.txt", bucket, "target.txt");

        using var copied = await _host.Client.GetObjectAsync(bucket, "target.txt");
        using var reader = new StreamReader(copied.ResponseStream);
        Assert.Equal("copy me", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task CopyObject_ShouldPreserveContentTypeAndMetadata()
    {
        var bucket = await NewBucketAsync();
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "docs/source.md",
            ContentBody = "# copied",
            ContentType = "text/markdown",
            Metadata = { ["origin"] = "unit-test" },
        });

        await _host.Client.CopyObjectAsync(bucket, "docs/source.md", bucket, "docs/copy.md");

        var metadata = await _host.Client.GetObjectMetadataAsync(bucket, "docs/copy.md");
        Assert.Equal("text/markdown", metadata.Headers.ContentType);
        Assert.Equal("unit-test", metadata.Metadata["x-amz-meta-origin"]);
    }

    [Fact]
    public async Task DeleteObjects_ShouldRemoveOnlyRequestedKeys()
    {
        var bucket = await NewBucketAsync();
        await _host.Client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "one.txt", ContentBody = "1" });
        await _host.Client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "two.txt", ContentBody = "2" });
        await _host.Client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "three.txt", ContentBody = "3" });

        var result = await _host.Client.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [new KeyVersion { Key = "one.txt" }, new KeyVersion { Key = "three.txt" }],
        });

        Assert.Equal(2, result.DeletedObjects.Count);

        var remaining = await _host.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        Assert.Equal(["two.txt"], remaining.S3Objects.Select(o => o.Key).ToArray());
    }

    [Fact]
    public async Task PresignedUrl_ShouldAllowAnonymousDownload()
    {
        var bucket = await NewBucketAsync();
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "presigned.txt",
            ContentBody = "via presigned url",
        });

        var url = ForceHttp(_host.Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = "presigned.txt",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Verb = HttpVerb.GET,
        }));

        using var http = new HttpClient();
        var body = await http.GetStringAsync(url);
        Assert.Equal("via presigned url", body);
    }

    [Fact]
    public async Task PresignedUrlWithWrongSignature_ShouldBeRejected()
    {
        var bucket = await NewBucketAsync();
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "presigned.txt",
            ContentBody = "secret-is-here",
        });

        var url = ForceHttp(_host.Client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = "presigned.txt",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Verb = HttpVerb.GET,
        }));

        var broken = url[..(url.IndexOf("X-Amz-Signature=", StringComparison.OrdinalIgnoreCase) + 16)]
            + "deadbeef" + "000000000000000000000000000000000000000000000000000000000000000";

        using var http = new HttpClient();
        var response = await http.GetAsync(broken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>SDK 出于安全策略可能把预签名 URL 升级成 https，测试环境只有 http。</summary>
    private string ForceHttp(string url)
    {
        var builder = new UriBuilder(url) { Scheme = "http", Port = new Uri(_host.ServiceUrl).Port };
        return builder.Uri.ToString();
    }

    private async Task<string> NewBucketAsync()
    {
        var bucket = "it-obj-" + Guid.NewGuid().ToString("N")[..8];
        await _host.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        return bucket;
    }

    private async Task PutAsync(string bucket, string key, byte[] payload, string contentType)
    {
        await using var stream = new MemoryStream(payload);
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            DisableDefaultChecksumValidation = true,
        });
    }
}
