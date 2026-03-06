using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PulseTerm.App.Services;

public class KeyboardShortcutService : IKeyboardShortcutService
{
    private readonly Dictionary<(KeyModifiers, KeyCode, ShortcutContext), ShortcutAction> _mappings;

    public KeyboardShortcutService()
        : this(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
    }

    public KeyboardShortcutService(bool isMacOS)
    {
        IsMacOS = isMacOS;
        _mappings = new Dictionary<(KeyModifiers, KeyCode, ShortcutContext), ShortcutAction>();
        RegisterMappings();
    }

    public bool IsMacOS { get; }

    public ShortcutAction Resolve(KeyModifiers modifiers, KeyCode key, ShortcutContext context)
    {
        if (_mappings.TryGetValue((modifiers, key, context), out var action))
            return action;

        if (context == ShortcutContext.Terminal &&
            _mappings.TryGetValue((modifiers, key, ShortcutContext.Global), out action))
            return action;

        return ShortcutAction.None;
    }

    private void RegisterMappings()
    {
        var primaryModifier = IsMacOS ? KeyModifiers.Meta : KeyModifiers.Ctrl;

        RegisterTerminalMappings(primaryModifier);
        RegisterGlobalMappings(primaryModifier);
    }

    private void RegisterTerminalMappings(KeyModifiers primaryModifier)
    {
        // Ctrl+C always sends interrupt in terminal (both platforms)
        Map(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.SendInterrupt);

        if (IsMacOS)
        {
            // macOS: Cmd+C = copy, Cmd+V = paste in terminal
            Map(KeyModifiers.Meta, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.Copy);
            Map(KeyModifiers.Meta, KeyCode.V, ShortcutContext.Terminal, ShortcutAction.Paste);
        }
        else
        {
            // Win/Linux: Ctrl+Shift+C = copy, Ctrl+Shift+V = paste in terminal
            Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.Copy);
            Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.V, ShortcutContext.Terminal, ShortcutAction.Paste);
        }
    }

    private void RegisterGlobalMappings(KeyModifiers primaryModifier)
    {
        Map(primaryModifier, KeyCode.T, ShortcutContext.Global, ShortcutAction.NewTab);
        Map(primaryModifier, KeyCode.W, ShortcutContext.Global, ShortcutAction.CloseTab);
        Map(primaryModifier, KeyCode.Comma, ShortcutContext.Global, ShortcutAction.OpenSettings);

        // Tab switching uses Ctrl on all platforms
        Map(KeyModifiers.Ctrl, KeyCode.Tab, ShortcutContext.Global, ShortcutAction.NextTab);
        Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.Tab, ShortcutContext.Global, ShortcutAction.PreviousTab);
    }

    private void Map(KeyModifiers modifiers, KeyCode key, ShortcutContext context, ShortcutAction action)
    {
        _mappings[(modifiers, key, context)] = action;
    }
}
