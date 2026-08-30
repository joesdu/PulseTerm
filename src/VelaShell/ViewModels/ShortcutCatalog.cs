using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 快捷键参考页的一个分组(纯展示;产品决定不提供自定义键位)。
/// 折叠态与快捷命令面板同语言:分组头是 ToggleButton,状态挂在这里。
/// </summary>
/// <param name="id">跨语言稳定的分组标识(取分组标题的资源键)—— 换语言会整表重建,靠它把折叠态搬过去。</param>
/// <param name="title">已本地化的分组标题。</param>
/// <param name="items">分组下的全部条目(不随搜索变化)。</param>
public sealed class ShortcutGroup(string id, string title, ShortcutItem[] items) : ReactiveObject
{
    /// <summary>跨语言稳定的分组标识。</summary>
    public string Id { get; } = id;

    /// <summary>已本地化的分组标题。</summary>
    public string Title { get; } = title;

    /// <summary>分组下的全部条目。</summary>
    public ShortcutItem[] Items { get; } = items;

    /// <summary>应用搜索后的可见条目;未搜索时即全量。</summary>
    public ObservableCollection<ShortcutItem> FilteredItems { get; } = [.. items];

    /// <summary>分组是否展开。默认展开 —— 参考页的首要用途是通读,折叠是用户主动收纳。</summary>
    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;
}

/// <summary>快捷键参考页的单条记录:一个功能名及其组合键序列。</summary>
/// <param name="Label">功能说明文本(本地化后的动作名)。</param>
/// <param name="Keys">组成该快捷键的按键序列(如 ["Ctrl", "N"];鼠标手势里也可以是「双击」「滚轮」这类本地化手势名)。</param>
/// <param name="Note">生效条件备注(如「仅在会话已断开时」);无条件生效时为 <see langword="null" />。</param>
public sealed record ShortcutItem(string Label, string[] Keys, string? Note = null)
{
    /// <summary>是否有生效条件备注(模板据此决定要不要占一行备注位)。</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>
    /// 搜索匹配用的合并文本:动作名 + 键位 + 备注,一次过滤全覆盖。
    /// 键位同时收录空格与加号两种拼法 —— 用户照着键帽敲的是 "Ctrl Shift F",
    /// 照着文档敲的是 "Ctrl+Shift+F",两种都得能搜到。
    /// </summary>
    public string SearchText { get; } =
        $"{Label} {string.Join(' ', Keys)} {string.Join('+', Keys)} {Note}";
}

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
            Group("Sc_GroupGlobal",
                [
                    Item("Cmd_NewSshConnection", [Ctrl, "N"]),
                    Item("Sc_NewTabAlias", [Ctrl, "T"]),
                    Item("Sc_CloneSession", [Ctrl, Shift, "N"]),
                    Item("Cmd_OpenSettings", [Ctrl, ","]),
                    Item("Cmd_CommandPalette", [Ctrl, "K"]),
                    Item("Sc_PaletteAlt", [Ctrl, "P"]),
                ]
            ),
            Group("Sc_GroupTabsAndPanels",
                [
                    Item("CloseTab", [Ctrl, "W"]),
                    Item("Sc_NextTab", [Ctrl, "Tab"]),
                    Item("Sc_PrevTab", [Ctrl, Shift, "Tab"]),
                    Item("Sc_ToggleFileBrowser", [Ctrl, Shift, "F"]),
                    Item("Cmd_TunnelManager", [Ctrl, Shift, "T"]),
                    Item("Cmd_ToggleLineGutter", [Ctrl, Shift, "L"]),
                ]
            ),
            Group("SetVm_SectionTerminal",
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
            Group("Sc_GroupCompletion",
                [
                    Item("Sc_CompletionPopup", [Alt, "Enter"]),
                    Item("Sc_SuggestNext", ["Down"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestPrev", ["Up"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestAccept", ["Enter"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestDismiss", ["Esc"], "Sc_NoteSuggestOpen"),
                    // Ctrl+C(取消当前行)与点击终端正文同样收起弹层:按键/点击照常
                    // 下发给终端,只是顺手收口面板(#315)。
                    Item("Sc_SuggestDismiss", [Ctrl, "C"], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestDismiss", [K("Sc_KeyLeftClick")], "Sc_NoteSuggestOpen"),
                    Item("Sc_SuggestNative", ["Tab"]),
                    Item("Sc_GhostAccept", ["Right"]),
                    Item("Sc_GhostAccept", ["End"]),
                ]
            ),
            Group("Sc_GroupMouse",
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
            Group("Cmd_CommandPalette",
                [
                    Item("Sc_PaletteNext", ["Down"]),
                    Item("Sc_PalettePrev", ["Up"]),
                    Item("Sc_PaletteRun", ["Enter"]),
                    Item("Sc_PaletteClose", ["Esc"]),
                ]
            ),
            Group("Cmd_SftpFileManager",
                [
                    Item("Sc_EditPath", [Ctrl, "L"]),
                    Item("Sc_CommitPath", ["Enter"]),
                    Item("Sc_CancelPath", ["Esc"]),
                    Item("Sc_OpenEntry", [K("Sc_KeyDoubleClick")]),
                ]
            ),
            Group("Sc_GroupFileOperations",
                [
                    Item("Sc_SaveInEditor", [Ctrl, "S"]),
                    Item("Sc_CloseEditor", ["Esc"], "Sc_NoteEditorOnly"),
                ]
            ),
            Group("Cmd_ProcessManager",
                [
                    Item("Sc_RefreshProcesses", ["F5"]),
                    Item("Sc_EndTask", ["Delete"], "Sc_NoteListFocused"),
                    Item("Sc_CloseWindow", ["Esc"]),
                ]
            ),
            Group("Sc_GroupDialogs",
                [
                    Item("Sc_DialogCancel", ["Esc"], "Sc_NoteAllDialogs"),
                    Item("Sc_DialogConfirm", ["Enter"]),
                    Item("Sc_MaximizeRestore", [K("Sc_KeyTitleBar"), K("Sc_KeyDoubleClick")]),
                    Item("Sc_CancelDockDrag", ["Esc"], "Sc_NoteDragging"),
                    Item("Sc_PasswordPaste", [Ctrl, "V"]),
                    Item("Sc_PasswordCopyBlocked", [Ctrl, "C"], "Sc_NotePasswordBlocked"),
                ]
            ),
            Group("Sc_GroupAi",
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

    /// <summary>分组标题的资源键同时充当分组 id —— 换语言重建后靠它把折叠态搬过去。</summary>
    private static ShortcutGroup Group(string titleKey, ShortcutItem[] items) => new(titleKey, T(titleKey), items);

    private static ShortcutItem Item(string labelKey, string[] keys, string? noteKey = null) =>
        new(T(labelKey), keys, noteKey is null ? null : T(noteKey));

    /// <summary>本地化的手势名(左键/双击/滚轮…),与 Ctrl、Shift 一样占一枚键帽。</summary>
    private static string K(string key) => T(key);

    private static string T(string key) => Strings.Get(key);
}
