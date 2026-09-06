using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 命令面板(Ctrl+P / Ctrl+K):一个支持模糊搜索、以键盘驱动的浮层,
/// 按分类列出会话与动作。条目来源由宿主提供,使面板保持解耦且可单元测试。
/// </summary>
public sealed class CommandPaletteViewModel : ReactiveObject
{
    private readonly List<CommandPaletteItem> _flat = [];
    private readonly Func<IReadOnlyList<CommandPaletteItem>> _itemsProvider;
    private readonly PaletteRecency? _recency;
    private IReadOnlyList<CommandPaletteItem> _all = [];

    /// <summary>
    /// 创建命令面板视图模型并接好其键盘/鼠标命令。
    /// </summary>
    /// <param name="itemsProvider">按需提供当前面板条目;为 null 时使用空列表。</param>
    /// <param name="recency">最近使用记录,用于给常用条目加权;为 null 时不加权。</param>
    public CommandPaletteViewModel(
        Func<IReadOnlyList<CommandPaletteItem>>? itemsProvider = null,
        PaletteRecency? recency = null)
    {
        _itemsProvider = itemsProvider ?? (() => []);
        _recency = recency;
        Groups = [];
        MoveDownCommand = ReactiveCommand.Create(MoveDown);
        MoveUpCommand = ReactiveCommand.Create(MoveUp);
        ExecuteSelectedCommand = ReactiveCommand.Create(ExecuteSelected);
        ActivateCommand = ReactiveCommand.Create<CommandPaletteItem>(Activate);
        CloseCommand = ReactiveCommand.Create(Close);
        this.WhenAnyValue(x => x.Query).Subscribe(_ => Rebuild());
    }

    /// <summary>
    /// 按分类分组后的已过滤条目。
    /// </summary>
    /// <remarks>
    /// 保留给测试与外部读取;<b>界面绑的是 <see cref="Rows" /></b>(摊平后可虚拟化)。
    /// </remarks>
    public ObservableCollection<CommandPaletteGroup> Groups { get; }

    /// <summary>
    /// 摊平后的结果行:每个分组一行 <see cref="CommandPaletteHeader" />,后面跟着它的条目。
    /// </summary>
    /// <remarks>
    /// 界面绑这个而不是 <see cref="Groups" />:嵌套的 <c>ItemsControl</c> 一条也虚拟化不了,
    /// 保存了几百台机器的用户每敲一个字符都要重建整棵结果控件树。
    /// </remarks>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>当前搜索文本;改动它会重新过滤条目列表。</summary>
    public string Query
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>命令面板浮层当前是否可见。</summary>
    public bool IsOpen
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前高亮项;同步维护该条目自身的高亮标记。</summary>
    public CommandPaletteItem? SelectedItem
    {
        get;
        private set
        {
            field?.IsSelected = false;
            this.RaiseAndSetIfChanged(ref field, value);
            field?.IsSelected = true;
            this.RaisePropertyChanged(nameof(HasResults));
        }
    }

    /// <summary>当前匹配查询的条目数量。</summary>
    public int ResultCount => _flat.Count;

    /// <summary>当前是否有条目匹配查询。</summary>
    public bool HasResults => _flat.Count > 0;

    /// <summary>将选中项移到下一个匹配项。</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveDownCommand { get; }

    /// <summary>将选中项移到上一个匹配项。</summary>
    public ReactiveCommand<RxVoid, RxVoid> MoveUpCommand { get; }

    /// <summary>运行当前选中项并关闭命令面板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExecuteSelectedCommand { get; }

    /// <summary>选中并立即运行所给条目(鼠标激活)。</summary>
    public ReactiveCommand<CommandPaletteItem, RxVoid> ActivateCommand { get; }

    /// <summary>关闭命令面板浮层,不运行任何条目。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    /// <summary>从提供方重新加载条目、清空查询并显示命令面板。</summary>
    public void Open()
    {
        _all = _itemsProvider();
        Query = string.Empty;
        Rebuild();
        IsOpen = true;
    }

    /// <summary>隐藏命令面板浮层。</summary>
    public void Close() => IsOpen = false;

