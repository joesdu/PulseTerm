using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VelaShell.Views;

/// <summary>
/// 插件面板的独立窗口。窗体规格与资源监视/任务管理器一致
/// (透明窗口 + 自绘圆角卡片 + 自绘缩放抓取区),内容为插件自建的 Avalonia 控件。
/// </summary>
public partial class PluginPanelWindow : Window
{
    /// <summary>初始化窗口(macOS 退回不透明矩形,与其它自绘窗体同一结论)。</summary>
    public PluginPanelWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            if (this.TryFindResource("VelaBgPage", out object? page) && page is IBrush brush)
            {
                Background = brush;
            }
            RootCard.BoxShadow = default;
            ApplyCardShape(rounded: false);
        }
    }

    /// <summary>设置面板内容(插件自建的控件)。</summary>
    public void SetContent(Control content) => PanelContent.Content = content;

    /// <summary>设置标题栏文本:面板标题 + 所属插件 id。</summary>
    public void SetTitle(string title, string pluginId)
    {
        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = pluginId;
    }

    /// <summary>
    /// 把插件声明的标题栏动作按钮插到最小化键左侧(按给出的顺序)。
    /// 与三连按钮同一套 caption 样式,只是图标换成插件给的路径、悬停带提示。
    /// </summary>
    public void SetTitleActions(IReadOnlyList<PluginSdk.Ui.PanelTitleAction> actions)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            PluginSdk.Ui.PanelTitleAction action = actions[i];
            var icon = new Controls.Controls.LucideIcon { Width = 12, Height = 12, Data = StreamGeometry.Parse(action.IconPathData) };
            icon.Bind(Controls.Controls.LucideIcon.ForegroundProperty, this.GetResourceObservable("VelaTextSecondary"));
            var button = new Button { Classes = { "caption" }, Content = icon };
            ToolTip.SetTip(button, action.ToolTip);
            button.Click += (_, _) => action.OnClick();
            CaptionButtons.Children.Insert(i, button);
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }

    /// <summary>缩放抓取区。只认左键:系统 sizing 模态循环只在左键弹起时退出(#116)。</summary>
    private void ResizeEdge_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (WindowState == WindowState.Normal
            && sender is Border { Tag: string tag }
            && Enum.TryParse(tag, out WindowEdge edge))
        {
            BeginResizeDrag(edge, e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty)
        {
            return;
        }
        bool normal = WindowState == WindowState.Normal;
        ResizeGrips.IsVisible = normal;
        if (!OperatingSystem.IsMacOS())
        {
            ApplyCardShape(normal);
        }
    }

    /// <summary>卡片外圆角 8 与 1px 边框;子元素在边框内侧,内圆弧半径是 8−1=7。</summary>
    private const double InnerRadius = 7;

    private void ApplyCardShape(bool rounded)
    {
        RootCard.Margin = rounded ? new Thickness(16) : default;
        RootCard.BorderThickness = rounded ? new Thickness(1) : default;
        RootCard.CornerRadius = rounded ? new CornerRadius(8) : default;
        ResizeGripLayout.Apply(ResizeGrips, rounded);
        TitleBarStrip.CornerRadius = rounded ? new CornerRadius(InnerRadius, InnerRadius, 0, 0) : default;
    }
}
