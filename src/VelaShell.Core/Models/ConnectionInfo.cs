namespace VelaShell.Core.Models;

/// <summary>
/// SSH 连接信息与凭据
/// </summary>
public class ConnectionInfo
{
    /// <summary>
    /// 获取或设置主机名或 IP 地址
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// 获取或设置 SSH 端口(默认:22)
    /// </summary>
    public int Port { get; init; } = 22;

    /// <summary>
    /// 获取或设置用户名
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// 获取或设置认证方式
    /// </summary>
    public required AuthMethod AuthMethod { get; init; }

    /// <summary>
    /// 获取或设置密码(用于密码认证)
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// 获取或设置私钥文件路径(用于私钥认证)
    /// </summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// 获取或设置私钥口令(可选)
    /// </summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>
    /// 跳板主机(ProxyJump):先连它,再经它连到本机。递归嵌套即多段跳;
    /// 由工作流按 <c>SessionProfile.JumpHostProfileId</c> 链解析(带环检测)。null = 直连。
    /// 具体建链方式由 Infrastructure 决定(当前为 Tmds.Ssh 原生 SshProxy 链)。
    /// </summary>
    public ConnectionInfo? JumpHost { get; init; }

    /// <summary>
    /// 本次连接的保活心跳间隔(秒,0 = 关闭);null = 跟随全局设置。
    /// </summary>
    /// <remarks>
    /// 来自 <c>SessionProfile.Terminal.KeepAliveSeconds</c>(F-06)。放在这里而不是让
    /// Infrastructure 再去查一次配置:建链时才知道这一跳是哪条配置,而跳板链上每一跳
    /// 都可以有自己的设置 —— 客户端工厂那一层手里只有一个 <see cref="ConnectionInfo" />。
    /// </remarks>
    public int? KeepAliveSeconds { get; init; }
}
