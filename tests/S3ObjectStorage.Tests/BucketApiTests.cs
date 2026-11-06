using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace S3ObjectStorage.Tests;

[Collection(S3HostCollection.Name)]
public sealed class BucketApiTests
{
    private readonly S3TestHost _host;

    public BucketApiTests(S3TestHost host) => _host = host;

    [Fact]
    public async Task CreateBucket_Then_ListBuckets_ShouldBeVisible()
    {
        var bucket = NewBucketName();

        await _host.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        var listed = await _host.Client.ListBucketsAsync();
        Assert.Contains(listed.Buckets, b => b.BucketName == bucket);
        Assert.True(await BucketExistsAsync(bucket));
    }

    [Fact]
    public async Task PutBucket_Twice_ShouldReportConflict()
    {
        var bucket = NewBucketName();
        await _host.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        var exception = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => _host.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task DeleteNonEmptyBucket_ShouldFailWith409()
    {
        var bucket = NewBucketName();
        await _host.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "keep.txt",
            ContentBody = "keep",
        });

        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => _host.Client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);

        await _host.Client.DeleteObjectAsync(bucket, "keep.txt");
        await _host.Client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        Assert.False(await BucketExistsAsync(bucket));
    }

    [Fact]
    public async Task GetMissingBucketLocation_ShouldReturn404()
    {
        var bucket = NewBucketName();
        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => _host.Client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket }));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task RequestWithTamperedSignature_ShouldBeRejected()
    {
        using var tampered = _host.CreateTamperedClient();
        var exception = await Assert.ThrowsAsync<AmazonS3Exception>(() => tampered.ListBucketsAsync());

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("SignatureDoesNotMatch", exception.ErrorCode);
    }

    private async Task<bool> BucketExistsAsync(string bucket)
    {
        try
        {
            await _host.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 });
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string NewBucketName() => "it-bucket-" + Guid.NewGuid().ToString("N")[..8];
}
