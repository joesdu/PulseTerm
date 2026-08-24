using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 应用内全部快捷键的<b>唯一事实来源</b>:设置 → 快捷键页与 <c>docs/快捷键参考.md</c> 都以本表为准。
/// </summary>
/// <remarks>
/// <para>
/// 每一条都逐一核对过真实绑定,不得列出未绑定的键位。绑定分散在六处,新增键位时按处对应补录:
/// </para>
/// <list type="bullet">
///   <item><c>Views/MainWindow.axaml</c> 的 <c>Window.KeyBindings</c> —— 全局键位;</item>
///   <item><c>Services/KeyboardShortcutService</c> —— 终端上下文 + 平台差异(macOS 用 Command);</item>
///   <item><c>VelaShell.Terminal/Input/TerminalKeyRouter</c> —— 终端控件内的剪贴板/翻页/编码分流;</item>
///   <item><c>VelaShell.Terminal/Rendering/VelaTerminalControl</c> —— 终端鼠标手势(选区、缩放、链接);</item>
///   <item><c>Views/TerminalTabView.axaml.cs</c> —— 搜索栏、补全弹层、断线态键位;</item>
///   <item>各视图/对话框自己的 <c>OnKeyDown</c>(命令面板、文件管理器、进程管理器、编辑器、AI 面板等)。</item>
/// </list>
/// <para>
/// <b>新增或修改快捷键时必须同步本表</b> —— <c>ShortcutCatalogTests</c> 会拿
/// <c>MainWindow.axaml</c> 里的 <c>KeyBinding</c> 手势与本表比对,漏登记直接测试失败。
/// 文案键统一用 <c>Sc_</c> 前缀;与命令面板同名的动作直接复用其 <c>Cmd_</c> 键,保证两处措辞一致。
/// </para>
/// </remarks>
public static class ShortcutCatalog
{
    private const string Ctrl = "Ctrl";
    private const string Shift = "Shift";
    private const string Alt = "Alt";