    /// <summary>将选中项推进到下一个匹配项,到末尾时回环。</summary>
    public void MoveDown() => Move(1);

    /// <summary>将选中项推进到上一个匹配项,到开头时回环。</summary>
    public void MoveUp() => Move(-1);

    private void Move(int delta)
    {
        if (_flat.Count == 0)
        {
            return;
        }
        int index = SelectedItem is null ? -1 : _flat.IndexOf(SelectedItem);
        index = (index + delta + _flat.Count) % _flat.Count;
        SelectedItem = _flat[index];
    }

    /// <summary>关闭命令面板并触发当前选中项(若有)。</summary>
    public void ExecuteSelected()
    {
        CommandPaletteItem? item = SelectedItem;
        if (item is null)
        {
            return;
        }
        Close();
        // 先记再执行:Invoke 可能弹窗/切页面,记在后面容易在异常路径上丢掉。
        _recency?.Touch(item.Id);
        item.Invoke();
    }

    /// <summary>选中并立即运行某条目(用于鼠标点击)。</summary>
    public void Activate(CommandPaletteItem item)
    {
        SelectedItem = item;
        ExecuteSelected();
    }

    private void Rebuild()
    {
        _flat.Clear();
        string query = Query.Trim();

        // 每键入一字符全量重建;分组在挂入 Groups 之前先装配完 Items——组一旦在
        // 可见集合里,逐项 Add 会给面板每条结果发一次 CollectionChanged+布局,
        // 会话/命令上千条时按键卡顿。离线装配后每组只挂一次。
        // 打分 + 排序:原先只判"能不能匹配",结果按注册顺序摆,于是输入 st 时
        // 前缀命中的 Settings 可能排在一串子序列命中的后面 —— 用户体感就是"搜不到"。
        DateTime now = DateTime.UtcNow;
        var scored = new List<CommandPaletteItem>(_all.Count);
        for (int i = 0; i < _all.Count; i++)
        {
            CommandPaletteItem item = _all[i];
            int score = PaletteScorer.Score(item.Title, item.Hint, query, out (int Start, int Length)[] spans);
            if (score == PaletteScorer.NoMatch)
            {
                continue;
            }
            if (_recency is { } recency)
            {
                (int count, DateTime? lastUsed) = recency.Get(item.Id);
                score += PaletteScorer.RecencyBonus(count, lastUsed, now);
            }
            item.Score = score;
            item.Highlights = spans;
            // 原序作为最后一档 tiebreak,保证同分时顺序稳定(否则每次按键结果会乱跳)。
            item.OriginalIndex = i;
            scored.Add(item);
        }
        scored.Sort(static (a, b) => b.Score != a.Score
            ? b.Score.CompareTo(a.Score)
            : a.OriginalIndex.CompareTo(b.OriginalIndex));

        var byCategory = new Dictionary<string, CommandPaletteGroup>();
        var ordered = new List<CommandPaletteGroup>();
        foreach (CommandPaletteItem item in scored)
        {
            if (!byCategory.TryGetValue(item.Category, out CommandPaletteGroup? group))
            {
                group = new(item.Category);
                byCategory[item.Category] = group;
                ordered.Add(group);
            }
            group.Items.Add(item);
        }
        Groups.Clear();
        Rows.Clear();
        foreach (CommandPaletteGroup group in ordered)
        {
            Groups.Add(group);
            // 扁平行 = 表头 + 该组条目。界面绑的是 Rows(单个虚拟化列表)。
            Rows.Add(new CommandPaletteHeader(group.Category));
            foreach (CommandPaletteItem item in group.Items)
            {
                Rows.Add(item);
            }
            // _flat 驱动上下键,必须与**看到的**顺序一致(逐组、组内按分)。
            // 按全局分数顺序填会让方向键在分组之间来回跳。
            _flat.AddRange(group.Items);
        }
        SelectedItem = _flat.Count > 0 ? _flat[0] : null;
        this.RaisePropertyChanged(nameof(ResultCount));
        this.RaisePropertyChanged(nameof(HasResults));
    }

}
