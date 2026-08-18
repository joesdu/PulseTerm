using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// Redis 工作台面板的视图。
/// <para>
/// 只做两件视图该做的事:装配,以及把**键盘手势**接到视图模型上 ——
/// 手势只有在控件层才拿得到,而它们是这个面板"键盘优先"的全部依据。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceView : UserControl
{
    private readonly RedisWorkspaceViewModel _viewModel;

    /// <summary>用给定的视图模型初始化。</summary>
    /// <param name="viewModel">视图模型。</param>
    public RedisWorkspaceView(RedisWorkspaceViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        KeyList.Tapped += OnKeyListTapped;
        // 列表关了自动滚动(理由见 AXAML),所以"跳到收藏的键"要视图自己滚过去。
        viewModel.KeyRevealed += (_, row) => Dispatcher.UIThread.Post(() => KeyList.ScrollIntoView(row));
    }

    /// <summary>
    /// 点分组行 = 展开/折叠。
    /// <para>
    /// 走 <c>Tapped</c> 而不是 <c>SelectionChanged</c>:选中同一行第二次不会再发选中变化,
    /// 于是"点一下展开、再点一下收起"里的第二下会石沉大海 —— 那种失灵最难往事件上想。
    /// </para>
    /// </summary>
    private void OnKeyListTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not RedisKeyRow { IsGroup: true } row)
        {
            return;
        }
        _viewModel.ToggleGroup(row);
        // 选中态弹回视图模型认可的那一行。视图模型已经否决了分组行的选中(它不是键),
        // 但 ListBox 这时已经把高亮画上去了 —— 光在 setter 里发一次通知弹不回来,
        // 那是在绑定自己的赋值过程中,控件不理会。排到下一轮再改才生效。
        //
        // 不弹回去的后果是详情区显示着 A、高亮却停在一条分组行上,两边各说各话。
        Dispatcher.UIThread.Post(() => KeyList.SelectedItem = _viewModel.SelectedRow);
    }

    /// <summary>
    /// 面板级快捷键。
    /// <list type="bullet">
    ///   <item><c>/</c> 或 <c>Ctrl+F</c> —— 聚焦过滤框(在输入框里时不抢 <c>/</c>)。</item>
    ///   <item><c>Ctrl+R</c> —— 重扫当前前缀。</item>
    ///   <item><c>Ctrl+`</c> —— 展开/收起底部抽屉。</item>
    ///   <item>控制台输入框里:<c>↑↓</c> 调历史、<c>Enter</c> / <c>Ctrl+Enter</c> 执行。</item>
    ///   <item><c>Esc</c> —— 关掉确认框 / 停止扫描。</item>
    /// </list>
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        // 确认框开着时,键盘只服务于它 —— 别让 Esc 在关框的同时又去停扫描。
        if (_viewModel.Confirmation.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.Confirmation.CancelCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (ReferenceEquals(e.Source, ConsoleInput))
        {
            switch (e.Key)
            {
                case Key.Up:
                    _viewModel.Console.HistoryBack();
                    ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                    e.Handled = true;
                    return;
                case Key.Down:
                    _viewModel.Console.HistoryForward();
                    ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _viewModel.Console.RunCommand.Execute(null);
                    e.Handled = true;
                    return;
                default:
                    return;
            }
        }

        // 在任意输入框里就别抢字符键了 —— 用户正在打字。
        bool typing = e.Source is TextBox;
        switch (e.Key)
        {
            case Key.OemQuestion or Key.Divide when !typing:
            case Key.F when control:
                FilterBox.Focus();
                FilterBox.SelectAll();
                e.Handled = true;
                return;
            case Key.R when control:
                _viewModel.RescanCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.OemTilde when control:
                _viewModel.ToggleDrawerCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Escape when !typing:
                _viewModel.StopScanCommand.Execute(null);
                e.Handled = true;
                return;
            default:
                return;
        }
    }
}
