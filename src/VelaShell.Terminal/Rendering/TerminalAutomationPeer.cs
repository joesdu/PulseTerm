using Avalonia.Automation.Peers;

namespace VelaShell.Terminal.Rendering;

/// <summary>
/// 终端控件的无障碍表述。
/// </summary>
/// <remarks>
/// <para>
/// 终端是完全自绘的:屏幕上那一屏字对读屏器来说什么都不是,连"这里有个控件"都算不上。
/// 没有这个 peer 时,读屏器在终端上只会报一个匿名的 <c>Control</c>。
/// </para>
/// <para>
/// 这里给的是**最小可用**的一份:控件类型报 <c>Document</c>(一片可读的文本区域,
/// 与终端的实际语义最接近),名字取宿主给的标签标题(<c>用户名@主机</c>),
/// 值取光标所在行的文本 —— 于是读屏器至少能说出"你在哪台机器上、光标那一行写着什么"。
/// </para>
/// <para>
/// 逐字符导航、选区朗读那一套要实现 <c>ITextProvider</c>,不在这一版范围内;
/// 但没有这个 peer 的话,连"是什么"都无从谈起。
/// </para>
/// </remarks>
public sealed class TerminalAutomationPeer(VelaTerminalControl owner) : ControlAutomationPeer(owner)
{
    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    /// <inheritdoc />
    protected override string GetClassNameCore() => "Terminal";

    /// <inheritdoc />
    protected override string? GetNameCore() =>
        string.IsNullOrWhiteSpace(owner.AccessibleName) ? base.GetNameCore() : owner.AccessibleName;

    /// <summary>光标所在行的文本 —— 读屏器据此播报"当前在哪一行"。</summary>
    protected override bool IsContentElementCore() => true;

    /// <inheritdoc />
    protected override bool IsControlElementCore() => true;

    /// <inheritdoc />
    protected override bool IsKeyboardFocusableCore() => true;
}
