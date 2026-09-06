namespace VelaShell.Docking.Model;

/// <summary>
/// 终端工作区布局:一棵 分栏/标签组 树 + 全局激活文档,并提供全部结构操作
/// (docs/dock-replacement-plan.md §2.3)。取代原 TerminalDockFactory + Dock.Model:
/// 新文档进主组;用户关闭走 <see cref="CloseDocument" />(触发 <see cref="DocumentClosed" />,
/// 下游据此断 SSH/SFTP/日志);程序撤除走 <see cref="RemoveDocument" />(静默)。
/// 空的非主组自动折叠,单子分栏自动提升。
/// </summary>
public sealed class DockWorkspace : DockElement
{
    private DockNode _root;

    /// <summary>创建仅含主组的空工作区,主组即为初始布局树根。</summary>
    public DockWorkspace()
    {
        PrimaryGroup = new DockGroup { IsPrimary = true };
        _root = PrimaryGroup;
    }

    /// <summary>布局树根:单组时就是主组,拆分后为最外层分栏。</summary>
    public DockNode Root
    {
        get => _root;
        private set => SetField(ref _root, value);
    }

    /// <summary>新文档默认加入的组;清空后也不折叠。</summary>
    public DockGroup PrimaryGroup { get; }

    /// <summary>全局激活文档(最后交互的组的选中标签),驱动 ActiveTerminalTab/状态栏联动。</summary>
    public DockDocument? ActiveDocument
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                ActiveDocumentChanged?.Invoke(value);
            }
        }
    }

    /// <summary>激活文档变化(合并了原 Dock 的 ActiveDockableChanged + FocusedDockableChanged)。</summary>
    public event Action<DockDocument?>? ActiveDocumentChanged;

    /// <summary>用户语义的“关闭标签”完成后触发;程序性 <see cref="RemoveDocument" /> 不触发。</summary>
    public event Action<DockDocument>? DocumentClosed;

    /// <summary>
    /// 文档离开工作区时触发(无论用户关闭还是程序撤除;组间移动不算),
    /// 供控件层清理视图缓存。
    /// </summary>
    public event Action<DockDocument>? DocumentRemoved;

    /// <summary>
    /// 文档进入工作区时触发(组间移动不算)。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DocumentRemoved" /> 成对,让"工作区是标签集合的唯一事实来源"这件事
    /// 在生命周期两端都成立。缺了这一半,宿主要挂每个标签的订阅(同步输入、会话状态、
    /// 快捷命令目标)就只能另存一份平行的标签列表去监听它的 <c>CollectionChanged</c> ——
    /// 那正是 Q-02 要拆掉的东西。
    /// </remarks>
    public event Action<DockDocument>? DocumentAdded;

    // ---- 查询 ----

    /// <summary>深度遍历布局树,返回其中全部标签组。</summary>
    public IEnumerable<DockGroup> AllGroups() => EnumerateGroups(Root);

    /// <summary>返回工作区内所有组的全部文档(拉平的标签集合)。</summary>
    public IEnumerable<DockDocument> AllDocuments() => AllGroups().SelectMany(group => group.Documents);

    /// <summary>查找该文档当前所属的组,未在树上则返回 null。</summary>
    public DockGroup? FindGroup(DockDocument document) =>
        AllGroups().FirstOrDefault(group => group.Documents.Contains(document));

    /// <summary>布局里是否不止一个标签组(= 当前处于分屏状态)。</summary>
    /// <remarks>
    /// 窗格移焦手势要靠它做**有条件拦截**:只有一个窗格时 Alt+方向键必须原样送给远端
    /// (zsh / fish 里有人绑了它),不能被无条件吃掉。
    /// </remarks>
    public bool HasMultipleGroups => AllGroups().Skip(1).Any();

    /// <summary>
    /// 从给定组出发,沿父链找到指定方向上相邻的那个标签组;边缘处返回 null。
    /// </summary>
    /// <remarks>
    /// 做法是沿父链上溯,找第一个**方向匹配**的分栏(左右看水平分栏,上下看垂直分栏),
    /// 在它的子节点里取相邻的那个,再向下钻到一个具体的组。向左/上时从相邻子树的
    /// **末端**进入,向右/下时从**首端**进入 —— 这样跨越嵌套分屏时,落点总是视觉上最近的那个窗格。
    /// </remarks>
    /// <param name="from">出发的组。</param>
    /// <param name="direction">方向。</param>
    /// <returns>相邻的组;该方向上没有邻居时为 null。</returns>
    public static DockGroup? FindNeighborGroup(DockGroup from, DockDirection direction)
    {
        ArgumentNullException.ThrowIfNull(from);
        bool horizontal = direction is DockDirection.Left or DockDirection.Right;
        int step = direction is DockDirection.Left or DockDirection.Up ? -1 : 1;

        DockNode node = from;
        while (node.Parent is { } split)
        {
            bool matchesAxis = horizontal == (split.Orientation == DockOrientation.Horizontal);
            int index = split.Children.IndexOf(node);
            int target = index + step;
            if (matchesAxis && index >= 0 && target >= 0 && target < split.Children.Count)
            {
                return Descend(split.Children[target], enterFromEnd: step < 0);
            }
            node = split;
        }
        return null;
    }

    /// <summary>向下钻到一个具体的组:遇到分栏就按进入方向取首/末子节点。</summary>
    private static DockGroup? Descend(DockNode node, bool enterFromEnd)
    {
        while (true)
        {
            switch (node)
            {
                case DockGroup group:
                    return group;
                case DockSplit { Children.Count: > 0 } split:
                    node = enterFromEnd ? split.Children[^1] : split.Children[0];
                    continue;
                default:
                    return null;
            }
        }
    }

    private static IEnumerable<DockGroup> EnumerateGroups(DockNode node)
    {
        switch (node)
        {
            case DockGroup group:
                yield return group;
                break;
            case DockSplit split:
                foreach (DockNode child in split.Children.ToArray())
                {
                    foreach (DockGroup group in EnumerateGroups(child))
                    {
                        yield return group;
                    }
                }
                break;
        }
    }

    // ---- 增删与激活 ----

    /// <summary>加入主组并激活(与原 Dock 行为一致:新终端总进第一组)。</summary>
    public void AddDocument(DockDocument document)
    {
        PrimaryGroup.Documents.Add(document);
        // 先报"来了"再激活:订阅方(宿主的每标签接线)要在活动标签切过去之前就位,
        // 否则激活回调里读到的还是一个没接好线的标签。
        DocumentAdded?.Invoke(document);
        ActivateDocument(document);
    }

    /// <summary>激活文档:选中其标签并设为全局激活。</summary>
    public void ActivateDocument(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        group.ActiveDocument = document;
        ActiveDocument = document;
    }

    /// <summary>
    /// 静默移除(撤掉连接失败的标签等程序行为),不触发 <see cref="DocumentClosed" />。
    /// </summary>
    public void RemoveDocument(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(document);
        group.Documents.RemoveAt(index);
        if (ReferenceEquals(group.ActiveDocument, document))
        {
            group.ActiveDocument = group.Documents.Count > 0
                                       ? group.Documents[Math.Min(index, group.Documents.Count - 1)]
                                       : null;
        }
        CollapseIfEmpty(group);
        if (ReferenceEquals(ActiveDocument, document))
        {
            // 组仍在树上则接管其新选中标签;组已折叠则退回仍有文档的第一个组。
            ActiveDocument = group.ActiveDocument
                             ?? AllGroups().FirstOrDefault(g => g.ActiveDocument is not null)?.ActiveDocument;
        }
        DocumentRemoved?.Invoke(document);
    }

    /// <summary>
    /// 关闭前的拦截器:返回 false 即取消本次关闭。由宿主装上"关闭已连接标签前确认"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 装在这一层而不是各个入口:用户能触发关闭的地方有六个(标签 ×、Ctrl+W、命令面板、
    /// 右键的 关闭其他 / 关闭全部 / 关闭左侧 / 关闭右侧),逐个去接必然漏。
    /// </para>
    /// <para>
    /// <see cref="CloseDocument" /> 保持**无条件**:拦截器放行后由它执行,程序性关闭
    /// (连接失败撤标签、退出时清场)也照旧直接调它,不会被确认框挡住。
    /// </para>
    /// </remarks>
    public Func<IReadOnlyList<DockDocument>, Task<bool>>? CloseInterceptor { get; set; }

    /// <summary>用户语义的关闭请求:先过拦截器,通过了才真的关。</summary>
    /// <param name="document">要关闭的文档。</param>
    public void RequestClose(DockDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequestCloseMany([document]);
    }

    /// <summary>
    /// 批量关闭请求:一次询问、一次放行,而不是逐个弹确认框把人问烦。
    /// </summary>
    /// <param name="documents">要关闭的文档集合(会先取快照,关闭过程改动集合不影响遍历)。</param>
    public void RequestCloseMany(IEnumerable<DockDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        DockDocument[] targets = [.. documents];
        if (targets.Length == 0)
        {
            return;
        }
        if (CloseInterceptor is not { } interceptor)
        {
            CloseEach(targets);
            return;
        }
        _ = RequestCloseAsync(interceptor, targets);
    }

    private async Task RequestCloseAsync(
        Func<IReadOnlyList<DockDocument>, Task<bool>> interceptor,
        DockDocument[] targets)
    {
        if (await interceptor(targets).ConfigureAwait(true))
        {
            CloseEach(targets);
        }
    }

    private void CloseEach(DockDocument[] targets)
    {
        foreach (DockDocument document in targets)
        {
            CloseDocument(document);
        }
    }

    /// <summary>用户语义的关闭:尊重 CanClose,移除后触发 <see cref="DocumentClosed" />。</summary>
    public void CloseDocument(DockDocument document)
    {
        if (!document.CanClose || FindGroup(document) is null)
        {
            return;
        }
        RemoveDocument(document);
        DocumentClosed?.Invoke(document);
    }

    /// <summary>关闭同组内除该文档以外的所有标签(组内语义与原 Dock 一致)。</summary>
    public void CloseOtherDocuments(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        // 经 RequestCloseMany:一次确认放行全部,而不是逐个弹框。
        RequestCloseMany(group.Documents.Where(d => !ReferenceEquals(d, document)).ToArray());
    }

    /// <summary>关闭该文档所在组的所有标签(含自身)。</summary>
    /// <remarks>经 <see cref="RequestCloseMany" />:一次询问放行全部,而不是逐个弹框。</remarks>
    public void CloseAllDocuments(DockDocument document)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        RequestCloseMany(group.Documents.ToArray());
    }

    /// <summary>关闭同组内位于该文档左侧的所有标签。</summary>
    public void CloseLeftDocuments(DockDocument document) => CloseToSide(document, left: true);

    /// <summary>关闭同组内位于该文档右侧的所有标签。</summary>
    public void CloseRightDocuments(DockDocument document) => CloseToSide(document, left: false);

    private void CloseToSide(DockDocument document, bool left)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int index = group.Documents.IndexOf(document);
        DockDocument[] targets = left
                                     ? [.. group.Documents.Take(index)]
                                     : [.. group.Documents.Skip(index + 1)];
        // 「关闭左侧/右侧」同样要过确认闸 —— 一次静默关掉半屏已连接会话,
        // 恰恰是这道闸要防的事故。
        RequestCloseMany(targets);
    }

    // ---- 拆分与停靠 ----

    /// <summary>
    /// 右键菜单“水平/垂直拆分”:把文档移入紧邻其所属组新建的组
    /// (水平 = 新组在右,垂直 = 新组在下,各占一半)。组内唯一文档时同样拆分,
    /// 原组留空作为放置目标(所有组行为一致);
    /// 空组会在其兄弟结构收敛时自动回收(见 <see cref="HoistIfSingle" />)。
    /// </summary>
    public void SplitDocument(DockDocument document, DockOrientation orientation)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        DockGroup newGroup = DetachToNewGroup(document, group, collapseSource: false);
        InsertNeighbor(group, newGroup, orientation, after: true);
        ActivateDocument(document);
    }

    /// <summary>
    /// 拖放停靠:Center = 并入目标组(index 为插入位,-1 为末尾);
    /// 四边 = 在目标组对应侧拆分出新组。
    /// </summary>
    public void DockTo(DockDocument document, DockGroup target, DockPosition position, int index = -1)
    {
        if (FindGroup(document) is not { } source)
        {
            return;
        }
        if (position == DockPosition.Center)
        {
            MoveToGroup(document, source, target, index);
            return;
        }
        DockOrientation orientation = position is DockPosition.Left or DockPosition.Right
                                          ? DockOrientation.Horizontal
                                          : DockOrientation.Vertical;
        bool after = position is DockPosition.Right or DockPosition.Bottom;

        // 拖到自身组的边缘且组里只有它自己 = 语义上的拆分(与右键拆分一致):原组留空。
        if (ReferenceEquals(source, target) && source.Documents.Count == 1)
        {
            DockGroup splitGroup = DetachToNewGroup(document, source, collapseSource: false);
            InsertNeighbor(source, splitGroup, orientation, after);
            ActivateDocument(document);
            return;
        }
        DockGroup newGroup = DetachToNewGroup(document, source);
        // source 若因清空被折叠,目标组仍在树上(source != target 已由上面分支保证),
        // 极端情况下兜底锚定主组。
        DockGroup anchor = FindNode(target) ? target : PrimaryGroup;
        InsertNeighbor(anchor, newGroup, orientation, after);
        ActivateDocument(document);
    }

    /// <summary>组内拖拽重排。</summary>
    public void MoveDocument(DockDocument document, int newIndex)
    {
        if (FindGroup(document) is not { } group)
        {
            return;
        }
        int oldIndex = group.Documents.IndexOf(document);
        newIndex = Math.Clamp(newIndex, 0, group.Documents.Count - 1);
        if (oldIndex != newIndex)
        {
            group.Documents.Move(oldIndex, newIndex);
        }
    }

    private void MoveToGroup(DockDocument document, DockGroup source, DockGroup target, int index)
    {
        if (ReferenceEquals(source, target))
        {
            if (index >= 0)
            {
                MoveDocument(document, Math.Min(index, source.Documents.Count - 1));
            }
            ActivateDocument(document);
            return;
        }
        source.Documents.Remove(document);
        if (ReferenceEquals(source.ActiveDocument, document))
        {
            source.ActiveDocument = source.Documents.FirstOrDefault();
        }
        target.Documents.Insert(index < 0 || index > target.Documents.Count ? target.Documents.Count : index, document);
        CollapseIfEmpty(source);
        ActivateDocument(document);
    }

    /// <summary>
    /// 把文档从原组摘出放入一个新组,返回新组。拆分路径传 collapseSource: false,
    /// 让原组即使清空也留在原位(作为空放置面板);移动路径保持自动折叠。
    /// </summary>
    private DockGroup DetachToNewGroup(DockDocument document, DockGroup source, bool collapseSource = true)
    {
        source.Documents.Remove(document);
        if (ReferenceEquals(source.ActiveDocument, document))
        {
            source.ActiveDocument = source.Documents.FirstOrDefault();
        }
        var newGroup = new DockGroup();
        newGroup.Documents.Add(document);
        newGroup.ActiveDocument = document;
        if (collapseSource)
        {
            CollapseIfEmpty(source);
        }
        return newGroup;
    }

    /// <summary>
    /// 把 newGroup 插到 anchor 的旁边:父分栏方向相同则同级插入(平分 anchor 的比例),
    /// 否则用新分栏替换 anchor 再装入两者(各占一半)。
    /// </summary>
    private void InsertNeighbor(DockGroup anchor, DockGroup newGroup, DockOrientation orientation, bool after)
    {
        if (anchor.Parent is { } parent && parent.Orientation == orientation)
        {
            int i = parent.Children.IndexOf(anchor);
            if (!double.IsNaN(anchor.Proportion))
            {
                double half = anchor.Proportion / 2;
                anchor.Proportion = half;
                newGroup.Proportion = half;
            }
            parent.Children.Insert(after ? i + 1 : i, newGroup);
            return;
        }
        var split = new DockSplit(orientation) { Proportion = anchor.Proportion };
        ReplaceNode(anchor, split);
        anchor.Proportion = double.NaN;
        split.Children.Add(after ? anchor : newGroup);
        split.Children.Add(after ? newGroup : anchor);
    }

    // ---- 树维护 ----

    private bool FindNode(DockNode node)
    {
        DockNode current = node;
        while (current.Parent is { } parent)
        {
            current = parent;
        }
        return ReferenceEquals(current, Root);
    }

    private void ReplaceNode(DockNode oldNode, DockNode newNode)
    {
        if (ReferenceEquals(Root, oldNode))
        {
            Root = newNode;
            newNode.Parent = null;
            return;
        }
        DockSplit parent = oldNode.Parent!;
        int index = parent.Children.IndexOf(oldNode);
        parent.Children[index] = newNode;
    }

    private void CollapseIfEmpty(DockGroup group)
    {
        if (group.IsPrimary || group.Documents.Count > 0 || group.Parent is not { } parent)
        {
            return;
        }
        parent.Children.Remove(group);
        HoistIfSingle(parent);
    }

    private void HoistIfSingle(DockSplit split)
    {
        if (split.Children.Count != 1)
        {
            return;
        }
        DockNode child = split.Children[0];
        split.Children.RemoveAt(0);
        child.Proportion = split.Proportion;
        ReplaceNode(split, child);

        // 提升出来的若是一个空的次级组(拆分留下的空面板,兄弟已全部关闭),
        // 顺带回收,不让空面板独自留在布局里;递归令上层分栏继续收敛。
        if (child is DockGroup { IsPrimary: false, Documents.Count: 0 } emptyGroup)
        {
            CollapseIfEmpty(emptyGroup);
        }
    }
}
