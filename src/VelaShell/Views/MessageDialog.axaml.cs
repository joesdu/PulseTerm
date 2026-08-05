using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using VelaShell.Core.Resources;

namespace VelaShell.Views;

/// <summary>标题栏图标的语义分组:着色跟随主题令牌(info/成功/警告/错误/询问)。</summary>
public enum MessageDialogKind
{
    /// <summary>普通信息提示。</summary>
    Info,

    /// <summary>操作成功提示。</summary>
    Success,

    /// <summary>警告提示。</summary>
    Warning,

    /// <summary>错误提示。</summary>
    Error,

    /// <summary>需要用户确认的询问提示。</summary>
    Question
}

/// <summary>
/// 主题化的通用消息弹窗,替代原生 <see cref="Window" /> 消息框(原生框带系统标题栏,
/// 与设计的无边框圆角弹窗风格不符)。支持纯消息、确认、单行文本输入与自定义内容四种形态,
/// 通过静态方法调用。
/// </summary>
public partial class MessageDialog : Window
{
    /// <summary>供 XAML 加载器调用的无参构造:初始化可视化组件。</summary>
    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>纯消息:仅一个"确定"按钮。</summary>
    public static Task ShowMessageAsync(Window owner,
        string title,
        string message,
        MessageDialogKind kind = MessageDialogKind.Info)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, kind, Strings.OK, null, false);
        return dialog.ShowDialog(owner);
    }

    /// <summary>确认框:返回 true 表示用户点击了确认。danger 时确认按钮渲染为红色。</summary>
    public static Task<bool> ConfirmAsync(Window owner,
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        MessageDialogKind kind = MessageDialogKind.Question,
        bool danger = false)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, kind, confirmText ?? Strings.OK, cancelText ?? Strings.Cancel, danger);
        return dialog.ShowDialog<bool>(owner);
    }

    /// <summary>单行文本输入(新建/重命名/移动等):返回输入内容,取消返回 null。</summary>
    public static async Task<string?> PromptAsync(Window owner,
        string title,
        string initialValue,
        string? message = null)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, MessageDialogKind.Question, Strings.OK, Strings.Cancel, false);
        dialog.InputBox.IsVisible = true;
        dialog.InputBox.Text = initialValue;
        dialog.Opened += (_, _) =>
        {
            dialog.InputBox.SelectAll();
            dialog.InputBox.Focus();
        };
        bool confirmed = await dialog.ShowDialog<bool>(owner);
        return confirmed ? dialog.InputBox.Text : null;
    }

    /// <summary>
    /// 多选按钮弹窗:按 <paramref name="choices" /> 顺序在按钮栏生成按钮,返回被点按钮的下标;
    /// 用户按 Esc 或点右上角关闭时返回 <paramref name="cancelResult" />。<paramref name="primaryIndex" />
    /// 指定的按钮以强调色渲染并作为默认(Enter)动作。用于“文件已存在:覆盖 / 全部覆盖 /
    /// 跳过 / 全部跳过”等超过两个选项的场景。
    /// </summary>
    public static Task<int> ChooseAsync(Window owner,
        string title,
        string message,
        IReadOnlyList<string> choices,
        int primaryIndex = 0,
        int cancelResult = -1,
        MessageDialogKind kind = MessageDialogKind.Question)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, message, kind, Strings.OK, null, false);
        dialog._choiceMode = true;
        dialog._choiceCancelResult = cancelResult;
        // 复用同一按钮栏:隐去默认的确认/取消,按选项动态铺按钮(顺序即下标)。
        dialog.ConfirmButton.IsVisible = false;
        dialog.CancelButton.IsVisible = false;
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = choices[i],
                Classes = { index == primaryIndex ? "dlg-primary" : "dlg-outline" },
                IsDefault = index == primaryIndex
            };
            button.Click += (_, _) => dialog.CloseDeferred(index);
            dialog.ButtonBar.Children.Add(button);
        }
        return dialog.ShowDialog<int>(owner);
    }

    /// <summary>自定义内容(属性表、权限矩阵等):返回 true 表示用户点击了确认。</summary>
    public static Task<bool> ShowCustomAsync(Window owner,
        string title,
        Control content,
        string? confirmText = null,
        string? cancelText = null,
        bool showCancel = true,
        MessageDialogKind kind = MessageDialogKind.Info)
    {
        var dialog = new MessageDialog();
        dialog.Configure(title, null, kind, confirmText ?? Strings.OK,
            showCancel ? cancelText ?? Strings.Cancel : null, false);
        dialog.BodyHost.IsVisible = true;
        dialog.BodyHost.Content = content;
        return dialog.ShowDialog<bool>(owner);
    }

    private void Configure(string title,
        string? message,
        MessageDialogKind kind,
        string confirmText,
        string? cancelText,
        bool danger)
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.IsVisible = !string.IsNullOrEmpty(message);

        // 画刷经样式类 + DynamicResource 解析(代码里 FindResource 拿不到主题字典画刷)。
        (string iconKey, string kindClass) = kind switch
        {
            MessageDialogKind.Success => ("Icon.circle-check", "success"),
            MessageDialogKind.Warning => ("Icon.triangle-alert", "warning"),
            MessageDialogKind.Error => ("Icon.circle-alert", "error"),
            MessageDialogKind.Question => ("Icon.circle-help", "question"),
            _ => ("Icon.info", "info")
        };
        KindIcon.Data = this.FindResource(iconKey) as Geometry;
        KindIcon.Classes.Add(kindClass);
        ConfirmButton.Content = confirmText;
        if (danger)
        {
            ConfirmButton.Classes.Remove("dlg-primary");
            ConfirmButton.Classes.Add("dlg-danger");
        }
        if (cancelText is null)
        {
            CancelButton.IsVisible = false;
        }
        else
        {
            CancelButton.Content = cancelText;
        }
    }

    // 多选(ChooseAsync)模式:关闭结果为 int 下标而非 bool;Esc / 右上角关闭返回取消下标。
    private bool _choiceMode;
    private int _choiceCancelResult;

    private void Confirm_Click(object? sender, RoutedEventArgs e) => CloseDeferred(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => CloseDeferred(false);

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseDeferred(CancelResult());

    /// <summary>取消/关闭时的返回值:多选模式为取消下标(int),否则为 false(bool)。</summary>
    private object? CancelResult() => _choiceMode ? _choiceCancelResult : false;

    /// <summary>
    /// 输入事件处理器内不得同步 Close:窗口销毁后,本轮输入事件的剩余路由
    /// (PointerReleased 等)会打到已无 PlatformImpl 的窗口上,Avalonia 刷
    /// "PlatformImpl is null, couldn't handle input" 警告。推迟到当前事件出栈后再关
    /// (统一走 <see cref="WindowCloseExtensions.PostClose(Window, object?)" />)。
    /// </summary>
    private void CloseDeferred(object? result) => this.PostClose(result);

    /// <summary>拦截 Esc 键关闭弹窗:纯消息框(无取消按钮)时也能用 Esc 取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 无取消按钮的纯消息框也要能用 Esc 关闭(IsCancel 只在按钮可见时生效)。
        if (e.Key == Key.Escape)
        {
            CloseDeferred(CancelResult());
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
