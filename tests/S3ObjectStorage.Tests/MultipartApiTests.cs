using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Tests;

[Collection(S3HostCollection.Name)]
public sealed class MultipartApiTests
{
    private readonly S3TestHost _host;

    public MultipartApiTests(S3TestHost host) => _host = host;

    [Fact]
    public async Task MultipartUpload_ShouldMergePartsInOrder()
    {
        var bucket = await NewBucketAsync();
        var key = "large/merged.bin";

        var first = new byte[5 * 1024 * 1024];
        var second = new byte[1024 * 512];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);

        var expected = Convert.ToHexStringLower(MD5.HashData([.. first, .. second]));

        var initiate = await _host.Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = "application/octet-stream",
        });

        var part1 = await UploadPartAsync(bucket, key, initiate.UploadId, 1, first);
        var part2 = await UploadPartAsync(bucket, key, initiate.UploadId, 2, second);

        var parts = await _host.Client.ListPartsAsync(new ListPartsRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = initiate.UploadId,
        });
        Assert.Equal(2, parts.Parts.Count);
        Assert.Equal(part1, parts.Parts[0].ETag);

        var complete = await _host.Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = initiate.UploadId,
            PartETags =
            [
                new PartETag(1, part1),
                new PartETag(2, part2),
            ],
        });

        Assert.Contains("-2", complete.ETag);

        using var get = await _host.Client.GetObjectAsync(bucket, key);
        Assert.Equal(first.Length + second.Length, get.ContentLength);

        await using var buffer = new MemoryStream();
        await get.ResponseStream.CopyToAsync(buffer);
        Assert.Equal(expected, Convert.ToHexStringLower(MD5.HashData(buffer.ToArray())));
        Assert.Equal(first[..16], buffer.ToArray()[..16]);
        Assert.Equal(second[..16], buffer.ToArray()[(first.Length)..(first.Length + 16)]);
    }

    [Fact]
    public async Task CompleteWithWrongPartEtag_ShouldFail()
    {
        var bucket = await NewBucketAsync();
        var key = "large/wrong-etag.bin";

        var initiate = await _host.Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
        });

        var etag = await UploadPartAsync(bucket, key, initiate.UploadId, 1, Encoding.UTF8.GetBytes("hello multipart"));

        var exception = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => _host.Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = initiate.UploadId,
                PartETags = [new PartETag(1, "\"00000000000000000000000000000000\"")],
            }));

        _ = etag;
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("InvalidPart", exception.ErrorCode);
    }

    [Fact]
    public async Task AbortUpload_ShouldDropUploadedParts()
    {
        var bucket = await NewBucketAsync();
        var key = "large/aborted.bin";

        var initiate = await _host.Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
        });

        await UploadPartAsync(bucket, key, initiate.UploadId, 1, Encoding.UTF8.GetBytes("temporary"));

        var uploads = await _host.Client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        Assert.Contains(uploads.MultipartUploads ?? [], u => u.UploadId == initiate.UploadId);

        await _host.Client.AbortMultipartUploadAsync(bucket, key, initiate.UploadId);

        var after = await _host.Client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
        Assert.DoesNotContain(after.MultipartUploads ?? [], u => u.UploadId == initiate.UploadId);

        await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => _host.Client.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = initiate.UploadId }));
    }

    [Fact]
    public async Task StreamingPut_WithChecksum_ShouldSucceed()
    {
        // SDK 对不可定位流会启用 aws-chunked / trailing checksum，
        // 这条用例专门覆盖 STREAMING-AWS4-HMAC-SHA256-PAYLOAD 的分块签名链。
        var bucket = await NewBucketAsync();
        var key = "streaming/chunked.bin";
        var payload = new byte[1024 * 700];
        Random.Shared.NextBytes(payload);

        await using var stream = new MemoryStream(payload);
        await _host.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/octet-stream",
            DisableDefaultChecksumValidation = true,
        });

        using var get = await _host.Client.GetObjectAsync(bucket, key);
        Assert.Equal(payload.Length, get.ContentLength);

        await using var actual = new MemoryStream();
        await get.ResponseStream.CopyToAsync(actual);
        Assert.Equal(payload, actual.ToArray());
    }

    [Fact]
    public async Task UploadPart_WithUploadIdFromAnotherBucket_ShouldReturnNoSuchUpload()
    {
        // 两个桶都属于同一个管理员账号，因此 RequireBucketAsync 会放行；
        // 这里验证的是「uploadId 必须属于当前桶」这一层隔离。
        var bucketA = await NewBucketAsync();
        var bucketB = await NewBucketAsync();

        var initiate = await _host.Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucketA,
            Key = "a.bin",
        });

        var exception = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            UploadPartAsync(bucketB, "b.bin", initiate.UploadId, 1, Encoding.UTF8.GetBytes("hijack")));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("NoSuchUpload", exception.ErrorCode);

        var abort = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() =>
            _host.Client.AbortMultipartUploadAsync(bucketB, "b.bin", initiate.UploadId));
        Assert.Equal("NoSuchUpload", abort.ErrorCode);

        await _host.Client.AbortMultipartUploadAsync(bucketA, "a.bin", initiate.UploadId);
    }

    [Fact]
    public async Task UploadPart_WithWrongContentMd5_ShouldKeepOtherParts()
    {
        var bucket = await NewBucketAsync();
        const string key = "large/md5.bin";

        var initiate = await _host.Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
        });

        await UploadPartAsync(bucket, key, initiate.UploadId, 1, Encoding.UTF8.GetBytes("first part is fine"));

        // 第二条分片故意带一个错误的 Content-MD5：
        // 服务端只应回滚这一片，不能把第一片一起删掉。
        var payload = Encoding.UTF8.GetBytes("second part has bad md5");
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{_host.ServiceUrl}/{bucket}/{key}?partNumber=2&uploadId={initiate.UploadId}");
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.TryAddWithoutValidation(
            "Content-MD5",
            Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes("not-the-same-content"))));
        RawSigner.Sign(request, _host.ServiceUrl, HttpMethod.Put.Method, $"/{bucket}/{key}",
            $"partNumber=2&uploadId={initiate.UploadId}", SigV4.UnsignedPayload);

        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var parts = await _host.Client.ListPartsAsync(new ListPartsRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = initiate.UploadId,
        });

        Assert.Single(parts.Parts);
        Assert.Equal(1, parts.Parts[0].PartNumber);

        await _host.Client.AbortMultipartUploadAsync(bucket, key, initiate.UploadId);
    }

    private async Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, byte[] content)
    {
        await using var stream = new MemoryStream(content);
        var response = await _host.Client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            InputStream = stream,
            DisableDefaultChecksumValidation = true,
        });

        return response.ETag;
    }

    private async Task<string> NewBucketAsync()
    {
        var bucket = "it-mpu-" + Guid.NewGuid().ToString("N")[..8];
        await _host.Client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        return bucket;
    }
}
