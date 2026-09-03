using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 「已授权的聊天」那一段:每个聊天各自的范围、挡位与审批。
/// </summary>
/// <remarks>
/// <para>
/// 从前这里是一个多行文本框,一行一个聊天 id —— 那是"谁能说话"那一个轴。
/// 但把机器人拉进群之后,群里的人数会在用户不知情时增长,而<b>能碰哪些机器</b>
/// 与<b>能做到什么程度</b>是另外两个轴,它们过去只有一份全局值。
/// </para>
/// <para>
/// <b>默认值按房间类型分,而不是一刀切。</b>群要求先选范围;单聊默认不限范围 ——
/// 它只有一个对端,而且是用户逐个放行的。这一条不是宽松,是这套设计的红线:
/// 一套把作者自己也拦住的权限设计,结局是被整个关掉,回到零防护。
/// </para>
/// </remarks>
public partial class CollaborationView
{
    /// <summary>会话树里已保存的连接(建范围选择器时用;界面打开时取一次)。</summary>
    private List<SavedSessionInfo> _saved = [];

    /// <summary>一条授权在界面上的那几个控件。</summary>
    private sealed class GrantRow
    {
        public required ChatGrant Grant { get; init; }

        public required TextBox ChatId { get; init; }

        public required ComboBox Scope { get; init; }

        public required ComboBox Mode { get; init; }

        public required ComboBox Approval { get; init; }

        /// <summary>分组勾选框(键 = 分组名)。</summary>
        public required Dictionary<string, CheckBox> Groups { get; init; }

        /// <summary>单台勾选框(键 = 已保存会话 id)。</summary>
        public required Dictionary<string, CheckBox> Machines { get; init; }

        public bool Removed { get; set; }

        /// <summary>把界面上填的东西读回一份授权(空聊天 id = 这一行没填完,丢掉)。</summary>
        public ChatGrant? Harvest()
        {
            string chatId = (ChatId.Text ?? "").Trim();
            if (Removed || chatId.Length == 0)
            {
                return null;
            }
            Grant.ChatId = chatId;
            Grant.Scope = new SessionScope
            {
                Kind = Scope.SelectedIndex == 1 ? ScopeKind.Limited : ScopeKind.All,
                Groups = [.. Groups.Where(g => g.Value.IsChecked == true).Select(g => g.Key)],
                SavedIds = [.. Machines.Where(m => m.Value.IsChecked == true).Select(m => m.Key)]
            };
            // 第 0 项是"跟随全局",落库时写 null —— 存成具体值的话,用户以后改全局设置,
            // 这些聊天会一动不动,而界面上看不出为什么。
            Grant.Mode = Mode.SelectedIndex > 0 ? (ChatMode)(Mode.SelectedIndex - 1) : null;
            Grant.Approval = Approval.SelectedIndex > 0 ? (ApprovalMode)(Approval.SelectedIndex - 1) : null;
            return Grant;
        }
    }

    /// <summary>整段「已授权的聊天」。</summary>
    /// <param name="config">这个渠道。</param>
    /// <param name="rows">建出来的行(调用方保存时从这里收割)。</param>
    /// <param name="list">
    /// 装着那些行的面板。设置页上那个「允许」按钮要往里再插一行 ——
    /// 不插的话用户点完放行再点保存,反而把刚放行的又抹掉了。
    /// </param>
    private StackPanel BuildGrantsSection(ChannelConfig config, List<GrantRow> rows, out StackPanel list)
    {
        var caption = new TextBlock { Text = _loc["GrantsLabel"] };
        caption.Classes.Add("label");
        var hint = new TextBlock { Text = _loc["GrantsHint"], TextWrapping = TextWrapping.Wrap };
        hint.Classes.Add("hint");

        list = new StackPanel { Spacing = 8 };
        var panel = new StackPanel();
        panel.Children.Add(caption);
        panel.Children.Add(hint);
        panel.Children.Add(list);

        config.NormalizeGrants();
        StackPanel target = list;
        foreach (ChatGrant grant in config.Grants)
        {
            target.Children.Add(BuildGrantRow(grant.Clone(), rows, target));
        }

        Button add = HostButton(_loc["GrantAdd"], 96);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        add.Click += (_, _) => target.Children.Add(BuildGrantRow(new ChatGrant(), rows, target));
        panel.Children.Add(add);
        return panel;
    }

