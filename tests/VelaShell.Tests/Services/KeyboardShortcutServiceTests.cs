using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public class KeyboardShortcutServiceTests
{
    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlShiftC_InTerminal_ReturnsCopy()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.C,
            ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Copy, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlShiftV_InTerminal_ReturnsPaste()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.V,
            ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Paste, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlT_InGlobal_ReturnsNewTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.T, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.NewTab, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlW_InGlobal_ReturnsCloseTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.W, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.CloseTab, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlC_InTerminal_ReturnsSendInterrupt()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.SendInterrupt, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdC_InTerminal_ReturnsCopy()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.C, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Copy, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdV_InTerminal_ReturnsPaste()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.V, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.Paste, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdT_InGlobal_ReturnsNewTab()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.T, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.NewTab, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CtrlC_InTerminal_ReturnsSendInterrupt()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.SendInterrupt, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlTab_InGlobal_ReturnsNextTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.Tab, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.NextTab, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlShiftTab_InGlobal_ReturnsPreviousTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.Tab,
            ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.PreviousTab, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_CtrlComma_InGlobal_ReturnsOpenSettings()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Ctrl, KeyCode.Comma, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.OpenSettings, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_MacOS_CmdComma_InGlobal_ReturnsOpenSettings()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        ShortcutAction action = service.Resolve(KeyModifiers.Meta, KeyCode.Comma, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.OpenSettings, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_UnmappedKey_ReturnsNone()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction action = service.Resolve(KeyModifiers.Alt, KeyCode.T, ShortcutContext.Global);

        Assert.AreEqual(ShortcutAction.None, action);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void IsMacOS_ReturnsConstructorValue()
    {
        var macService = new KeyboardShortcutService(isMacOS: true);
        var winService = new KeyboardShortcutService(isMacOS: false);

        Assert.IsTrue(macService.IsMacOS);
        Assert.IsFalse(winService.IsMacOS);
    }

    [TestMethod]
    [TestCategory("Keyboard")]
    public void Resolve_GlobalShortcuts_AlsoWorkInTerminalContext()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        ShortcutAction newTab = service.Resolve(KeyModifiers.Ctrl, KeyCode.T, ShortcutContext.Terminal);
        ShortcutAction closeTab = service.Resolve(KeyModifiers.Ctrl, KeyCode.W, ShortcutContext.Terminal);

        Assert.AreEqual(ShortcutAction.NewTab, newTab);
        Assert.AreEqual(ShortcutAction.CloseTab, closeTab);
    }
}
