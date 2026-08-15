namespace VelaShell.PluginSdk.Ui;

/// <summary>面板的呈现方式,由插件在打开时选择。</summary>
public enum PanelDisplayMode
{
    /// <summary>
    /// 停靠文档:出现在主窗口标签区,用户可拖拽到任意分栏位置。
    /// 仅进程内插件可用;隔离进程插件请求该模式时自动回退为 <see cref="Window" />
    /// (原生控件无法跨进程嵌入,回退会记一条警告日志)。
    /// </summary>
    Document,

    /// <summary>独立窗口:进程内为宿主同款自绘卡片窗口;隔离进程为插件进程自己的窗口。</summary>
    Window
}

/// <summary>
/// 停靠文档的初始落位。等价于"打开后立刻拖到某一侧" —— 用的就是拖放停靠那条路径,
/// 因此落位后的一切(拖回去、拆分、关闭)与用户手动拖出来的分栏完全一致。
/// 仅 <see cref="PanelDisplayMode.Document" /> 有意义;窗口模式忽略。
/// </summary>
public enum PanelPlacement
{
    /// <summary>并入当前标签组,成为一个新标签(默认)。</summary>
    Tabs,

    /// <summary>在标签区最右侧拆出一栏(VSCode 里 Copilot 聊天面板的位置)。</summary>
    Right,

    /// <summary>在标签区最左侧拆出一栏。</summary>
    Left,

    /// <summary>在标签区底部拆出一栏。</summary>
    Bottom,

    /// <summary>在标签区顶部拆出一栏。</summary>
    Top
}

/// <summary>打开面板的选项。</summary>
public sealed record PanelOptions
{
    /// <summary>面板标题(标签页文字 / 窗口标题)。</summary>
    public required string Title { get; init; }

    /// <summary>呈现方式,默认停靠文档。</summary>
    public PanelDisplayMode DisplayMode { get; init; } = PanelDisplayMode.Document;

    /// <summary>停靠文档的初始落位,默认并入当前标签组;窗口模式忽略。</summary>
    public PanelPlacement Placement { get; init; } = PanelPlacement.Tabs;

    /// <summary>
    /// 侧边落位时这一栏初始占标签区的比例(0.15–0.85,超界自动夹取),默认 0.3。
    /// <see cref="PanelPlacement.Tabs" /> 与窗口模式忽略。
    /// </summary>
    /// <remarks>
    /// 只是"打开时多宽";之后用户拖分割条随时可改,宿主也不会把这个值写回去。
    /// 插件若想记住用户偏好,自己存一份再在下次打开时传进来。
    /// </remarks>
    public double PlacementRatio { get; init; } = 0.3;

    /// <summary>窗口模式的初始宽度(逻辑像素),文档模式忽略。</summary>
    public double WindowWidth { get; init; } = 520;

    /// <summary>窗口模式的初始高度(逻辑像素),文档模式忽略。</summary>
    public double WindowHeight { get; init; } = 420;
}

/// <summary>
/// 一个已打开的插件面板句柄:纯生命周期。内容是插件自己的 Avalonia 控件,
/// 事件与状态更新都由插件直接操作控件完成,不经过宿主。
/// </summary>
public interface IPluginPanel : IAsyncDisposable
{
    /// <summary>面板的不透明 id。</summary>
    string PanelId { get; }

    /// <summary>面板是否仍打开(用户关闭或 <see cref="CloseAsync" /> 后为 false)。</summary>
    bool IsOpen { get; }

    /// <summary>面板已关闭(用户关闭、插件关闭或插件停用),只触发一次。</summary>
    event Action? Closed;

    /// <summary>程序性关闭面板(幂等,任意线程可调)。</summary>
    Task CloseAsync();
}

/// <summary>
/// 界面能力:插件用**完整的 Avalonia** 自行设计界面(AXAML/代码任选,可自带样式、
/// 国际化与第三方组件包)。约束只有一条:Avalonia 相关包必须
/// <c>ExcludeAssets="runtime"</c> 且版本与宿主一致 —— 运行时由装载方共享同一套
/// Avalonia 程序集(进程内 = 宿主的;隔离进程 = PluginHost 自带的),保证类型同一。
/// </summary>
public interface IUiApi
{
    /// <summary>
    /// 打开一个面板。<paramref name="contentFactory" /> 在 **UI 线程**被调用
    /// (进程内 = 宿主 UI 线程;隔离进程 = 插件进程自己的 Avalonia UI 线程),
    /// 必须返回一个 <c>Avalonia.Controls.Control</c>;同一控件实例只能挂进一个面板。
    /// 之后对控件的操作请经 Avalonia 的 <c>Dispatcher.UIThread</c> 封送。
    /// 插件停用时其全部面板由宿主自动关闭。
    /// </summary>
    Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory, CancellationToken cancellationToken = default);
}
