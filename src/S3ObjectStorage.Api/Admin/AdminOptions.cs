namespace S3ObjectStorage.Api.Admin;

/// <summary>
/// 网页管理端的配置。
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>是否启用管理端；关闭后 /admin 全路径返回 404。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 管理员登录令牌。留空时启动阶段会生成一个随机令牌并打到日志里。
    /// 生产环境请通过环境变量 <c>S3STORAGE_Admin__Token</c> 注入。
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>登录会话有效期（小时）。</summary>
    public int SessionHours { get; set; } = 12;
}
