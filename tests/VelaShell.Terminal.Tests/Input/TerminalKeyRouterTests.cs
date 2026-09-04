using System.Text;
using Avalonia.Input;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Input;

namespace VelaShell.Terminal.Tests.Input;

/// <summary>
/// <see cref="TerminalKeyRouter" /> 的决策树回归:优先级、修饰键改写与编码分派
/// 与 VelaTerminalControl.OnKeyDown 的历史行为逐条对应。
/// </summary>
[TestClass]
[TestCategory("KeyRouter")]
public class TerminalKeyRouterTests
{
    private readonly TerminalModes _modes = new();

    private TerminalKeyAction Classify(
        Key key,
        KeyModifiers modifiers,
        bool canScrollHistory = false,
        bool ctrlCCopiesSelection = false) =>
        TerminalKeyRouter.Classify(key, modifiers, _modes, TerminalType.XtermColor256, canScrollHistory, ctrlCCopiesSelection);

    [TestMethod]
    public void ImeProcessed_IsPassedThrough_NeverEncoded()
    {
        // IME 组字消耗的按键若被编码,散逸的 ESC/方向键会打死全屏程序(#14a)。
        TerminalKeyAction action = Classify(Key.ImeProcessed, KeyModifiers.None);
        Assert.AreEqual(TerminalKeyActionKind.ImePassthrough, action.Kind);
    }

    [TestMethod]
    public void CtrlShiftC_CopiesSelection() => Assert.AreEqual(TerminalKeyActionKind.CopySelection, Classify(Key.C, KeyModifiers.Control | KeyModifiers.Shift).Kind);

    [TestMethod]
    public void CtrlShiftV_Pastes() => Assert.AreEqual(TerminalKeyActionKind.PasteClipboard, Classify(Key.V, KeyModifiers.Control | KeyModifiers.Shift).Kind);

    [TestMethod]
    public void ShiftInsert_Pastes_InsteadOfEncodingCsi2Tilde() =>
        // 必须在编码器之前拦截,否则会被编成 CSI 2~ 发往主机。
        Assert.AreEqual(TerminalKeyActionKind.PasteClipboard, Classify(Key.Insert, KeyModifiers.Shift).Kind);

    [TestMethod]
    public void PageUp_OnMainScreenWithScrollback_ScrollsHistoryUp()
    {
        TerminalKeyAction action = Classify(Key.PageUp, KeyModifiers.None, canScrollHistory: true);
        Assert.AreEqual(TerminalKeyActionKind.ScrollHistory, action.Kind);
        Assert.AreEqual(1, action.ScrollPageDirection);
    }

    [TestMethod]
    public void PageDown_WithShift_ScrollsEvenWithoutScrollback()
    {
        TerminalKeyAction action = Classify(Key.PageDown, KeyModifiers.Shift, canScrollHistory: false);
        Assert.AreEqual(TerminalKeyActionKind.ScrollHistory, action.Kind);
        Assert.AreEqual(-1, action.ScrollPageDirection);
    }

    [TestMethod]
    public void PageUp_OnAltScreen_IsEncodedForTheFullScreenApp()
    {
        // 备用屏上的全屏程序(less/htop)要自己收到 CSI 5~。
        TerminalKeyAction action = Classify(Key.PageUp, KeyModifiers.None, canScrollHistory: false);
        Assert.AreEqual(TerminalKeyActionKind.SendBytes, action.Kind);
        Assert.AreSequenceEqual("\x1b[5~"u8.ToArray(), action.Bytes);
    }

    [TestMethod]
    public void ShiftHome_StripsShift_SoReadlineSeesPlainHome()
    {
        TerminalKeyAction plain = Classify(Key.Home, KeyModifiers.None);
        TerminalKeyAction shifted = Classify(Key.Home, KeyModifiers.Shift);
        Assert.AreEqual(TerminalKeyActionKind.SendBytes, shifted.Kind);
        Assert.AreSequenceEqual(plain.Bytes, shifted.Bytes);
    }

    [TestMethod]
    public void CtrlC_WithSelectionAndSettingOn_Copies_InsteadOfInterrupting()
    {
        Assert.AreEqual(
            TerminalKeyActionKind.CopySelection,
            Classify(Key.C, KeyModifiers.Control, ctrlCCopiesSelection: true).Kind);
    }

    [TestMethod]
    public void CtrlC_WithoutSelection_SendsInterrupt()
    {
        TerminalKeyAction action = Classify(Key.C, KeyModifiers.Control, ctrlCCopiesSelection: false);
        Assert.AreEqual(TerminalKeyActionKind.SendBytes, action.Kind);
        Assert.AreSequenceEqual(new byte[] { 0x03 }, action.Bytes);
    }

    [TestMethod]
    public void PlainCharacterKey_ProducesNoAction_TextArrivesViaTextInput() =>
        // 无修饰的字符键交给 TextInput 管线(IME/大小写/布局都在那边处理)。
        Assert.AreEqual(TerminalKeyActionKind.None, Classify(Key.A, KeyModifiers.None).Kind);

    [TestMethod]
    public void Backspace_EncodesDelete()
    {
        TerminalKeyAction action = Classify(Key.Back, KeyModifiers.None);
        Assert.AreEqual(TerminalKeyActionKind.SendBytes, action.Kind);
        Assert.AreSequenceEqual(new byte[] { 0x7F }, action.Bytes);
    }

    [TestMethod]
    public void CtrlBackspace_SendsEscDelete_SoReadlineKillsAWord()
    {
        // #127:ESC+DEL 命中 readline 的 backward-kill-word(zsh 同名绑定);
        // 老行为 0x08(^H)只等价于退一格,与普通 Backspace 无异。
        TerminalKeyAction action = Classify(Key.Back, KeyModifiers.Control);
        Assert.AreEqual(TerminalKeyActionKind.SendBytes, action.Kind);
        Assert.AreSequenceEqual(new byte[] { 0x1B, 0x7F }, action.Bytes);
    }

    [TestMethod]
    public void AltBackspace_SendsTheSameKillWordSequence()
    {
        // 两种习惯(Tabby 的 Ctrl+Backspace 与 xterm 的 Alt+Backspace)必须等价。
        Assert.AreSequenceEqual(
            Classify(Key.Back, KeyModifiers.Control).Bytes,
            Classify(Key.Back, KeyModifiers.Alt).Bytes);
    }

    [TestMethod]
    public void Enter_EncodesCarriageReturn()
    {
        TerminalKeyAction action = Classify(Key.Enter, KeyModifiers.None);
        Assert.AreEqual(TerminalKeyActionKind.SendBytes, action.Kind);
        Assert.AreSequenceEqual(Encoding.ASCII.GetBytes("\r"), action.Bytes);
    }
}
