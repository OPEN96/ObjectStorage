using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 管理端（/admin）的端到端测试：认证、概览、桶与对象管理、访问密钥。
/// 走真实 Kestrel + Cookie 会话，和浏览器里的行为一致。
/// </summary>
[Collection(S3HostCollection.Name)]
public sealed class AdminApiTests
{
    private readonly S3TestHost _host;

    public AdminApiTests(S3TestHost host) => _host = host;

    [Fact]
    public async Task AdminUi_ShouldServeIndexHtml()
    {
        using var client = NewClient();
        var response = await client.GetAsync("/admin/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("管理控制台", html);
        Assert.Contains("/admin/app.js", html);
    }

    [Fact]
    public async Task AdminApi_WithoutSession_ShouldReturnUnauthorized()
    {
        using var client = NewClient();
        var response = await client.GetAsync("/admin/api/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithToken_ShouldIssueSessionCookie()
    {
        using var client = NewClient();

        var login = await client.PostAsJsonAsync("/admin/api/auth/login", new { token = S3TestHost.AdminToken });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/admin/api/auth/me");
        Assert.Equal("token", me.GetProperty("subject").GetString());
        Assert.False(me.GetProperty("viaAccessKey").GetBoolean());

        var overview = await client.GetAsync("/admin/api/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongToken_ShouldBeRejected()
    {
        using var client = NewClient();
        var login = await client.PostAsJsonAsync("/admin/api/auth/login", new { token = "definitely-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Login_WithAdminAccessKey_ShouldWork()
    {
        using var client = NewClient();
        var login = await client.PostAsJsonAsync("/admin/api/auth/login", new
        {
            accessKeyId = S3TestHost.AccessKey,
            secretAccessKey = S3TestHost.SecretKey,
        });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/admin/api/auth/me");
        Assert.Equal(S3TestHost.AccessKey, me.GetProperty("subject").GetString());
        Assert.True(me.GetProperty("viaAccessKey").GetBoolean());
    }

    [Fact]
    public async Task HeaderToken_ShouldAuthorizeWithoutLogin()
    {
        using var client = NewClient();
        client.DefaultRequestHeaders.Add("X-Admin-Token", S3TestHost.AdminToken);

        var response = await client.GetAsync("/admin/api/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BucketAndObjectLifecycle_ShouldWorkThroughAdminApi()
    {
        using var client = await LoggedInClientAsync();
        var bucket = NewBucketName();

        // 建桶
        var create = await client.PostAsJsonAsync("/admin/api/buckets", new { name = bucket });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // 重复建桶
        var duplicate = await client.PostAsJsonAsync("/admin/api/buckets", new { name = bucket });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // 上传（key 里带空格与中文，验证路径编码）
        const string key = "docs/集成 测试.txt";
        var payload = Encoding.UTF8.GetBytes("hello from admin console");
        var put = await client.PutAsync(
            $"/admin/api/buckets/{bucket}/objects/{EncodeKey(key)}",
            new ByteArrayContent(payload) { Headers = { ContentType = new("text/plain") } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var putJson = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(payload.Length, putJson.GetProperty("size").GetInt64());

        // 列举
        var listing = await client.GetFromJsonAsync<JsonElement>(
            $"/admin/api/buckets/{bucket}/objects?prefix=docs/&delimiter=/");
        var keys = listing.GetProperty("objects").EnumerateArray()
            .Select(o => o.GetProperty("key").GetString()).ToList();
        Assert.Contains(key, keys);

        // 下载（编码斜杠形式也要能命中）
        var encodedWhole = Uri.EscapeDataString(key);
        var download = await client.GetAsync($"/admin/api/buckets/{bucket}/objects/{encodedWhole}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());

        // 桶统计
        var detail = await client.GetFromJsonAsync<JsonElement>($"/admin/api/buckets/{bucket}");
        Assert.Equal(1, detail.GetProperty("objectCount").GetInt64());
        Assert.Equal(payload.Length, detail.GetProperty("totalSize").GetInt64());

        // 非空桶不允许直接删
        var refused = await client.DeleteAsync($"/admin/api/buckets/{bucket}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // 强制删除
        var forced = await client.DeleteAsync($"/admin/api/buckets/{bucket}?force=true");
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);

        var after = await client.GetAsync($"/admin/api/buckets/{bucket}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task BulkDelete_ShouldRemoveMultipleObjects()
    {
        using var client = await LoggedInClientAsync();
        var bucket = NewBucketName();
        await client.PostAsJsonAsync("/admin/api/buckets", new { name = bucket });

        foreach (var key in new[] { "a.txt", "dir/b.txt", "dir/c.txt" })
        {
            var response = await client.PutAsync(
                $"/admin/api/buckets/{bucket}/objects/{EncodeKey(key)}",
                new ByteArrayContent([1, 2, 3]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var delete = await client.PostAsJsonAsync(
            $"/admin/api/buckets/{bucket}/objects/delete",
            new { keys = new[] { "a.txt", "dir/b.txt" } });

        var result = await delete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("deleted").GetInt32());

        var detail = await client.GetFromJsonAsync<JsonElement>($"/admin/api/buckets/{bucket}");
        Assert.Equal(1, detail.GetProperty("objectCount").GetInt64());

        await client.DeleteAsync($"/admin/api/buckets/{bucket}?force=true");
    }

    [Fact]
    public async Task AccessKeys_ShouldCreateToggleAndDelete()
    {
        using var client = await LoggedInClientAsync();

        var create = await client.PostAsJsonAsync("/admin/api/access-keys", new { displayName = "it-bot", isAdmin = false });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var keyId = created.GetProperty("accessKeyId").GetString()!;
        Assert.StartsWith("AKIA", keyId);
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("secretAccessKey").GetString()));

        // 列表里不应返回 Secret
        var list = await client.GetStringAsync("/admin/api/access-keys");
        Assert.Contains(keyId, list);
        Assert.DoesNotContain(created.GetProperty("secretAccessKey").GetString()!, list);

        // 禁用
        var disable = await client.PatchAsJsonAsync($"/admin/api/access-keys/{keyId}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        // 普通密钥禁用后无法登录
        using var other = NewClient();
        var login = await other.PostAsJsonAsync("/admin/api/auth/login", new
        {
            accessKeyId = keyId,
            secretAccessKey = created.GetProperty("secretAccessKey").GetString(),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // 删除
        var delete = await client.DeleteAsync($"/admin/api/access-keys/{keyId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    [Fact]
    public async Task LastAdminKey_ShouldNotBeDeletable()
    {
        using var client = await LoggedInClientAsync();

        // 这个宿主只配了一把管理员密钥，删除应被拒绝。
        var delete = await client.DeleteAsync($"/admin/api/access-keys/{S3TestHost.AccessKey}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task AccessKeySecret_ShouldBeRotatableInPlace()
    {
        using var client = await LoggedInClientAsync();

        var create = await client.PostAsJsonAsync("/admin/api/access-keys", new { displayName = "rotate-me", isAdmin = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var keyId = created.GetProperty("accessKeyId").GetString()!;
        var oldSecret = created.GetProperty("secretAccessKey").GetString()!;

        // 过长或过短都不合法
        var tooShort = await client.PatchAsJsonAsync($"/admin/api/access-keys/{keyId}", new { secretAccessKey = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        const string newSecret = "rotated-secret-0123456789abcdef";
        var rotate = await client.PatchAsJsonAsync($"/admin/api/access-keys/{keyId}", new { secretAccessKey = newSecret });
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);

        var rotated = await rotate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rotated.GetProperty("rotated").GetBoolean());
        Assert.Equal(keyId, rotated.GetProperty("accessKeyId").GetString());

        // 旧 Secret 立即失效，新 Secret 立即生效，且 AccessKeyId 未变（桶属主不受影响）
        using var stale = NewClient();
        var oldLogin = await stale.PostAsJsonAsync("/admin/api/auth/login", new { accessKeyId = keyId, secretAccessKey = oldSecret });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var fresh = NewClient();
        var newLogin = await fresh.PostAsJsonAsync("/admin/api/auth/login", new { accessKeyId = keyId, secretAccessKey = newSecret });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

        await client.DeleteAsync($"/admin/api/access-keys/{keyId}");
    }

    [Fact]
    public async Task AccessKeySession_ShouldBeRevokedWhenKeyDeleted()
    {
        using var admin = await LoggedInClientAsync();

        var create = await admin.PostAsJsonAsync("/admin/api/access-keys", new { displayName = "session-bot", isAdmin = true });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var keyId = created.GetProperty("accessKeyId").GetString()!;

        using var session = NewClient();
        var login = await session.PostAsJsonAsync("/admin/api/auth/login", new
        {
            accessKeyId = keyId,
            secretAccessKey = created.GetProperty("secretAccessKey").GetString(),
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/admin/api/access-keys")).StatusCode);

        await admin.DeleteAsync($"/admin/api/access-keys/{keyId}");

        // 会话 Cookie 未过期，但密钥已不存在，必须立刻失效。
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/admin/api/access-keys")).StatusCode);
    }

    [Fact]
    public async Task AccessKeySession_ShouldBeRevokedWhenSecretRotated()
    {
        using var admin = await LoggedInClientAsync();

        var create = await admin.PostAsJsonAsync("/admin/api/access-keys", new { displayName = "rotate-session", isAdmin = true });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var keyId = created.GetProperty("accessKeyId").GetString()!;

        using var session = NewClient();
        await session.PostAsJsonAsync("/admin/api/auth/login", new
        {
            accessKeyId = keyId,
            secretAccessKey = created.GetProperty("secretAccessKey").GetString(),
        });
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/admin/api/access-keys")).StatusCode);

        await admin.PatchAsJsonAsync($"/admin/api/access-keys/{keyId}", new { secretAccessKey = "another-secret-0123456789abcdef" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/admin/api/access-keys")).StatusCode);

        await admin.DeleteAsync($"/admin/api/access-keys/{keyId}");
    }

    [Fact]
    public async Task RootPath_FromBrowser_ShouldRedirectToAdmin()
    {
        // HttpClient 默认会自动跟随 302，这里要看重定向本身。
        using var client = NewClient(allowAutoRedirect: false);
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AdminResponses_ShouldCarrySecurityHeaders()
    {
        using var client = NewClient();
        var response = await client.GetAsync("/admin/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task AdminErrorResponses_ShouldAlsoCarrySecurityHeaders()
    {
        using var client = NewClient();
        using var response = await client.GetAsync("/admin/api/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // 错误响应不能因为重新写状态码/正文而丢掉安全头。
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Login_FromForeignOrigin_ShouldBeRejected()
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/api/auth/login");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        request.Content = JsonContent.Create(new { token = S3TestHost.AdminToken });

        using var response = await client.SendAsync(request);

        // 即使令牌正确，跨站来源也必须被拒绝（CSRF 纵深防御）。
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadsEndpoint_ShouldReportUploadedPartCounts()
    {
        using var client = await LoggedInClientAsync();
        var bucket = NewBucketName();
        await client.PostAsJsonAsync("/admin/api/buckets", new { name = bucket });

        var initiate = await _host.Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "pending.bin",
        });

        var payload = Encoding.UTF8.GetBytes("pending part");
        await using var stream = new MemoryStream(payload);
        await _host.Client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = "pending.bin",
            UploadId = initiate.UploadId,
            PartNumber = 1,
            InputStream = stream,
            DisableDefaultChecksumValidation = true,
        });

        var listing = await client.GetFromJsonAsync<JsonElement>($"/admin/api/buckets/{bucket}/uploads");
        var upload = listing.GetProperty("uploads").EnumerateArray()
            .Single(u => u.GetProperty("uploadId").GetString() == initiate.UploadId);

        Assert.Equal(1, upload.GetProperty("uploadedParts").GetInt32());
        Assert.Equal(payload.Length, upload.GetProperty("uploadedBytes").GetInt64());

        await _host.Client.AbortMultipartUploadAsync(bucket, "pending.bin", initiate.UploadId);
        await client.DeleteAsync($"/admin/api/buckets/{bucket}?force=true");
    }

    // ------------------------------------------------------------ 辅助

    private HttpClient NewClient(bool allowAutoRedirect = true)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = allowAutoRedirect,
        };

        return new HttpClient(handler) { BaseAddress = new Uri(_host.ServiceUrl) };
    }

    private async Task<HttpClient> LoggedInClientAsync()
    {
        var client = NewClient();
        var login = await client.PostAsJsonAsync("/admin/api/auth/login", new { token = S3TestHost.AdminToken });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static string NewBucketName() => "admin-it-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>按路径段编码，保留 '/' 作为分隔符（与前端一致）。</summary>
    private static string EncodeKey(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
}
