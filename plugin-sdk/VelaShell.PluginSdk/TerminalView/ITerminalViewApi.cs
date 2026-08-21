namespace VelaShell.PluginSdk.TerminalView;

/// <summary>建一个终端视图的选项。</summary>
public sealed record TerminalViewOptions
{
    /// <summary>回滚行数,默认 2000。</summary>
    public int ScrollbackLines { get; init; } = 2000;

    /// <summary>
    /// 跟随宿主的终端外观(字体、字号、行高、配色、光标样式、Gutter 选项)。
    /// <para>
    /// 默认开。关掉只在插件确实需要一个"看起来不一样"的终端时才有意义 ——
    /// 用户在设置里调过一次终端字体,不该因为换了个面板就得再调一次。
    /// </para>
    /// </summary>
    public bool FollowHostAppearance { get; init; } = true;

    /// <summary>
    /// 本地回显。默认关:对面是 PTY 时它自己会回显,再开一次等于每个字符两遍。
    /// 只有对面**不**回显(半双工的串口之类)才打开。
    /// </summary>
    public bool LocalEcho { get; init; }

    /// <summary>终端类型标识(<c>TERM</c>),默认 <c>xterm-256color</c>。</summary>
    public string TerminalType { get; init; } = "xterm-256color";
}

/// <summary>
/// 一个可嵌进插件界面的终端视图:喂进字节 → 渲染;用户按键 → 交出字节。
/// <para>
/// 它把宿主那套已经写好的 VT 解析、屏幕缓冲、回滚、选区、IME、鼠标上报、
/// 键盘编码整体借给插件。插件不必也不应该再写一个 —— ANSI 不是"处理一下转义序列"
/// 那么回事:光标寻址、滚动区、字符集切换、组合字符宽度、括号粘贴、
/// 各种终端的键盘编码差异,每一条都能单独耗掉一周。
/// </para>
/// </summary>
public interface IPluginTerminalView : IDisposable
{
    /// <summary>
    /// 要嵌进插件自己界面里的控件。进程内宿主给的是一个 Avalonia <c>Control</c>,
    /// 插件自行转型 —— 与 <see cref="Ui.IUiApi.ShowPanelAsync" /> 的内容工厂同一个约定,
    /// SDK 这一层刻意不认识任何 UI 框架。
    /// </summary>
    object Control { get; }

    /// <summary>当前列数。</summary>
    int Columns { get; }

    /// <summary>当前行数。</summary>
    int Rows { get; }

    /// <summary>把远端来的原始字节喂进终端。可在任意线程调用,宿主负责切到 UI 线程。</summary>
    void Feed(ReadOnlySpan<byte> data);

    /// <summary>
    /// 往屏幕上写一段文本(横幅、断开提示这类"不是远端说的"话)。
    /// 走的是同一条渲染路径,所以 ANSI 转义序列同样生效。
    /// </summary>
    void Write(string text);

    /// <summary>清屏并清空回滚。</summary>
    void Clear();

    /// <summary>读回缓冲区末尾至多 <paramref name="maxLines" /> 行的纯文本(不含颜色属性)。</summary>
    string GetText(int maxLines = 1000);

    /// <summary>改变终端尺寸。通常不用手动调 —— 控件随布局变化会自己改并抛 <see cref="Resized" />。</summary>
    void Resize(int columns, int rows);

    /// <summary>
    /// 用户键入(含 IME、粘贴、鼠标上报)产生的字节。把它们原样写回远端即可。
    /// 在 UI 线程抛出。
    /// </summary>
    event Action<byte[]>? UserInput;

    /// <summary>
    /// 终端尺寸变了(列, 行)。拿到之后要告诉远端 —— 远端不知道尺寸,
    /// <c>vim</c> 之类的全屏程序会照着旧尺寸画,画出来是错位的。
    /// </summary>
    event Action<int, int>? Resized;

    /// <summary>
    /// 把这个视图接到一条双工字节流上,并一直泵到流结束或令牌取消:
    /// 流里读到的喂给终端,用户键入的写回流里。
    /// <para>
    /// 这一步交给宿主做,是因为它有两处容易做错而且做错了很难发现的地方:
    /// 读循环必须在后台线程、渲染必须切回 UI 线程;以及用户在一次按键里
    /// 产生的字节必须**按序**写回,不能并发写同一条流。
    /// </para>
    /// <para>
    /// 返回的任务在流读到末尾、远端断开或令牌取消时完成。同一个视图同一时刻只能接一条流;
    /// 再接一条会先把前一条断掉。<b>不</b>负责释放传入的流 —— 谁开的谁关。
    /// </para>
    /// </summary>
    Task AttachAsync(Stream stream, CancellationToken cancellationToken = default);
}

/// <summary>
/// 终端视图能力:向插件出借宿主的终端仿真器(VT 解析 + 屏幕缓冲 + 渲染 + 输入编码)。
/// <para>
/// <b>与 <see cref="Terminal.ITerminalApi" /> 的分工。</b> 那一个是对**宿主已有会话**的旁路:
/// 读它的缓冲、搜它的输出、经授权往它里面敲字。这一个是插件**自己的**终端:
/// 插件拿到一个空白控件,自己决定喂什么、把用户输入送到哪儿去。
/// 前者操作别人的终端,后者拥有一个自己的。
/// </para>
/// <para>
/// 仅 <c>inProcess</c> 宿主模式可用:交出去的是一个活的原生控件,跨进程嵌不了。
/// 隔离进程里调用抛 <see cref="NotSupportedException" />;
/// 用 <see cref="IsAvailable" /> 先问一句,好过让用户点到一个会炸的按钮。
/// </para>
/// </summary>
public interface ITerminalViewApi
{
    /// <summary>这个宿主能不能给出终端视图。</summary>
    bool IsAvailable => false;

    /// <summary>
    /// 建一个终端视图。必须在 UI 线程调用(插件的视图构造与
    /// <see cref="Ui.IUiApi.ShowPanelAsync" /> 的内容工厂本来就在 UI 线程上)。
    /// </summary>
    IPluginTerminalView Create(TerminalViewOptions? options = null) =>
        throw new NotSupportedException("This host does not provide terminal views.");
}
