using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Services;

namespace VelaShell.Docking;

/// <summary>
/// 由插件全权渲染的会话文档(Redis、MySQL…)的停靠标签页。
/// <para>
/// 与 <see cref="SftpDocument" /> 的分工:那个的内容是宿主自己的双栏浏览器,这个的内容是
/// **插件的控件**。宿主在这里只负责标签页外壳(标题、强调色、关闭)与状态呈现;
/// 连接、界面与数据全在插件那边。
/// </para>
/// </summary>
public sealed class PluginWorkspaceDocument : DockDocument, IDockViewProvider
{
    private int _closed;

    /// <summary>从插件交出的文档初始化停靠标签页。</summary>
    /// <param name="profile">用户的连接配置。</param>
    /// <param name="sessionId">宿主分配的会话 id。</param>
    /// <param name="typeName">连接类型的展示名(如 <c>Redis</c>),用于提示文本。</param>
    /// <param name="workspace">插件交出的文档。</param>
    public PluginWorkspaceDocument(SessionProfile profile, Guid sessionId, string typeName, IWorkspaceDocument workspace)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        TypeName = typeName;
        SessionId = sessionId;
        Id = sessionId.ToString("N");
        Title = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name;
        Status = Map(SafeState());
        // 标签上的状态圆点跟着插件报的状态走。插件在**任意线程**触发这个事件,
        // 而属性通知会直接驱动绑定 —— 不封送回 UI 线程就是一次跨线程改可视树。
        Workspace.StatusChanged += OnWorkspaceStatus;
    }

    /// <summary>连接类型的展示名(如 <c>Redis</c>),标签与提示文本用。</summary>
    public string TypeName { get; }

    /// <summary>
    /// 标签上的状态。刻意映射成宿主的 <see cref="SessionStatus" /> 而不是直接暴露
    /// SDK 的 <see cref="ProtocolSessionState" />:这样标签能复用与终端/SFTP 完全同一个
    /// 状态圆点转换器,三种标签的绿/黄/红是同一套语言。
    /// </summary>
    public SessionStatus Status
    {
        get;
        private set => SetField(ref field, value);
    }

    private void OnWorkspaceStatus(object? sender, WorkspaceStatus status) =>
        Dispatcher.UIThread.Post(() => Status = Map(status.State));

    private static SessionStatus Map(ProtocolSessionState state) => state switch
    {
        ProtocolSessionState.Connected => SessionStatus.Connected,
        ProtocolSessionState.Faulted => SessionStatus.Error,
        _ => SessionStatus.Disconnected
    };

    private ProtocolSessionState SafeState()
    {
        try
        {
            return Workspace.Status.State;
        }
        catch
        {
            // 插件的属性访问器自爆不该让标签页建不出来。
            return ProtocolSessionState.Faulted;
        }
    }

    /// <summary>该文档背后的连接配置。</summary>
    public SessionProfile Profile { get; }

    /// <summary>宿主分配的会话 id(与树上的状态圆点、关闭流程共用)。</summary>
    public Guid SessionId { get; }

    /// <summary>插件交出的文档(状态与重连由它提供)。</summary>
    public IWorkspaceDocument Workspace { get; }

    /// <summary>从连接配置派生的强调色画刷,用于视觉标识(与终端/SFTP 标签同一套)。</summary>
    public IBrush ConnectionAccentBrush => ConnectionAccent.BrushFor(Profile.Id);

    /// <summary>显示连接详情的提示文本。</summary>
    public string ConnectionTooltip => $"{Title} · {TypeName} · {Profile.Host}:{Profile.Port}";

    /// <summary>
    /// 创建停靠内容:向插件索取控件。
    /// <para>
    /// 全程守卫:插件的工厂抛异常、或返回了一个不是 <see cref="Control" /> 的东西,
    /// 都只让这一个标签页显示一行说明 —— 一个插件的 bug 不该把宿主进程带走。
    /// </para>
    /// </summary>
    /// <returns>文档内容控件。</returns>
    public Control CreateView()
    {
        try
        {
            object view = Workspace.CreateView();
            if (view is Control control)
            {
                return control;
            }
            Trace.WriteLine($"[PluginWorkspace] '{TypeName}' returned {view?.GetType().FullName ?? "null"}, which is not an Avalonia Control.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginWorkspace] '{TypeName}' threw while creating its view: {ex}");
        }
        return BrokenView();
    }

    /// <summary>
    /// 关闭文档:释放插件那边的连接与资源。幂等 —— 用户点关闭与插件停用可能同时到达。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        Workspace.StatusChanged -= OnWorkspaceStatus;
        try
        {
            await Workspace.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 关闭路径不许抛:标签页已经从界面上消失了,再冒一个异常出去只会变成
            // 一次没人处理的 UnobservedTaskException。
            Trace.WriteLine($"[PluginWorkspace] Disposing '{TypeName}' session failed: {ex.Message}");
        }
    }

    /// <summary>当前状态(供标签页与会话树取用)。</summary>
    public ProtocolSessionState State
    {
        get
        {
            try
            {
                return Workspace.Status.State;
            }
            catch
            {
                return ProtocolSessionState.Faulted;
            }
        }
    }

    private static TextBlock BrokenView() =>
        new()
        {
            Text = Strings.Get("Plugin_ProtocolUnavailable"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(24)
        };
}
