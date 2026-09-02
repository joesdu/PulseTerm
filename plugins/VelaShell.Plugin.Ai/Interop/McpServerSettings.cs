using System.Security.Cryptography;
using System.Text.Json;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Interop;

/// <summary>
/// 对外 MCP 服务端的设置:让 Claude Code / Codex / Cursor 这类外部 agent
/// 反过来调用 VelaShell 的能力(枚举会话、读终端、跑命令、读写远端文件)。
/// </summary>
/// <remarks>
/// <b>方向要分清。</b>插件本来就是 MCP <i>客户端</i>(见 <c>Agent/McpManager</c>) ——
/// VelaShell 的 agent 去调别人的工具。这里是反过来:VelaShell 当<i>服务端</i>,
/// 别人的 agent 来调 VelaShell 的工具。两套东西没有共用代码,只共用一个名字。
/// </remarks>
public sealed class McpServerSettings
{
    /// <summary>
    /// 总开关。关着时不监听任何端口。<b>默认开</b>。
    /// </summary>
    /// <remarks>
    /// 默认打开一个监听端口需要有理由,这里的理由是三条叠起来之后风险足够低,
    /// 而"默认关"带来的代价(装完还得先去翻一页设置)恰恰是这个功能最没必要的门槛:
    /// <list type="number">
    /// <item>只绑 <c>127.0.0.1</c>,不提供绑其它地址的选项;</item>
    /// <item>每个请求都必须带令牌,而令牌是随机生成、不可关闭的;</item>
    /// <item>默认挡位是 <see cref="ChatMode.Plan" /> —— 外部 agent 开箱只能<b>看</b>,
    /// 改任何东西都要用户显式把挡位或审批方式调开。</item>
    /// </list>
    /// 换句话说:默认开的是一个"只读、要令牌、只在本机"的接口。
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>监听端口(只绑 127.0.0.1)。</summary>
    public int Port { get; set; } = 8391;

    /// <summary>
    /// 暴露哪一档工具。<b>默认计划档 = 只读</b> ——
    /// 外部 agent 的行为不在用户眼皮底下,默认不该能改任何东西。
    /// </summary>
    public ChatMode Mode { get; set; } = ChatMode.Plan;

    /// <summary>
    /// 写操作的审批方式。<see cref="ApprovalMode.Ask" /> 在这条路上<b>等于拒绝</b> ——
    /// 外部 agent 没有可以弹审批卡的界面(详见 <see cref="McpToolHost" />)。
    /// </summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.ReadOnlyAuto;

    /// <summary>不暴露给外部 agent 的工具名,每行一条。</summary>
    public string DisabledTools { get; set; } = "";

    /// <summary>
    /// 允许外部 agent 操作的服务器(<c>user@host:port</c>,每行一条;空 = 允许全部已连会话)。
    /// </summary>
    public string AllowedTargets { get; set; } = "";
}

/// <summary>MCP 服务端设置的读写。令牌走机密存储,不进明文配置。</summary>
public sealed class McpServerSettingsStore(IPluginContext context)
{
    private const string SettingsKey = "mcp-server";
    private const string TokenSecret = "mcp-server:token";

    /// <summary>读取设置(没有则返回默认值)。</summary>
    public async Task<McpServerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        JsonElement raw = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        return raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new McpServerSettings()
            : raw.Deserialize<McpServerSettings>() ?? new McpServerSettings();
    }

    /// <summary>持久化设置。</summary>
    public Task SaveAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
        => context.Storage.SetAsync(SettingsKey, settings, cancellationToken);

    /// <summary>
    /// 取访问令牌;<b>没有就现生成一个并存下</b>。
    /// </summary>
    /// <remarks>
    /// 监听在 127.0.0.1 上并不等于安全:同一台机器上任何进程(包括浏览器里的页面)
    /// 都能往本地端口发请求。令牌是这条路上唯一挡住"别的程序顺手调你的服务器"的东西,
    /// 所以它不是可选项,也不该让用户自己想一个。
    /// </remarks>
    public async Task<string> TokenAsync(CancellationToken cancellationToken = default)
    {
        if (await context.Secrets.GetAsync(TokenSecret, cancellationToken).ConfigureAwait(false) is { Length: > 0 } existing)
        {
            return existing;
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await context.Secrets.SetAsync(TokenSecret, token, cancellationToken).ConfigureAwait(false);
        return token;
    }

    /// <summary>换一个新令牌(设置页上的"重新生成")。</summary>
    public async Task<string> RotateTokenAsync(CancellationToken cancellationToken = default)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await context.Secrets.SetAsync(TokenSecret, token, cancellationToken).ConfigureAwait(false);
        return token;
    }
}
