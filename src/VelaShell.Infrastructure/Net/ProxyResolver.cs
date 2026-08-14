using System.Net;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// <see cref="IProxyResolver" /> 实现:每次解析都读当前设置(设置服务有进程内 JSON 缓存,
/// 读取廉价),保存代理设置后新建的连接立即生效,无需订阅保存事件。
/// </summary>
public sealed class ProxyResolver(ISettingsService settings) : IProxyResolver
{
    /// <summary>
    /// 「系统代理」的数据源。默认取进程启动时的 <see cref="HttpClient.DefaultProxy" />;
    /// <see cref="VelaWebProxy.Install" /> 覆盖 DefaultProxy 前会先把原值存进来,
    /// 避免 system 类型解析到我们自己而无限递归。
    /// </summary>
    internal static IWebProxy? SystemProxySource { get; set; }

    /// <inheritdoc />
    public ProxyRoute Resolve(string targetHost, int targetPort)
    {
        if (IsLoopback(targetHost))
        {
            return ProxyRoute.Direct;
        }
        ProxyOptions o = ReadOptions();
        return o.Type switch
        {
            "http" => Explicit(ProxyKind.Http, o),
            "socks5" => Explicit(ProxyKind.Socks5, o),
            "system" => FromSystem(targetHost, targetPort, o),
            _ => ProxyRoute.Direct,
        };
    }

    private ProxyOptions ReadOptions()
    {
        try { return settings.GetSettingsAsync().GetAwaiter().GetResult().Proxy; }
        catch { return new(); }
    }

    private static ProxyRoute Explicit(ProxyKind kind, ProxyOptions o) =>
        string.IsNullOrWhiteSpace(o.Host) || o.Port is < 1 or > 65535
            ? throw new InvalidOperationException(Strings.Get("Msg_ProxyMisconfigured"))
            : new(kind, o.Host.Trim(), o.Port, o.Username, o.Password, o.ProxyDns);

    private static ProxyRoute FromSystem(string targetHost, int targetPort, ProxyOptions o)
    {
        IWebProxy sys = SystemProxySource ?? HttpClient.DefaultProxy;
        if (sys is VelaWebProxy)
        {
            // 未经 Install 捕获且 DefaultProxy 已被替换:无法取到真实系统代理,按直连处理。
            return ProxyRoute.Direct;
        }
        try
        {
            // 系统代理按 URL 配置(可带 bypass 列表),用 https 目标探询最通用的一档。
            var target = new Uri($"https://{FormatHost(targetHost)}:{targetPort}/");
            if (sys.IsBypassed(target))
            {
                return ProxyRoute.Direct;
            }
            Uri? p = sys.GetProxy(target);
            if (p is null || p == target)
            {
                return ProxyRoute.Direct;
            }
            ProxyKind kind = p.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
                ? ProxyKind.Socks5
                : ProxyKind.Http;
            return new(kind, p.Host, p.Port, "", "", o.ProxyDns);
        }
        catch
        {
            // 系统代理探询失败(非法主机名等)不阻断连接:system 档语义是"跟随系统",系统无代理即直连。
            return ProxyRoute.Direct;
        }
    }

    /// <summary>环回目标永不走代理:代理自身的环回中继、本机实验环境都依赖这一点。</summary>
    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip));

    /// <summary>IPv6 字面量拼进 URL 需要方括号。</summary>
    internal static string FormatHost(string host) =>
        IPAddress.TryParse(host, out IPAddress? ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
}
