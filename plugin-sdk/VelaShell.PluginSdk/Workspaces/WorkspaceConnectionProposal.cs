namespace VelaShell.PluginSdk.Workspaces;

/// <summary>
/// 一条由插件提议的连接。宿主据此打开自己的「新建连接」对话框并预填,
/// **保存与否由用户决定** —— 插件不能自己写宿主的会话库。
/// </summary>
public sealed record WorkspaceConnectionProposal
{
    /// <summary>连接类型 id;必须是本插件注册过的那一个(或其子 id)。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>建议的连接名称(用户可改)。</summary>
    public required string Name { get; init; }

    /// <summary>主机。</summary>
    public required string Host { get; init; }

    /// <summary>端口。</summary>
    public required int Port { get; init; }

    /// <summary>用户名;不需要时留空。</summary>
    public string Username { get; init; } = "";

    /// <summary>
    /// 口令;不需要时留空。
    /// <para>
    /// 探测到的口令(如从远端配置文件里读到的 <c>requirepass</c>)可以放在这里 ——
    /// 它会随宿主的「记住密码」策略走加密落盘那条路。但**插件自己不得把它写进日志**。
    /// </para>
    /// </summary>
    public string Password { get; init; } = "";

    /// <summary>专属设置的预填值(键为 <see cref="Protocols.ProtocolSettingField.Key" />)。</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>建议归入的分组名;留空即不分组。</summary>
    public string GroupName { get; init; } = "";
}