    /// <summary>按当前界面语言构建完整分组表(语言切换后需重新调用)。</summary>
    public static ShortcutGroup[] Build() =>
        [
            new(
                T("Sc_GroupGlobal"),
                [
                    Item("Cmd_NewSshConnection", [Ctrl, "N"]),
                    Item("Sc_NewTabAlias", [Ctrl, "T"]),
                    Item("Sc_CloneSession", [Ctrl, Shift, "N"]),
                    Item("Cmd_OpenSettings", [Ctrl, ","]),
                    Item("Cmd_CommandPalette", [Ctrl, "K"]),
                    Item("Sc_PaletteAlt", [Ctrl, "P"]),
                ]
            ),
            new(
                T("Sc_GroupTabsAndPanels"),
                [
                    Item("CloseTab", [Ctrl, "W"]),
                    Item("Sc_NextTab", [Ctrl, "Tab"]),
                    Item("Sc_PrevTab", [Ctrl, Shift, "Tab"]),
                    Item("Sc_ToggleFileBrowser", [Ctrl, Shift, "F"]),
                    Item("Cmd_TunnelManager", [Ctrl, Shift, "T"]),
                    Item("Cmd_ToggleLineGutter", [Ctrl, Shift, "L"]),
                ]
            ),
            new(
                T("SetVm_SectionTerminal"),
                [
                    Item("Copy", [Ctrl, Shift, "C"]),
                    Item("Cmd_Paste", [Ctrl, Shift, "V"]),
                    Item("Sc_PasteShiftInsert", [Shift, "Insert"]),
                    Item("Sc_SendInterrupt", [Ctrl, "C"], "Sc_NoteCtrlCCopies"),
                    Item("Sc_SearchTerminal", [Ctrl, "F"]),
                    Item("Sc_SearchNext", ["Enter"], "Sc_NoteSearchOpen"),
                    Item("Sc_SearchPrev", [Shift, "Enter"], "Sc_NoteSearchOpen"),
                    Item("Sc_SearchClose", ["Esc"], "Sc_NoteSearchOpen"),
                    Item("Sc_ScrollPageUp", ["PageUp"], "Sc_NoteMainScreen"),
                    Item("Sc_ScrollPageDown", ["PageDown"], "Sc_NoteMainScreen"),
                    Item("Sc_ScrollPageUp", [Shift, "PageUp"], "Sc_NoteAnyScreen"),
                    Item("Sc_ScrollPageDown", [Shift, "PageDown"], "Sc_NoteAnyScreen"),
                    Item("Sc_DeleteWord", [Ctrl, "Backspace"]),
                    Item("Sc_LineStart", [Shift, "Home"]),
                    Item("Sc_LineEnd", [Shift, "End"]),
                    Item("Sc_Reconnect", ["Enter"], "Sc_NoteDisconnected"),
                    Item("Sc_ReconnectAlt", [Ctrl, "R"], "Sc_NoteDisconnected"),
                    Item("Sc_CloseDisconnectedTab", ["Esc"], "Sc_NoteDisconnected"),
                ]
            ),
            new(
                T("Sc_GroupCompletion"),
                [
                    Item("Sc_CompletionPopup", [Alt, "Enter"]),
                    Item("Sc_SuggestNext", ["Down"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestPrev", ["Up"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestAccept", ["Enter"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestDismiss", ["Esc"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestNative", ["Tab"]),
                    Item("Sc_GhostAccept", ["Right"]),
                    Item("Sc_GhostAccept", ["End"]),
                ]
            ),
            new(
                T("Sc_GroupMouse"),
                [
                    Item("Sc_OpenLink", [Ctrl, K("Sc_KeyLeftClick")]),
                    Item("Sc_SelectWord", [K("Sc_KeyDoubleClick")], "Sc_NoteRequiresSetting"),
                    Item("Sc_AppendWord", [Ctrl, Shift, K("Sc_KeyDoubleClick")]),
                    Item("Sc_ExtendSelection", [Shift, K("Sc_KeyLeftClick")]),
                    Item("Sc_BlockSelection", [Alt, K("Sc_KeyDrag")]),
                    Item("Sc_AppendSelection", [Ctrl, Shift, K("Sc_KeyDrag")]),
                    Item("Sc_AppendBlockSelection", [Ctrl, Shift, Alt, K("Sc_KeyDrag")]),
                    Item("Sc_BypassMouseReport", [Shift, K("Sc_KeyDrag")], "Sc_NoteMouseReporting"),
                    Item("Sc_RightClickPaste", [K("Sc_KeyRightClick")], "Sc_NoteOptional"),
                    Item("Sc_ZoomFont", [Ctrl, K("Sc_KeyWheel")]),
                    Item("Sc_FastScroll", [Alt, K("Sc_KeyWheel")]),
                    Item("Sc_GutterMenu", [K("Sc_KeyGutter"), K("Sc_KeyRightClick")]),
                    Item("Sc_ToggleFold", [K("Sc_KeyGutter"), K("Sc_KeyLeftClick")]),
                ]
            ),
            new(
                T("Cmd_CommandPalette"),
                [
                    Item("Sc_PaletteNext", ["Down"]),
                    Item("Sc_PalettePrev", ["Up"]),
                    Item("Sc_PaletteRun", ["Enter"]),
                    Item("Sc_PaletteClose", ["Esc"]),
                ]
            ),
            new(
                T("Cmd_SftpFileManager"),
                [
                    Item("Sc_EditPath", [Ctrl, "L"]),
                    Item("Sc_CommitPath", ["Enter"]),
                    Item("Sc_CancelPath", ["Esc"]),
                    Item("Sc_OpenEntry", [K("Sc_KeyDoubleClick")]),
                ]
            ),
            new(
                T("Sc_GroupFileOperations"),
                [
                    Item("Sc_SaveInEditor", [Ctrl, "S"]),
                    Item("Sc_CloseEditor", ["Esc"], "Sc_NoteEditorOnly"),
                ]
            ),
            new(
                T("Cmd_ProcessManager"),
                [
                    Item("Sc_RefreshProcesses", ["F5"]),
                    Item("Sc_EndTask", ["Delete"], "Sc_NoteListFocused"),
                    Item("Sc_CloseWindow", ["Esc"]),
                ]
            ),
            new(
                T("Sc_GroupDialogs"),
                [
                    Item("Sc_DialogCancel", ["Esc"], "Sc_NoteAllDialogs"),
                    Item("Sc_DialogConfirm", ["Enter"]),
                    Item("Sc_MaximizeRestore", [K("Sc_KeyTitleBar"), K("Sc_KeyDoubleClick")]),
                    Item("Sc_CancelDockDrag", ["Esc"], "Sc_NoteDragging"),
                    Item("Sc_PasswordPaste", [Ctrl, "V"]),
                    Item("Sc_PasswordCopyBlocked", [Ctrl, "C"], "Sc_NotePasswordBlocked"),
                ]
            ),
            new(
                T("Sc_GroupAi"),
                [
                    Item("Sc_AiSend", ["Enter"]),
                    Item("Sc_AiNewline", [Shift, "Enter"]),
                    Item("Sc_AiHistoryPrev", ["Up"], "Sc_NoteCaretFirstLine"),
                    Item("Sc_AiHistoryNext", ["Down"], "Sc_NoteCaretLastLine"),
                    Item("Sc_AiRefNext", ["Down"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefPrev", ["Up"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefAccept", ["Enter"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefAccept", ["Tab"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefClose", ["Esc"], "Sc_NoteRefPopup"),
                    Item("Sc_AiRefDelete", ["Backspace"]),
                    Item("Sc_AiRenameCommit", ["Enter"]),
                    Item("Sc_AiRenameCancel", ["Esc"]),
                    Item("Sc_AiClosePanel", ["Esc"]),
                ]
            ),
        ];

    /// <summary>全部条目的扁平序列(计数与搜索用)。</summary>
    public static IEnumerable<ShortcutItem> Flatten(ShortcutGroup[] groups) => groups.SelectMany(group => group.Items);

    private static ShortcutItem Item(string labelKey, string[] keys, string? noteKey = null) =>
        new(T(labelKey), keys, noteKey is null ? null : T(noteKey));

    /// <summary>本地化的手势名(左键/双击/滚轮…),与 Ctrl、Shift 一样占一枚键帽。</summary>
    private static string K(string key) => T(key);

    private static string T(string key) => Strings.Get(key);
}
