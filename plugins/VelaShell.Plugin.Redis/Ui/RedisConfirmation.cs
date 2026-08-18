namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 面板内的确认闸门。
/// <para>
/// **为什么自己画而不是弹系统对话框**:SDK 的界面能力只给"开一个面板",没有"弹一个模态框";
/// 而真正的理由更实在 —— 确认要贴在**出事的那个面板**上。一个飘在屏幕中央、不说清是哪条连接的
/// 弹窗,在同时开着生产与开发两个标签页时是危险的。
/// </para>
/// <para>
/// 三档护栏的呈现差别全在这里:"危"档给一句后果说明 + 确认按钮;
/// "毁"档额外要求**手打确认串**(键入 <c>prod-cache/db0</c>),与删仓同款。
/// </para>
/// </summary>
public sealed class RedisConfirmation : ObservableObject
{
    private TaskCompletionSource<bool>? _pending;

    /// <summary>构造。</summary>
    public RedisConfirmation()
    {
        ConfirmCommand = new(() =>
        {
            Close(true);
            return Task.CompletedTask;
        }, () => CanConfirm);
        CancelCommand = new(() =>
        {
            Close(false);
            return Task.CompletedTask;
        });
    }

    /// <summary>确认框是否打开。</summary>
    public bool IsOpen
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>标题(一句话说清要做什么)。</summary>
    public string Title
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>后果说明(为什么值得停一下)。</summary>
    public string Message
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>要执行的命令(等宽显示,让用户看到确切的东西)。</summary>
    public string Detail
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>确认按钮的文案。</summary>
    public string ConfirmLabel
    {
        get;
        private set => SetProperty(ref field, value);
    } = "OK";

    /// <summary>取消按钮的文案。</summary>
    public string CancelLabel
    {
        get;
        private set => SetProperty(ref field, value);
    } = "Cancel";

    /// <summary>是否是"毁"档(界面据此把确认按钮染成危险色)。</summary>
    public bool IsDestructive
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>是否要求手打确认串。</summary>
    public bool RequiresTyping
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>要求键入的串(如 <c>prod-cache/db0</c>)。</summary>
    public string ExpectedText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>用户键入的串。</summary>
    public string TypedText
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(CanConfirm));
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>确认按钮是否可用。</summary>
    public bool CanConfirm =>
        !RequiresTyping || string.Equals(TypedText.Trim(), ExpectedText, StringComparison.Ordinal);

    /// <summary>确认。</summary>
    public AsyncCommand ConfirmCommand { get; }

    /// <summary>取消。</summary>
    public AsyncCommand CancelCommand { get; }

    /// <summary>
    /// 问一次并等答案。
    /// <para>
    /// 同时只允许一个确认在飞:第二个请求直接被拒(返回 false)而不是排队 ——
    /// 排队会让用户在第一个框上点"确认"之后,莫名其妙地被问第二个他早已忘了的问题。
    /// </para>
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="message">后果说明。</param>
    /// <param name="detail">要执行的命令。</param>
    /// <param name="confirmLabel">确认按钮文案。</param>
    /// <param name="cancelLabel">取消按钮文案。</param>
    /// <param name="destructive">是否"毁"档。</param>
    /// <param name="expectedText">要求键入的串;为空表示不要求。</param>
    /// <returns>用户是否确认。</returns>
    public Task<bool> AskAsync(
        string title,
        string message,
        string detail,
        string confirmLabel,
        string cancelLabel,
        bool destructive = false,
        string? expectedText = null)
    {
        if (_pending is not null)
        {
            return Task.FromResult(false);
        }
        Title = title;
        Message = message;
        Detail = detail;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
        IsDestructive = destructive;
        ExpectedText = expectedText ?? string.Empty;
        RequiresTyping = ExpectedText.Length > 0;
        TypedText = string.Empty;
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        RaisePropertyChanged(nameof(CanConfirm));
        ConfirmCommand.RaiseCanExecuteChanged();
        return _pending.Task;
    }

    /// <summary>关掉确认框(面板释放时用,当作取消)。</summary>
    public void Dismiss() => Close(false);

    private void Close(bool answer)
    {
        TaskCompletionSource<bool>? pending = _pending;
        _pending = null;
        IsOpen = false;
        pending?.TrySetResult(answer);
    }
}