    /// <summary>往一个渠道的授权列表里补一行(设置页上那个「允许」按钮用)。</summary>
    /// <remarks>
    /// <b>单聊补出来的是"不限范围"。</b>它只有一个对端,而且是用户逐个放行的;
    /// 群补出来的也是不限范围 —— 与放行前的行为一致,收紧留给用户自己在这一行上点。
    /// 默认值不该由一次点击替用户做决定,但界面把范围摆在那一行上,他一眼就看得见。
    /// </remarks>
    private void AppendGrant(ChannelRow row, PendingChat chat)
    {
        if (row.Grants.Any(g => string.Equals((g.ChatId.Text ?? "").Trim(), chat.ChatId, StringComparison.Ordinal)))
        {
            return;
        }
        var grant = new ChatGrant { ChatId = chat.ChatId, IsGroup = chat.IsGroup, Label = chat.UserName };
        row.GrantsList.Children.Add(BuildGrantRow(grant, row.Grants, row.GrantsList));
    }

    private Border BuildGrantRow(ChatGrant grant, List<GrantRow> rows, Panel list)
    {
        var chatId = new TextBox { Text = grant.ChatId, PlaceholderText = _loc["GrantChatId"] };
        var kind = new TextBlock
        {
            Text = grant.ChatId.Length == 0 ? "" : _loc[grant.IsGroup ? "GrantIsGroup" : "GrantIsDirect"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        kind.Classes.Add("hint");
        Button remove = HostButton(_loc["ChannelRemove"], 64);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        head.Children.Add(chatId);
        Grid.SetColumn(kind, 1);
        head.Children.Add(kind);
        Grid.SetColumn(remove, 2);
        head.Children.Add(remove);

        var scope = new ComboBox { MinWidth = 190 };
        scope.ItemsSource = new[] { _loc["ScopeAll"], _loc["ScopeLimited"] };
        scope.SelectedIndex = grant.Scope.IsUnrestricted ? 0 : 1;

        var mode = new ComboBox { MinWidth = 130 };
        mode.ItemsSource = new[] { _loc["GrantFollowGlobal"], _loc["ModeChat"], _loc["ModePlan"], _loc["ModeAgent"] };
        mode.SelectedIndex = grant.Mode is { } m ? (int)m + 1 : 0;

        var approval = new ComboBox { MinWidth = 130 };
        approval.ItemsSource = new[]
        {
            _loc["GrantFollowGlobal"], _loc["ApprovalAsk"], _loc["ApprovalReadOnly"], _loc["ApprovalBypass"]
        };
        approval.SelectedIndex = grant.Approval is { } a ? (int)a + 1 : 0;

        var controls = new WrapPanel { ItemSpacing = 12, LineSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(Labelled(_loc["ScopeLabel"], scope));
        controls.Children.Add(Labelled(_loc["GrantMode"], mode));
        controls.Children.Add(Labelled(_loc["GrantApproval"], approval));

        StackPanel picker = BuildScopeChecklist(grant.Scope,
            out Dictionary<string, CheckBox> groups, out Dictionary<string, CheckBox> machines, out TextBlock empty);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(controls);
        body.Children.Add(picker);
        var card = new Border { Child = body };
        card.Classes.Add("card");

        var row = new GrantRow
        {
            Grant = grant,
            ChatId = chatId,
            Scope = scope,
            Mode = mode,
            Approval = approval,
            Groups = groups,
            Machines = machines
        };
        rows.Add(row);

        void Sync()
        {
            bool limited = scope.SelectedIndex == 1;
            picker.IsVisible = limited;
            // 受限却一个都没勾 = 这个聊天碰不到任何机器。说出来 —— 这个方向读错了很难自己发现,
            // 用户会以为是机器人坏了。
            empty.IsVisible = limited && _saved.Count > 0
                              && groups.Values.All(c => c.IsChecked != true)
                              && machines.Values.All(c => c.IsChecked != true);
        }

        scope.SelectionChanged += (_, _) => Sync();
        foreach (CheckBox check in groups.Values.Concat(machines.Values))
        {
            check.IsCheckedChanged += (_, _) => Sync();
        }
        remove.Click += (_, _) =>
        {
            row.Removed = true;
            list.Children.Remove(card);
        };
        Sync();
        return card;
    }

    /// <summary>
    /// 范围选择器的下半截:分组与单台的勾选框。
    /// </summary>
    /// <remarks>
    /// 勾选项来自会话树,不是让用户手打分组名 —— 打错一个字的后果是这份授权碰不到任何机器,
    /// 而界面上看不出哪里错了。
    /// </remarks>
    private StackPanel BuildScopeChecklist(SessionScope scope,
        out Dictionary<string, CheckBox> groups, out Dictionary<string, CheckBox> machines, out TextBlock empty)
    {
        groups = [];
        machines = [];
        var picker = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        empty = new TextBlock { Text = _loc["ScopeEmptyWarning"], TextWrapping = TextWrapping.Wrap };
        empty.Classes.Add("hint");
        if (_saved.Count == 0)
        {
            var none = new TextBlock { Text = _loc["ScopeNoSaved"], TextWrapping = TextWrapping.Wrap };
            none.Classes.Add("hint");
            picker.Children.Add(none);
            picker.Children.Add(empty);
            return picker;
        }
        List<string> groupNames =
        [
            .. _saved.Select(s => s.Group)
                .Where(g => g is { Length: > 0 })
                .Select(g => g!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCulture)
        ];
        if (groupNames.Count > 0)
        {
            var box = new WrapPanel { ItemSpacing = 14, LineSpacing = 4 };
            foreach (string name in groupNames)
            {
                CheckBox check = HostCheckBox(name, scope.Groups.Contains(name, StringComparer.OrdinalIgnoreCase));
                groups[name] = check;
                box.Children.Add(check);
            }
            picker.Children.Add(Section(_loc["ScopeGroups"], box));
        }
        var machineBox = new WrapPanel { ItemSpacing = 14, LineSpacing = 4 };
        foreach (SavedSessionInfo saved in _saved.OrderBy(s => s.Name, StringComparer.CurrentCulture))
        {
            CheckBox check = HostCheckBox($"{saved.Name} ({saved.Host})",
                scope.SavedIds.Contains(saved.SavedSessionId, StringComparer.Ordinal));
            machines[saved.SavedSessionId] = check;
            machineBox.Children.Add(check);
        }
        picker.Children.Add(Section(_loc["ScopeMachines"], machineBox));
        picker.Children.Add(empty);
        return picker;
    }

    /// <summary>
    /// 发给群的配对码那一段的范围选择器:整块面板 + 一个"现在勾了什么"的读取器。
    /// </summary>
    /// <remarks>
    /// <b>范围在发码时就定死。</b>从前的顺序是"先放进来,再去设置页收紧" —— 那在权限上是反的:
    /// 从放行到收紧之间那个群拥有全部权限,而人往往就忘了第二步。
    /// </remarks>
    private StackPanel BuildPairScopePicker(out Func<SessionScope> read)
    {
        StackPanel checklist = BuildScopeChecklist(new SessionScope { Kind = ScopeKind.Limited },
            out Dictionary<string, CheckBox> groups, out Dictionary<string, CheckBox> machines, out TextBlock empty);
        Dictionary<string, CheckBox> g = groups;
        Dictionary<string, CheckBox> m = machines;

        void Sync() => empty.IsVisible = _saved.Count > 0
                                         && g.Values.All(c => c.IsChecked != true)
                                         && m.Values.All(c => c.IsChecked != true);
        foreach (CheckBox check in g.Values.Concat(m.Values))
        {
            check.IsCheckedChanged += (_, _) => Sync();
        }
        Sync();

        read = () => new SessionScope
        {
            Kind = ScopeKind.Limited,
            Groups = [.. g.Where(x => x.Value.IsChecked == true).Select(x => x.Key)],
            SavedIds = [.. m.Where(x => x.Value.IsChecked == true).Select(x => x.Key)]
        };
        return checklist;
    }

    /// <summary>一个"小标题 + 控件"的竖排(下拉那几个用)。</summary>
    private static StackPanel Labelled(string label, Control control)
    {
        var caption = new TextBlock { Text = label };
        caption.Classes.Add("hint");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(caption);
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>范围选择器里的一小段(分组 / 单台)。</summary>
    private static StackPanel Section(string label, Control content)
    {
        var caption = new TextBlock { Text = label };
        caption.Classes.Add("hint");
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(caption);
        panel.Children.Add(content);
        return panel;
    }
}
