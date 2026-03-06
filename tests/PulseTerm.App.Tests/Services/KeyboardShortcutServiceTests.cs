using FluentAssertions;
using PulseTerm.App.Services;

namespace PulseTerm.App.Tests.Services;

public class KeyboardShortcutServiceTests
{
    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlShiftC_InTerminal_ReturnsCopy()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.C,
            ShortcutContext.Terminal);

        action.Should().Be(ShortcutAction.Copy);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlShiftV_InTerminal_ReturnsPaste()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.V,
            ShortcutContext.Terminal);

        action.Should().Be(ShortcutAction.Paste);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlT_InGlobal_ReturnsNewTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(KeyModifiers.Ctrl, KeyCode.T, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.NewTab);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlW_InGlobal_ReturnsCloseTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(KeyModifiers.Ctrl, KeyCode.W, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.CloseTab);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlC_InTerminal_ReturnsSendInterrupt()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal);

        action.Should().Be(ShortcutAction.SendInterrupt);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_MacOS_CmdC_InTerminal_ReturnsCopy()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        var action = service.Resolve(KeyModifiers.Meta, KeyCode.C, ShortcutContext.Terminal);

        action.Should().Be(ShortcutAction.Copy);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_MacOS_CmdV_InTerminal_ReturnsPaste()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        var action = service.Resolve(KeyModifiers.Meta, KeyCode.V, ShortcutContext.Terminal);

        action.Should().Be(ShortcutAction.Paste);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_MacOS_CmdT_InGlobal_ReturnsNewTab()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        var action = service.Resolve(KeyModifiers.Meta, KeyCode.T, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.NewTab);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_MacOS_CtrlC_InTerminal_ReturnsSendInterrupt()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        var action = service.Resolve(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal);

        action.Should().Be(ShortcutAction.SendInterrupt);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlTab_InGlobal_ReturnsNextTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(KeyModifiers.Ctrl, KeyCode.Tab, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.NextTab);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlShiftTab_InGlobal_ReturnsPreviousTab()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(
            KeyModifiers.Ctrl | KeyModifiers.Shift,
            KeyCode.Tab,
            ShortcutContext.Global);

        action.Should().Be(ShortcutAction.PreviousTab);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_CtrlComma_InGlobal_ReturnsOpenSettings()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(KeyModifiers.Ctrl, KeyCode.Comma, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.OpenSettings);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_MacOS_CmdComma_InGlobal_ReturnsOpenSettings()
    {
        var service = new KeyboardShortcutService(isMacOS: true);

        var action = service.Resolve(KeyModifiers.Meta, KeyCode.Comma, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.OpenSettings);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_UnmappedKey_ReturnsNone()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var action = service.Resolve(KeyModifiers.Alt, KeyCode.T, ShortcutContext.Global);

        action.Should().Be(ShortcutAction.None);
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void IsMacOS_ReturnsConstructorValue()
    {
        var macService = new KeyboardShortcutService(isMacOS: true);
        var winService = new KeyboardShortcutService(isMacOS: false);

        macService.IsMacOS.Should().BeTrue();
        winService.IsMacOS.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Keyboard")]
    public void Resolve_GlobalShortcuts_AlsoWorkInTerminalContext()
    {
        var service = new KeyboardShortcutService(isMacOS: false);

        var newTab = service.Resolve(KeyModifiers.Ctrl, KeyCode.T, ShortcutContext.Terminal);
        var closeTab = service.Resolve(KeyModifiers.Ctrl, KeyCode.W, ShortcutContext.Terminal);

        newTab.Should().Be(ShortcutAction.NewTab);
        closeTab.Should().Be(ShortcutAction.CloseTab);
    }
}
