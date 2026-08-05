namespace VelaShell.Services;

/// <summary>
/// 键盘快捷键所触发的操作类型。
/// </summary>
public enum ShortcutAction
{
    /// <summary>
    /// 无操作,表示未匹配到任何快捷键。
    /// </summary>
    None,

    /// <summary>
    /// 复制选中内容。
    /// </summary>
    Copy,

    /// <summary>
    /// 粘贴剪贴板内容。
    /// </summary>
    Paste,

    /// <summary>
    /// 新建标签页。
    /// </summary>
    NewTab,

    /// <summary>
    /// 关闭当前标签页。
    /// </summary>
    CloseTab,

    /// <summary>
    /// 切换到下一个标签页。
    /// </summary>
    NextTab,

    /// <summary>
    /// 切换到上一个标签页。
    /// </summary>
    PreviousTab,

    /// <summary>
    /// 打开设置界面。
    /// </summary>
    OpenSettings,

    /// <summary>
    /// 向终端发送中断信号(如 Ctrl+C)。
    /// </summary>
    SendInterrupt
}

/// <summary>
/// 快捷键的生效上下文范围。
/// </summary>
public enum ShortcutContext
{
    /// <summary>
    /// 全局上下文,在任意界面均生效。
    /// </summary>
    Global,

    /// <summary>
    /// 终端上下文,仅在终端聚焦时生效。
    /// </summary>
    Terminal
}

/// <summary>
/// 快捷键的修饰键标志,可按位组合。
/// </summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>
    /// 无修饰键。
    /// </summary>
    None = 0,

    /// <summary>
    /// Ctrl 键(在 macOS 上对应 Command 语义视实现而定)。
    /// </summary>
    Ctrl = 1,

    /// <summary>
    /// Shift 键。
    /// </summary>
    Shift = 2,

    /// <summary>
    /// Alt 键。
    /// </summary>
    Alt = 4,

    /// <summary>
    /// Meta 键(如 Windows 键或 macOS 的 Command 键)。
    /// </summary>
    Meta = 8
}

/// <summary>
/// 参与快捷键匹配的按键标识。
/// </summary>
public enum KeyCode
{
    /// <summary>
    /// 无按键。
    /// </summary>
    None,

    /// <summary>
    /// 字母键 C。
    /// </summary>
    C,

    /// <summary>
    /// 字母键 V。
    /// </summary>
    V,

    /// <summary>
    /// 字母键 T。
    /// </summary>
    T,

    /// <summary>
    /// 字母键 W。
    /// </summary>
    W,

    /// <summary>
    /// Tab 键。
    /// </summary>
    Tab,

    /// <summary>
    /// 逗号键。
    /// </summary>
    Comma
}

/// <summary>
/// 键盘快捷键解析服务,根据按键组合与上下文映射为具体操作。
/// </summary>
public interface IKeyboardShortcutService
{
    /// <summary>
    /// 获取当前运行环境是否为 macOS,用于区分平台相关的快捷键约定。
    /// </summary>
    bool IsMacOS { get; }

    /// <summary>
    /// 根据修饰键、按键与上下文解析出对应的快捷键操作。
    /// </summary>
    ShortcutAction Resolve(KeyModifiers modifiers, KeyCode key, ShortcutContext context);
}
