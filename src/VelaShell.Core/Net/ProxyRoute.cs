using System.Net;

namespace VelaShell.Core.Net;

/// <summary>出站代理类别(已解析:设置里的 system 已折算为具体类别或直连)。</summary>
public enum ProxyKind
{
    /// <summary>直连,不经代理。</summary>
    None,

    /// <summary>HTTP CONNECT 隧道代理。</summary>
    Http,

    /// <summary>SOCKS5 代理(RFC 1928 / 1929)。</summary>
    Socks5,
}

/// <summary>
/// 一次出站连接的已解析代理路由:把设置里的 none / system / http / socks5 折算成
/// 「直连或一个具体代理端点」。所有出站通道(SSH / FTP / HTTP)共用此结果,
/// 各通道的适配层只做协议映射,不各自读取代理设置。
/// </summary>
public sealed record ProxyRoute(
    ProxyKind Kind,
    string Host = "",
    int Port = 0,
    string Username = "",
    string Password = "",
    bool ProxyDns = true)
{
    /// <summary>直连路由(未启用代理,或目标本身是环回地址)。</summary>
    public static ProxyRoute Direct { get; } = new(ProxyKind.None);

    /// <summary>是否携带代理认证凭据。</summary>
    public bool HasCredentials => Username.Length > 0;

    /// <summary>转成 <see cref="NetworkCredential" />;无凭据时为 null。</summary>
    public NetworkCredential? ToCredential() => HasCredentials ? new(Username, Password) : null;
}

/// <summary>
/// 统一代理解析入口:任何功能要发起出站连接,一律经由本接口取路由;
/// 后续新功能接入网络时同样只消费它,不得各自读取代理设置或自行实现代理逻辑。
/// </summary>
public interface IProxyResolver
{
    /// <summary>
    /// 解析连接到 <paramref name="targetHost" />:<paramref name="targetPort" /> 应使用的代理路由。
    /// none 或环回目标返回 <see cref="ProxyRoute.Direct" />;system 按系统代理折算;
    /// http / socks5 校验主机端口,配置不完整时抛 <see cref="InvalidOperationException" />
    /// (用户显式要求走代理时绝不静默直连)。
    /// </summary>
    ProxyRoute Resolve(string targetHost, int targetPort);
}
