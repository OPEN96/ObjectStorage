using S3ObjectStorage.S3Protocol.SigV4;

namespace S3ObjectStorage.Tests;

public sealed class SigV4UnitTests
{
    [Theory]
    [InlineData("a b", "a%20b")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("~-_.", "~-_.")]
    [InlineData("中文", "%E4%B8%AD%E6%96%87")]
    public void PercentEncode_ShouldFollowRfc3986(string input, string expected)
        => Assert.Equal(expected, SigV4.PercentEncode(input));

    [Fact]
    public void PercentEncodeDecode_ShouldRoundTrip()
    {
        const string sample = "photos/2026/假期 (1).jpg";
        Assert.Equal(sample, SigV4.PercentDecode(SigV4.PercentEncode(sample)));
    }

    [Fact]
    public void CanonicalUri_ShouldKeepSlashSeparators()
        => Assert.Equal("/bucket/dir/a%20b.jpg", SigV4.CanonicalUri("/bucket/dir/a b.jpg"));

    [Fact]
    public void CanonicalQueryString_ShouldSortAndExcludeSignature()
    {
        var canonical = SigV4.CanonicalQueryString("?b=2&a=1&X-Amz-Signature=deadbeef&a=0");
        Assert.Equal("X-Amz-Signature=deadbeef&a=0&a=1&b=2", canonical);

        var withoutSignature = SigV4.CanonicalQueryString("?b=2&a=1&X-Amz-Signature=deadbeef", "X-Amz-Signature");
        Assert.Equal("a=1&b=2", withoutSignature);
    }

    /// <summary>
    /// 期望值由独立的 Python 实现（hmac/hashlib）对同一 canonical request 计算得到，
    /// 用于交叉验证「派生密钥 → string-to-sign → 签名」这条链路不被无意改动。
    /// </summary>
    [Fact]
    public void SignaturePipeline_ShouldProduceStableVector()
    {
        const string secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        const string dateStamp = "20130524";
        const string region = "us-east-1";

        var signingKey = SigV4.DeriveSigningKey(secret, dateStamp, region, "s3");
        var canonicalRequest = string.Join("\n",
            "GET",
            "/test.txt",
            "",
            "host:examplebucket.s3.amazonaws.com",
            "range:bytes=0-9",
            "x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "x-amz-date:20130524T000000Z",
            "",
            "host;range;x-amz-content-sha256;x-amz-date",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        var stringToSign = SigV4.BuildStringToSign(
            "20130524T000000Z",
            $"{dateStamp}/{region}/s3/aws4_request",
            SigV4.Sha256Hex(canonicalRequest));

        var signature = SigV4.ComputeSignature(signingKey, stringToSign);

        Assert.Equal("67fe34c8530db585abddc51067328adfedb6e42487d2566dc7d927d6e2722900", signature);
    }

    [Fact]
    public void NormalizeHeaderValue_ShouldCollapseInternalWhitespace()
        => Assert.Equal("a b c", SigV4.NormalizeHeaderValue("  a\t\tb   c  "));

    [Fact]
    public void FixedTimeEquals_ShouldCompareHexSignaturesCaseInsensitivelyCorrectly()
    {
        var upper = "F0E8BDB87C964420E857BD35B5D6ED310BD44F0170ABA48DD91039C6036BDB41";
        var lower = upper.ToLowerInvariant();

        Assert.True(SigV4.FixedTimeEquals(upper, upper));
        Assert.False(SigV4.FixedTimeEquals(upper, lower));
    }
}
