using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Builder;
using S3ObjectStorage.Api;
using S3ObjectStorage.Infrastructure;

namespace S3ObjectStorage.Tests;

/// <summary>
/// 用真实 Kestrel 拉起服务端，让 AWS 官方 SDK 通过 HTTP 访问它，
/// 从而端到端验证 SigV4 签名、XML 报文与语义的正确性。
/// </summary>
public sealed class S3TestHost : IAsyncLifetime
{
    public const string AccessKey = "AKIATESTLOCAL000001";
    public const string SecretKey = "integration-secret-key-0123456789abcdef";
    public const string AdminToken = "integration-admin-token-0123456789";

    private string? _root;

    public string ServiceUrl { get; private set; } = string.Empty;

    /// <summary>本次测试使用的存储根目录（临时目录）。</summary>
    public string RootPath => _root ?? throw new InvalidOperationException("宿主尚未初始化");

    public IAmazonS3 Client { get; private set; } = null!;

    public WebApplication App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "s3objstore-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        ServiceUrl = "http://127.0.0.1:" + GetFreePort();

        var settings = new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = _root,
            ["Storage:BootstrapAccessKeys:0:AccessKeyId"] = AccessKey,
            ["Storage:BootstrapAccessKeys:0:SecretAccessKey"] = SecretKey,
            ["Storage:BootstrapAccessKeys:0:DisplayName"] = "integration",
            ["Storage:BootstrapAccessKeys:0:IsAdmin"] = "true",
            ["Admin:Token"] = AdminToken,
            ["Logging:LogLevel:Default"] = "Warning",
            // appsettings.json 里的 Kestrel 端点优先级高于 UseUrls，这里显式覆盖。
            ["Kestrel:Endpoints:Http:Url"] = ServiceUrl,
        };

        App = S3Application.Create([], urls: ServiceUrl, settings: settings);
        await App.Services.InitializeStorageAsync();
        await App.StartAsync();

        Client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 0,
            });

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        // SQLite 连接池会持有 db 文件句柄，释放后才能删掉临时目录。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (_root is not null && Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结果。
            }
        }
    }

    public IAmazonS3 CreateTamperedClient() => new AmazonS3Client(
        new BasicAWSCredentials(AccessKey, SecretKey + "-wrong"),
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
        });

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
