using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using S3ObjectStorage.Api;
using S3ObjectStorage.Api.Admin;
using S3ObjectStorage.Domain.Exceptions;
using S3ObjectStorage.S3Protocol.Xml;

namespace S3ObjectStorage.Tests;

/// <summary>不依赖宿主的加固逻辑单元测试：登录节流、令牌引导、XML 体积上限。</summary>
public sealed class HardeningUnitTests
{
    [Fact]
    public void LoginThrottle_ShouldBlockAfterTenFailures()
    {
        var throttle = new AdminLoginThrottle();
        const string source = "203.0.113.7";

        for (var i = 0; i < 9; i++)
        {
            throttle.RecordFailure(source);
        }

        Assert.False(throttle.IsBlocked(source, out _));

        throttle.RecordFailure(source);

        Assert.True(throttle.IsBlocked(source, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // 不同来源互不影响。
        Assert.False(throttle.IsBlocked("198.51.100.9", out _));

        throttle.Reset(source);
        Assert.False(throttle.IsBlocked(source, out _));
    }

    [Fact]
    public void EnsureToken_ShouldGenerateOnlyInDevelopment()
    {
        var development = new AdminOptions { Enabled = true, Token = string.Empty };
        var generated = AdminAuth.EnsureToken(development, NullLogger.Instance, isDevelopment: true);

        Assert.False(string.IsNullOrWhiteSpace(generated));
        Assert.Equal(generated, development.Token);

        var production = new AdminOptions { Enabled = true, Token = string.Empty };
        Assert.Throws<InvalidOperationException>(
            () => AdminAuth.EnsureToken(production, NullLogger.Instance, isDevelopment: false));

        var configured = new AdminOptions { Enabled = true, Token = "already-configured-token" };
        Assert.Equal(
            "already-configured-token",
            AdminAuth.EnsureToken(configured, NullLogger.Instance, isDevelopment: false));
    }

    [Fact]
    public async Task XmlDeserializer_ShouldRejectBodyOverLimit()
    {
        var payload = Encoding.UTF8.GetBytes("<Delete><Object><Key>" + new string('k', 4096) + "</Key></Object></Delete>");
        await using var stream = new MemoryStream(payload);

        var exception = await Assert.ThrowsAsync<S3Exception>(
            () => S3Xml.DeserializeAsync<DeleteObjectsRequest>(stream, maxBytes: 1024));

        Assert.Equal(S3ErrorCode.EntityTooLarge, exception.ErrorCode);
    }

    [Fact]
    public async Task XmlDeserializer_ShouldAcceptBodyWithinLimit()
    {
        var payload = Encoding.UTF8.GetBytes("<Delete><Object><Key>a.txt</Key></Object></Delete>");
        await using var stream = new MemoryStream(payload);

        var request = await S3Xml.DeserializeAsync<DeleteObjectsRequest>(stream, maxBytes: 1024);

        Assert.Equal(["a.txt"], request.Objects.Select(o => o.Key).ToArray());
    }

    [Fact]
    public void ReverseProxy_ShouldTrustOnlyConfiguredProxies()
    {
        var options = new ForwardedHeadersOptions();
        ReverseProxyOptions.Apply(options, new ReverseProxyOptions
        {
            Enabled = true,
            KnownProxies = ["127.0.0.1", "10.0.0.0/8", "::1"],
            ForwardLimit = 2,
        });

        // 框架默认白名单（loopback）必须先被清掉，否则容器内任何来源都能伪造来源 IP。
        Assert.Equal(2, options.ForwardLimit);
        Assert.Contains(System.Net.IPAddress.Parse("127.0.0.1"), options.KnownProxies);
        Assert.Contains(System.Net.IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.Single(options.KnownIPNetworks);
        Assert.Equal(
            System.Net.IPNetwork.Parse("10.0.0.0/8").ToString(),
            options.KnownIPNetworks[0].ToString());
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("0.0.0.0/0")] // 等于信任所有来源，会让 X-Forwarded-For 可被任意伪造
    public void ReverseProxy_ShouldRejectUnsafeEntries(string entry)
    {
        var options = new ForwardedHeadersOptions();

        var error = Assert.Throws<InvalidOperationException>(
            () => ReverseProxyOptions.Apply(options, new ReverseProxyOptions { Enabled = true, KnownProxies = [entry] }));

        Assert.Contains("KnownProxies", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReverseProxy_WithoutAnyProxy_ShouldFailFast()
    {
        var options = new ForwardedHeadersOptions();

        Assert.Throws<InvalidOperationException>(
            () => ReverseProxyOptions.Apply(options, new ReverseProxyOptions { Enabled = true }));
    }
}
