using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Amazon.S3.Model;
using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 手工构造 HTTP 请求，覆盖 MinIO 生态（mc / minio-go）特有的协议细节：
/// UNSIGNED 流式分块负载 + checksum trailer、encoding-type=url、MinIO 健康检查端点。
/// 这些分支现有 SDK 不会自动触发，必须显式验证。
/// </summary>
[Collection(S3HostCollection.Name)]
public sealed class MinioProtocolTests
{
    private readonly S3TestHost _host;

    public MinioProtocolTests(S3TestHost host) => _host = host;

    [Fact]
    public async Task UnsignedChunkedPayloadWithTrailer_ShouldStoreDecodedBody()
    {
        var bucket = await CreateBucketAsync();
        const string key = "unsigned-chunked.bin";
        try
        {
            var payload = Encoding.UTF8.GetBytes("minio-go unsigned streaming payload" + new string('x', 37));

            // minio-go 在 HTTP（非 TLS）下的请求体：
            //   <hex-size>\r\n<data>\r\n0\r\nx-amz-checksum-...:<value>\r\n\r\n
            var body = new StringBuilder();
            body.Append(payload.Length.ToString("x", CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(Encoding.UTF8.GetString(payload)).Append("\r\n")
                .Append("0\r\n")
                .Append("x-amz-checksum-crc32c:AAAAAA==\r\n")
                .Append("\r\n");

            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{_host.ServiceUrl}/{bucket}/{key}");
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToString()));
            request.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aws-chunked");
            request.Headers.TryAddWithoutValidation("x-amz-trailer", "x-amz-checksum-crc32c");
            request.Headers.TryAddWithoutValidation(
                "x-amz-decoded-content-length", payload.Length.ToString(CultureInfo.InvariantCulture));
            Sign(request, "PUT", $"/{bucket}/{key}", string.Empty, SigV4.StreamingUnsignedPayloadTrailer);

            using var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await DumpAsync(response));

            // 落盘内容必须是解码后的 payload，且 aws-chunked 不能作为对象编码回显。
            using var get = new HttpRequestMessage(HttpMethod.Get, $"{_host.ServiceUrl}/{bucket}/{key}");
            Sign(get, "GET", $"/{bucket}/{key}", string.Empty, SigV4.UnsignedPayload);
            using var downloaded = await client.SendAsync(get);
            Assert.True(downloaded.IsSuccessStatusCode, await DumpAsync(downloaded));
            Assert.Equal(payload, await downloaded.Content.ReadAsByteArrayAsync());
            Assert.DoesNotContain("aws-chunked", downloaded.Content.Headers.ContentEncoding);
        }
        finally
        {
            await CleanupAsync(bucket, key);
        }
    }

    [Fact]
    public async Task ListObjects_WithEncodingTypeUrl_ShouldPercentEncodeKeys()
    {
        var bucket = await CreateBucketAsync();
        const string key = "a+b/中文 100%.txt";
        try
        {
            // 用 AWS SDK 创建特殊字符 key，避开手写签名对非 ASCII 路径的编码歧义。
            await _host.Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = new MemoryStream([1, 2, 3]),
            });

            const string query = "list-type=2&encoding-type=url";
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_host.ServiceUrl}/{bucket}?{query}");
            Sign(request, "GET", $"/{bucket}", query, SigV4.UnsignedPayload);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
            Assert.Equal("url", xml.Root?.Element(ns + "EncodingType")?.Value);
            Assert.Equal(
                "a%2Bb/%E4%B8%AD%E6%96%87%20100%25.txt",
                xml.Root?.Element(ns + "Contents")?.Element(ns + "Key")?.Value);
        }
        finally
        {
            await CleanupAsync(bucket, key);
        }
    }

    [Theory]
    [InlineData("/minio/health/live")]
    [InlineData("/minio/health/ready")]
    [InlineData("/minio/health/cluster")]
    public async Task MinioHealthEndpoints_ShouldBeAnonymous(string path)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(_host.ServiceUrl + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListBuckets_WithoutAuthorization_ShouldReturn403AccessDenied()
    {
        // AWS S3 对完全匿名（无 Authorization、无预签名参数）的请求返回 403 AccessDenied，
        // 而不是 400 —— 部分客户端把 4xx 细分处理，返回 400 会被误判为"请求格式错误"。
        using var client = new HttpClient();
        using var response = await client.GetAsync(_host.ServiceUrl + "/");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("AccessDenied", xml.Root?.Element("Code")?.Value);
    }

    [Fact]
    public async Task HeadObject_WithRange_ShouldReturnPartialContentAndCorrectLength()
    {
        var bucket = await CreateBucketAsync();
        const string key = "head-range.bin";
        try
        {
            await _host.Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = "0123456789",
            });

            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, $"{_host.ServiceUrl}/{bucket}/{key}");
            request.Headers.TryAddWithoutValidation("Range", "bytes=2-5");
            RawSigner.Sign(request, _host.ServiceUrl, "HEAD", $"/{bucket}/{key}", string.Empty, SigV4.UnsignedPayload);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(4, response.Content.Headers.ContentLength);
            Assert.Equal("bytes 2-5/10", response.Content.Headers.ContentRange?.ToString());
        }
        finally
        {
            await CleanupAsync(bucket, key);
        }
    }

    [Fact]
    public async Task DeleteObjects_WithOversizedBody_ShouldBeRejected()
    {
        var bucket = await CreateBucketAsync();
        try
        {
            var body = "<Delete>" + new string('x', 1024 * 1024 + 1024) + "</Delete>";

            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host.ServiceUrl}/{bucket}?delete");
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
            RawSigner.Sign(request, _host.ServiceUrl, "POST", $"/{bucket}", "delete", SigV4.UnsignedPayload);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("EntityTooLarge", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await _host.Client.DeleteBucketAsync(bucket);
        }
    }

    /// <summary>为手写请求生成 SigV4 Authorization 头（只签 host / x-amz-content-sha256 / x-amz-date）。</summary>
    private void Sign(HttpRequestMessage request, string method, string path, string rawQuery, string payloadHash)
        => RawSigner.Sign(request, _host.ServiceUrl, method, path, rawQuery, payloadHash);

    private async Task<string> CreateBucketAsync()
    {
        var bucket = "minio-proto-" + Guid.NewGuid().ToString("N")[..10];
        await _host.Client.PutBucketAsync(bucket);
        return bucket;
    }

    private async Task CleanupAsync(string bucket, string key)
    {
        await _host.Client.DeleteObjectAsync(bucket, key);
        await _host.Client.DeleteBucketAsync(bucket);
    }

    private static async Task<string> DumpAsync(HttpResponseMessage response) =>
        $"status={(int)response.StatusCode} body={await response.Content.ReadAsStringAsync()}";
}
