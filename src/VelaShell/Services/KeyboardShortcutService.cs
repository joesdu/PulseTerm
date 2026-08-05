using System.Runtime.InteropServices;

namespace VelaShell.Services;

/// <summary>
/// 将按键组合解析为快捷键操作,按上下文套用平台相关
/// (macOS 与 Windows/Linux)的按键映射。
/// </summary>
public class KeyboardShortcutService : IKeyboardShortcutService
{
    private readonly Dictionary<(KeyModifiers, KeyCode, ShortcutContext), ShortcutAction> _mappings;

    /// <summary>
    /// 创建服务,自动探测当前操作系统是否为 macOS。
    /// </summary>
    public KeyboardShortcutService()
        : this(RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { }

    // ReSharper disable once InconsistentNaming
    /// <summary>
    /// 以显式平台标志创建服务(主要用于测试)。
    /// </summary>
    /// <param name="isMacOS">为 true 时使用 macOS 按键映射,否则使用 Windows/Linux 映射。</param>
    public KeyboardShortcutService(bool isMacOS)
    {
        IsMacOS = isMacOS;
        _mappings = [];
        RegisterMappings();
    }

    /// <summary>是否启用 macOS 按键映射。</summary>
    public bool IsMacOS { get; }

    /// <summary>
    /// 在给定上下文中将按键组合解析为对应的快捷键操作;
    /// 处于终端上下文时回退到全局映射。
    /// </summary>
    /// <param name="modifiers">当前生效的修饰键。</param>
    /// <param name="key">按下的键。</param>
    /// <param name="context">按键被按下时所处的上下文。</param>
    /// <returns>映射到的操作;未映射时返回 <see cref="ShortcutAction.None"/>。</returns>
    public ShortcutAction Resolve(KeyModifiers modifiers, KeyCode key, ShortcutContext context)
    {
        if (_mappings.TryGetValue((modifiers, key, context), out ShortcutAction action) || context == ShortcutContext.Terminal && _mappings.TryGetValue((modifiers, key, ShortcutContext.Global), out action))
        {
            return action;
        }
        return ShortcutAction.None;
    }

    private void RegisterMappings()
    {
        KeyModifiers primaryModifier = IsMacOS ? KeyModifiers.Meta : KeyModifiers.Ctrl;
        RegisterTerminalMappings();
        RegisterGlobalMappings(primaryModifier);
    }

    private void RegisterTerminalMappings()
    {
        // Ctrl+C 在终端中总是发送中断信号(两个平台皆是)
        Map(KeyModifiers.Ctrl, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.SendInterrupt);
        if (IsMacOS)
        {
            // macOS:Cmd+C = 复制,Cmd+V = 粘贴(终端内)
            Map(KeyModifiers.Meta, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.Copy);
            Map(KeyModifiers.Meta, KeyCode.V, ShortcutContext.Terminal, ShortcutAction.Paste);
        }
        else
        {
            // Win/Linux:Ctrl+Shift+C = 复制,Ctrl+Shift+V = 粘贴(终端内)
            Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.C, ShortcutContext.Terminal, ShortcutAction.Copy);
            Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.V, ShortcutContext.Terminal, ShortcutAction.Paste);
        }
    }

    private void RegisterGlobalMappings(KeyModifiers primaryModifier)
    {
        Map(primaryModifier, KeyCode.T, ShortcutContext.Global, ShortcutAction.NewTab);
        Map(primaryModifier, KeyCode.W, ShortcutContext.Global, ShortcutAction.CloseTab);
        Map(primaryModifier, KeyCode.Comma, ShortcutContext.Global, ShortcutAction.OpenSettings);

        // 标签页切换在所有平台都使用 Ctrl
        Map(KeyModifiers.Ctrl, KeyCode.Tab, ShortcutContext.Global, ShortcutAction.NextTab);
        Map(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyCode.Tab, ShortcutContext.Global, ShortcutAction.PreviousTab);
    }

    private void Map(KeyModifiers modifiers, KeyCode key, ShortcutContext context, ShortcutAction action)
    {
        _mappings[(modifiers, key, context)] = action;
    }
}
