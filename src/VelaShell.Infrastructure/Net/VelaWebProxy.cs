using System.Net;
using VelaShell.Core.Net;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// 面向 <see cref="HttpClient" /> 家族的代理适配:安装为进程级
/// <see cref="HttpClient.DefaultProxy" /> 后,应用内所有未显式配置 Proxy 的
/// HttpClient(更新检查、Gist 同步、告警 Webhook、头像、插件)每次请求都会
/// 经由 <see cref="IProxyResolver" /> 取当前路由 —— 保存设置后无需重启即生效。
/// .NET 的 SocketsHttpHandler 原生支持 http:// 与 socks5:// 两种代理方案。
/// </summary>
public sealed class VelaWebProxy(IProxyResolver resolver) : IWebProxy
{
    /// <summary>
    /// 安装为进程级默认代理(幂等)。先把当前 <see cref="HttpClient.DefaultProxy" />
    /// 存为「系统代理」数据源,再行覆盖 —— 否则 system 档会解析到我们自己。
    /// </summary>
    public static void Install(IProxyResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (HttpClient.DefaultProxy is VelaWebProxy)
        {
            return;
        }
        ProxyResolver.SystemProxySource = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new VelaWebProxy(resolver);
    }

    /// <summary>代理认证凭据;按当前设置动态给出,setter 为满足接口而存在(忽略写入)。</summary>
    public ICredentials? Credentials
    {
        get => SafeRoute("proxy-credential-probe.invalid", 443)?.ToCredential();
        set { }
    }

    /// <inheritdoc />
    public Uri? GetProxy(Uri? destination)
    {
        if (destination is null)
        {
            return null;
        }
        // 解析失败(代理已启用但配置不完整)必须让请求失败,绝不静默直连泄漏流量。
        ProxyRoute route = resolver.Resolve(destination.Host, destination.Port);
        return route.Kind switch
        {
            ProxyKind.Http => new Uri($"http://{ProxyResolver.FormatHost(route.Host)}:{route.Port}"),
            ProxyKind.Socks5 => new Uri($"socks5://{ProxyResolver.FormatHost(route.Host)}:{route.Port}"),
            _ => null,
        };
    }

    /// <inheritdoc />
    public bool IsBypassed(Uri? host) =>
        host is null || SafeRoute(host.Host, host.Port) is { Kind: ProxyKind.None };

    /// <summary>IsBypassed/Credentials 语境下的容错解析:配置不完整时按“不绕过”处理,让 GetProxy 抛出明确错误。</summary>
    private ProxyRoute? SafeRoute(string host, int port)
    {
        try { return resolver.Resolve(host, port); }
        catch { return null; }
    }
}
