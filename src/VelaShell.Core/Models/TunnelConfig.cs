namespace VelaShell.Core.Models;

/// <summary>端口转发隧道的配置:类型、名称及本地/远端监听与目标地址。</summary>
public sealed class TunnelConfig
{
    /// <summary>转发类型:本地、远程或动态(SOCKS)。</summary>
    public required TunnelType Type { get; init; }

    /// <summary>隧道显示名称。</summary>
    public required string Name { get; init; }

    /// <summary>本地监听主机(通常为 127.0.0.1 或 0.0.0.0)。</summary>
    public required string LocalHost { get; init; }

    /// <summary>本地监听端口。</summary>
    public required uint LocalPort { get; init; }

    /// <summary>转发目标主机;动态转发(SOCKS)无固定目标,允许留空。</summary>
    public string RemoteHost { get; init; } = string.Empty;

    /// <summary>转发目标端口;动态转发(SOCKS)无固定目标,允许为 0。</summary>
    public uint RemotePort { get; init; }

    /// <summary>
    /// 承载会话掉线后是否自动重连并重建这条隧道。默认关闭:自动重连会替用户
    /// 重新拨号(可能触发凭据提示、也可能反复撞上同一个不可达的服务器),
    /// 该由用户按隧道自己决定。旧的持久化配置缺这个字段时按 false 反序列化。
    /// </summary>
    public bool AutoReconnect { get; init; }
}
