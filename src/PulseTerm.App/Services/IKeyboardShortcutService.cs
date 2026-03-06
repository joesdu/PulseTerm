using System;

namespace PulseTerm.App.Services;

public enum ShortcutAction
{
    None,
    Copy,
    Paste,
    NewTab,
    CloseTab,
    NextTab,
    PreviousTab,
    OpenSettings,
    SendInterrupt
}

public enum ShortcutContext
{
    Global,
    Terminal
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Meta = 8
}

public enum KeyCode
{
    None,
    C,
    V,
    T,
    W,
    Tab,
    Comma
}

public interface IKeyboardShortcutService
{
    ShortcutAction Resolve(KeyModifiers modifiers, KeyCode key, ShortcutContext context);

    bool IsMacOS { get; }
}
