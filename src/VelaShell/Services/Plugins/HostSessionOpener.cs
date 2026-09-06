using System.Globalization;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.ViewModels;

namespace VelaShell.Services.Plugins;

/// <summary>
/// <see cref="IPluginSessionOpener" /> 的宿主实现:授权闸 → 宿主既有的连接流程 → 一个真实的标签页。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么走 <see cref="MainWindowViewModel.TryConnectProfileAsync" /> 而不是直接
/// <see cref="ISshConnectionService.ConnectAsync" />。</b>后者能连上,但连出来的是一条
/// 用户在界面上看不见的会话:没有标签页,关不掉,断了也没人知道。插件替人连的机器
/// 尤其不该是隐形的 —— 用户点了"同意",他期待的是屏幕上多出一台机器,
/// 而不是后台多了一条自己无从察觉的 SSH。顺带,凭据弹窗、跳板链、主机指纹确认、
/// 连接历史与审计也都在那条路上,复用它等于这些一件都没漏。
/// </para>
/// <para>
/// 授权闸在连接之前:先问人,再动网络。反过来的话,用户点"拒绝"时机器早就连上了。
/// </para>
/// </remarks>
internal sealed class HostSessionOpener(
    Func<MainWindowViewModel?> viewModel,
    ISshConnectionService connections,
    PluginPermissionGate gate) : IPluginSessionOpener
{
    public async Task<PluginSessionOpenResult> OpenAsync(string pluginId, SessionProfile profile, string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (viewModel() is not { } vm)
        {
            // 主窗口还没建起来(启动早期的惰性激活)。这不是拒绝,过一会儿再来就行。
            return PluginSessionOpenResult.Failed("The main window is not ready yet.");
        }
        bool allowed = await gate.CheckSessionOpenAsync(pluginId, Describe(profile), reason, cancellationToken)
                                 .ConfigureAwait(false);
        if (!allowed)
        {
            return PluginSessionOpenResult.Denied($"The user denied opening a session for plugin '{pluginId}'.");
        }
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile, cancellationToken);
            if (tab is { ConnectionStatus: SessionStatus.Connected } connected && connected.SessionId != Guid.Empty)
            {
                return PluginSessionOpenResult.Opened(connected.SessionId);
            }
            // TryConnectProfileAsync 从不让连接异常逃逸 —— 失败原因落在 LastConnectionError 上,
            // 而用户在凭据弹窗上取消时它被显式清空。这是此处区分"没连上"与"人不同意"的依据。
            string? error = vm.LastConnectionError;
            return string.IsNullOrWhiteSpace(error)
                ? PluginSessionOpenResult.Denied("The user cancelled the credential prompt.")
                : PluginSessionOpenResult.Failed(error);
        });
    }

    public async Task CloseAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        bool closed = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (viewModel() is not { } vm)
            {
                return false;
            }
            TerminalTabViewModel? tab = vm.TerminalTabs
                                          .FirstOrDefault(t => t.SessionId == sessionId);
            if (tab is null)
            {
                return false;
            }
            // 用户语义的关闭:标签、停靠文档、会话日志与底层传输一并拆掉,SSH 会话随之断开。
            vm.CloseTerminalTab(tab);
            return true;
        });
        if (!closed)
        {
            // 没有对应标签(用户先手动关了,或会话根本不是标签页开的):直接拆连接。
            // DisconnectAsync 本身幂等,会话已不在时是空操作。
            await connections.DisconnectAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>确认框上的那台机器:会话树上的名字 + <c>user@host:port</c>。</summary>
    /// <remarks>
    /// 名字与地址都要给。只给名字("生产 3 号")的话,用户没法确认插件要连的是不是
    /// 他以为的那台;只给地址则要用户自己把 IP 翻译回机器 —— 确认框上多花的这一秒,
    /// 换的是他真的看懂了自己在同意什么。
    /// </remarks>
    private static string Describe(SessionProfile profile)
    {
        string address = string.IsNullOrWhiteSpace(profile.Username)
            ? string.Create(CultureInfo.InvariantCulture, $"{profile.Host}:{profile.Port}")
            : string.Create(CultureInfo.InvariantCulture, $"{profile.Username}@{profile.Host}:{profile.Port}");
        return string.IsNullOrWhiteSpace(profile.Name) ? address : $"{profile.Name} ({address})";
    }
}
