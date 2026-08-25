using System.Text.Json.Serialization;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Startup;

/// <summary>外部拉起请求的类型。</summary>
public enum ExternalLaunchKind
{
    /// <summary>只是把已在运行的窗口唤到前台(第二次双击图标、托盘隐藏后再启动)。</summary>
    Activate,

    /// <summary>带目标(可能还带一次性凭据)的连接请求。</summary>
    Connect
}

/// <summary>请求是怎么进来的;确认弹窗要把它原样告诉用户。</summary>
public enum ExternalLaunchOrigin
{
    /// <summary><c>-url</c> / <c>-l</c> / <c>-p</c> 等 Xshell 风格命令行参数。</summary>
    CommandLine,

    /// <summary>系统 URL 协议(<c>ssh://</c> / <c>sftp://</c>),即网页里点一下就唤起本应用。</summary>
    UrlProtocol,

    /// <summary><c>-f &lt;session.xsh&gt;</c> 指向的 Xshell 会话文件。</summary>
    SessionFile
}

/// <summary>
/// 一次「从进程外拉起 VelaShell 去连某台服务器」的请求。Xshell 兼容登录的唯一数据载体:
/// 命令行、URL 协议、<c>.xsh</c> 会话文件三条入口都归一到它,单实例转发也只传它。
/// <para>
/// <see cref="Password" /> 是一次性凭据(堡垒机/SSO 现发的),**从不落盘**:
/// 由此建出的 <see cref="SessionProfile" /> 一律 <c>RememberPassword = false</c>
/// 且不写入会话仓储;<see cref="ToString" /> 也刻意不含密码,免得随手一句日志就把它抄进文件。
/// </para>
/// </summary>
public sealed class ExternalLaunchRequest
{
    /// <summary>请求类型;默认是连接请求。</summary>
    public ExternalLaunchKind Kind { get; init; } = ExternalLaunchKind.Connect;

    /// <summary>原始 scheme(小写:ssh / sftp / ftp / ftps / telnet …),确认弹窗按它显示协议名。</summary>
    public string Scheme { get; init; } = "ssh";

    /// <summary>映射后的连接类型;<see cref="IsSupported" /> 为 false 时该值无意义。</summary>
    public ConnectionType ConnectionType { get; init; } = ConnectionType.SSH;

    /// <summary>本应用是否支持这个 scheme(telnet / rlogin / serial 等一律 false,给出可读提示而不是静默丢弃)。</summary>
    public bool IsSupported { get; init; } = true;

    /// <summary>目标主机(主机名或 IP;IPv6 已去掉方括号)。</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>目标端口;URL 未给出时按 scheme 取默认值。</summary>
    public int Port { get; init; } = 22;

    /// <summary>登录用户名;缺省为空(交给应用内的登录弹窗问)。</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>随请求传来的一次性密码;没有则为 null。绝不持久化。</summary>
    public string? Password { get; init; }

    /// <summary>随请求指定的私钥文件(Xshell <c>-i</c>);没有则为 null。</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>请求来源。</summary>
    public ExternalLaunchOrigin Origin { get; init; } = ExternalLaunchOrigin.CommandLine;

    /// <summary>
    /// 信任判据:scheme + 用户名 + 主机 + 端口。用户勾了「不再询问」记的就是它 ——
    /// 粒度必须含用户名与端口,否则「信任 10.0.3.21」会连带放行 <c>root@10.0.3.21</c>。
    /// </summary>
    [JsonIgnore]
    public string TrustKey =>
        $"{Scheme.ToLowerInvariant()}://{Username}@{Host.ToLowerInvariant()}:{Port}";

    /// <summary>给人看的目标(<c>user@host:port</c>);没有用户名时省掉前缀。</summary>
    [JsonIgnore]
    public string DisplayTarget =>
        Username.Length > 0 ? $"{Username}@{Host}:{Port}" : $"{Host}:{Port}";

    /// <summary>诊断用的字符串。**刻意不含密码**:这条随时可能被 Trace 抄进日志文件。</summary>
    public override string ToString() =>
        $"{Kind} {Scheme}://{DisplayTarget} (origin={Origin}, credentials={(Password is null ? "none" : "supplied")})";
}
