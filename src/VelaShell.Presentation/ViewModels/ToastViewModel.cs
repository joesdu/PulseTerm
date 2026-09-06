using ReactiveUI;

namespace VelaShell.Presentation.ViewModels;

/// <summary>提示的分级;决定配色、图标与是否自动消失。</summary>
public enum ToastSeverity
{
    /// <summary>一次成功的操作、一条无关痛痒的告知。看一眼就够,自动消失。</summary>
    Info,

    /// <summary>需要留意但不必立刻处理(重连倒计时、降级运行)。停留久一些。</summary>
    Warning,

    /// <summary>出错了。<b>不自动消失</b> —— 用户没看见的错误等于没报。</summary>
    Error
}

/// <summary>
/// 一条浮层提示。
/// </summary>
/// <remarks>
/// <para>
/// 在此之前,断线通知、自动重连倒计时、安全告警、连接失败、导出成功全都往
/// <c>StatusBar.Status</c> 那一个字符串里写,后写覆盖先写 —— 三条消息挤在一秒内到达时,
/// 用户只会看到最后一条,而那条未必是最要紧的。
/// </para>
/// <para>
/// 分级不只是配色:<see cref="ToastSeverity.Error" /> 不自动消失。一条转瞬即逝的错误
/// 与没报错没有区别,而用户往往正低头看别处。
/// </para>
/// </remarks>
public sealed class ToastViewModel : ReactiveObject
{
    /// <summary>创建一条提示。</summary>
    /// <param name="severity">分级。</param>
    /// <param name="message">正文。</param>
    /// <param name="actionLabel">操作按钮文案;null = 没有操作按钮。</param>
    /// <param name="action">点击操作按钮时执行;null = 没有操作按钮。</param>
    public ToastViewModel(
        ToastSeverity severity,
        string message,
        string? actionLabel = null,
        Action? action = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Severity = severity;
        Message = message;
        ActionLabel = actionLabel;
        Action = actionLabel is { Length: > 0 } ? action : null;
    }

    /// <summary>分级。</summary>
    public ToastSeverity Severity { get; }

    /// <summary>正文。会随倒计时一类的消息刷新。</summary>
    public string Message
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>操作按钮文案;null = 不显示按钮。</summary>
    public string? ActionLabel { get; }

    /// <summary>点击操作按钮时执行。</summary>
    public Action? Action { get; }

    /// <summary>是否有操作按钮(供绑定,免得在 axaml 里挂转换器)。</summary>
    public bool HasAction => Action is not null;

    /// <summary>是不是错误级(供绑定配色/图标)。</summary>
    public bool IsError => Severity == ToastSeverity.Error;

    /// <summary>是不是警告级。</summary>
    public bool IsWarning => Severity == ToastSeverity.Warning;

    /// <summary>是不是信息级。</summary>
    public bool IsInfo => Severity == ToastSeverity.Info;

    /// <summary>
    /// 同类消息的合并键;非空时,新的同键消息就地更新这一条而不是再堆一条。
    /// </summary>
    /// <remarks>
    /// 自动重连倒计时是逐秒刷新的:不合并的话十秒之内会堆出十条几乎一样的提示,
    /// 把别的消息全挤出屏幕。
    /// </remarks>
    public string? MergeKey { get; init; }
}
