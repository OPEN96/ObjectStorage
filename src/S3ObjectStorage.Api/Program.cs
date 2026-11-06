using S3ObjectStorage.Api;
using S3ObjectStorage.Infrastructure;

var app = S3Application.Create(args);

// 建库 + 初始化密钥，必须在开始监听之前完成。
await app.Services.InitializeStorageAsync();

await app.RunAsync();

// 让集成测试可以通过 WebApplicationFactory 拿到宿主入口。
public partial class Program;
