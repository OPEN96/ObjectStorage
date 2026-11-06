using System.Text;
using S3ObjectStorage.S3Protocol.Xml;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 客户端发来的 XML 有的带 xmlns、有的不带，两种形态都必须能解析。
/// </summary>
public sealed class S3XmlTests
{
    private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    [Fact]
    public void Deserialize_DeleteObjects_WithoutNamespace()
    {
        const string xml = "<Delete><Object><Key>a.txt</Key></Object><Object><Key>b.txt</Key></Object></Delete>";

        var request = S3Xml.Deserialize<DeleteObjectsRequest>(ToStream(xml));

        Assert.Equal(["a.txt", "b.txt"], request.Objects.Select(o => o.Key).ToArray());
        Assert.False(request.Quiet);
    }

    [Fact]
    public void Deserialize_DeleteObjects_WithNamespace()
    {
        var xml = $"""<Delete xmlns="{Namespace}"><Object><Key>a.txt</Key></Object></Delete>""";

        var request = S3Xml.Deserialize<DeleteObjectsRequest>(ToStream(xml));

        Assert.Equal(["a.txt"], request.Objects.Select(o => o.Key).ToArray());
    }

    [Fact]
    public void Deserialize_CompleteMultipartUpload_WithoutNamespace()
    {
        const string xml = """
            <CompleteMultipartUpload>
              <Part><PartNumber>1</PartNumber><ETag>"aaa"</ETag></Part>
              <Part><PartNumber>2</PartNumber><ETag>"bbb"</ETag></Part>
            </CompleteMultipartUpload>
            """;

        var request = S3Xml.Deserialize<CompleteMultipartUploadRequest>(ToStream(xml));

        Assert.Equal(2, request.Parts.Count);
        Assert.Equal(1, request.Parts[0].PartNumber);
        Assert.Equal("\"aaa\"", request.Parts[0].ETag);
    }

    [Fact]
    public void Deserialize_CreateBucketConfiguration_WithNamespace()
    {
        var xml = $"""<CreateBucketConfiguration xmlns="{Namespace}"><LocationConstraint>cn-north-1</LocationConstraint></CreateBucketConfiguration>""";

        var configuration = S3Xml.Deserialize<CreateBucketConfiguration>(ToStream(xml));

        Assert.Equal("cn-north-1", configuration.LocationConstraint);
    }

    [Fact]
    public void Serialize_ListBucketResult_ShouldUseS3Namespace()
    {
        var body = S3Xml.Serialize(new ListBucketResult { Name = "demo", MaxKeys = 10 }, S3Namespaces.S3);

        Assert.Contains($"xmlns=\"{Namespace}\"", body, StringComparison.Ordinal);
        Assert.Contains("<Name>demo</Name>", body, StringComparison.Ordinal);
    }

    private static MemoryStream ToStream(string xml) => new(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public async Task DeserializeAsync_DeepNesting_ShouldBeRejectedQuickly()
    {
        // 10 万层嵌套：体积只有 300 KB，却会让解析器长时间占用 CPU
        var xml = string.Concat(Enumerable.Repeat("<a>", 100_000));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<S3ObjectStorage.Domain.Exceptions.S3Exception>(
            () => S3Xml.DeserializeAsync<DeleteObjectsRequest>(ToStream(xml)));

        started.Stop();
        Assert.Equal("MalformedXML", ex.ErrorCode.Code);
        Assert.True(started.ElapsedMilliseconds < 2000, $"耗时 {started.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DeserializeAsync_ShallowNesting_ShouldStillParse()
    {
        var xml = "<Delete>" + string.Concat(Enumerable.Range(0, 500)
            .Select(i => $"<Object><Key>k{i}.bin</Key></Object>")) + "</Delete>";

        var request = await S3Xml.DeserializeAsync<DeleteObjectsRequest>(ToStream(xml));

        Assert.Equal(500, request.Objects.Count);
    }
}
