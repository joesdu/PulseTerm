using VelaShell.Core.Models;

namespace VelaShell.Core.Ftp;

/// <summary>
/// 建立一条 FTP / FTPS 会话所需的全部参数。
/// <para>
/// 刻意与 <see cref="ConnectionInfo" />(SSH 传输参数)分开:FTP 不经 SSH 握手,
/// 把两者揉进同一个 required/init 对象只会让双方都长出对方用不到的字段。
/// </para>
/// </summary>
public sealed class FtpConnectionInfo
{
    /// <summary>匿名登录使用的用户名。</summary>
    public const string AnonymousUser = "anonymous";

    /// <summary>目标主机(主机名或 IP)。</summary>
    public required string Host { get; init; }

    /// <summary>控制连接端口。</summary>
    public int Port { get; init; } = FtpSettings.DefaultPort;

    /// <summary>登录用户名;匿名登录时忽略。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>登录口令;匿名登录时忽略。</summary>
    public string? Password { get; init; }

    /// <summary>协议专属设置(加密方式、数据连接方式、并发数等)。</summary>
    public required FtpSettings Settings { get; init; }

    /// <summary>用于日志与错误提示的可读名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>按 <see cref="FtpSettings.Anonymous" /> 解析出的实际登录用户名。</summary>
    public string EffectiveUsername => Settings.Anonymous ? AnonymousUser : Username;

    /// <summary>按 <see cref="FtpSettings.Anonymous" /> 解析出的实际登录口令(匿名时用邮箱占位)。</summary>
    public string EffectivePassword => Settings.Anonymous ? "anonymous@velashell" : Password ?? string.Empty;

    /// <summary>从一条会话配置构建连接参数;<see cref="SessionProfile.Ftp" /> 缺失时用默认设置。</summary>
    public static FtpConnectionInfo FromProfile(SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new()
        {
            Host = profile.Host.Trim(),
            Port = profile.Port > 0 ? profile.Port : FtpSettings.DefaultPort,
            Username = profile.Username.Trim(),
            Password = profile.Password,
            // 「最近连接」重连时会拿一份没有 Ftp 块的临时配置过来,此处兜底成默认设置。
            Settings = profile.Ftp ?? new FtpSettings(),
            DisplayName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name,
        };
    }
}
