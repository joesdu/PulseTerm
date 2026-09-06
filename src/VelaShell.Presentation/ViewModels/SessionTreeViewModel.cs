using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>会话树视图模型:管理分组/会话节点、选中项与右键菜单命令,并向宿主转发连接、编辑、SFTP 等操作请求。</summary>
public sealed class SessionTreeViewModel : ReactiveObject
{
    private readonly ISessionRepository _repository;
    private readonly Dictionary<Guid, SessionProfile> _sessionCache = [];

    /// <summary>
    /// 各配置最近一次上报的连接状态;重建树(LoadTreeAsync)后重放,状态圆点
    /// 与「活跃/连接中」标签才不会因刷新而回到断开态。
    /// </summary>
    private readonly Dictionary<Guid, SessionStatus> _statusCache = [];
    private readonly Dictionary<Guid, string> _syncChannelCache = [];

    private bool _hasNoSessions;

    /// <summary>用指定的会话仓储构造视图模型,并初始化各右键菜单命令及其可用性约束。</summary>
    /// <param name="repository">提供会话与分组读写、持久化的仓储。</param>
    public SessionTreeViewModel(ISessionRepository repository)
    {
        _repository = repository;
        Nodes = [];
        Nodes.CollectionChanged += OnNodesChanged;
        _hasNoSessions = true;
        LoadCommand = ReactiveCommand.CreateFromTask(LoadTreeAsync);
        IObservable<bool> hasSelectedSession = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false });
        ConnectCommand = ReactiveCommand.Create(
            () => RaiseForSelected(ConnectRequested),
            hasSelectedSession
        );
        EditSessionCommand = ReactiveCommand.Create(
            () => RaiseForSelected(EditRequested),
            hasSelectedSession
        );
        DeleteSessionCommand = ReactiveCommand.CreateFromTask(
            DeleteSelectedSessionAsync,
            hasSelectedSession
        );
        DuplicateSessionCommand = ReactiveCommand.CreateFromTask(
            DuplicateSelectedSessionAsync,
            hasSelectedSession
        );
        IObservable<bool> hasSelectedSftpProfile = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false, IsSshProfile: true } or
            { IsGroup: false, IsSftpProfile: true });
        IObservable<bool> hasSelectedSshSession = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is { IsGroup: false, IsSshProfile: true });
        OpenSftpCommand = ReactiveCommand.Create(
            () => RaiseForSelected(OpenSftpRequested),
            hasSelectedSftpProfile
        );
        PortForwardCommand = ReactiveCommand.Create(
            () => RaiseForSelectedSsh(PortForwardRequested),
            hasSelectedSshSession
        );
        DisconnectCommand = ReactiveCommand.Create(
            () => RaiseForSelected(DisconnectRequested),
            hasSelectedSession
        );
        DiagnoseCommand = ReactiveCommand.Create(
            () => RaiseForSelected(DiagnoseRequested),
            hasSelectedSession
        );
        MoveToGroupCommand = ReactiveCommand.CreateFromTask<SessionTreeNodeViewModel>(
            MoveSelectedToGroupAsync
        );
        DeleteGroupCommand = ReactiveCommand.CreateFromTask(
            DeleteSelectedGroupAsync,
            this.WhenAnyValue(x => x.SelectedNode).Select(node => node is { IsGroup: true })
        );
    }

    /// <summary>树的根级节点集合,包含各分组节点及直接挂在根级的未分组会话。</summary>
    /// <remarks>
    /// 这是<b>数据形状</b>(两层:分组 → 会话)。界面绑的不是它而是摊平后的 <see cref="Rows" /> ——
    /// 两者由 <see cref="SyncRows" /> 保持同步,这里的任何增删改都会自动反映过去。
    /// </remarks>
    public ObservableCollection<SessionTreeNodeViewModel> Nodes { get; }

    /// <summary>
    /// 摊平后的行:每个根级节点一行,展开的分组后面紧跟它的会话行。<b>界面绑的是这个。</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 之所以自己摊平、用平列表画,而不是交给 <c>TreeView</c>:那个控件为每一层预留了一块
    /// 缩进区与一枚内置箭头,而本设计的箭头是自绘的、缩进是行内 padding ——
    /// 于是只能靠一串按模板部件名去关灯的样式把内置的那套压掉,压不干净就在展开后的子行前面
    /// 留下一条<b>点不着、也不跟着高亮</b>的空白。摊平之后每一行都是同一层的普通行,
    /// 行背景从最左画到最右,那条空白从根上就不存在了。
    /// </para>
    /// <para>
    /// 就地对齐而不是清空重建:清空会让 <see cref="SelectedNode" /> 被列表控件顺手清成 null
    /// (选中项跟着 <c>SelectedItem</c> 双向绑),折一下分组就把用户的选择弄丢了。
    /// </para>
    /// </remarks>
    public ObservableCollection<SessionTreeNodeViewModel> Rows { get; } = [];

    /// <summary>已经挂上监听的节点(<see cref="Nodes" /> 的 Reset 不带旧项,得自己记着才摘得掉)。</summary>
    private readonly HashSet<SessionTreeNodeViewModel> _watched = [];

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Clear() 不给 OldItems,只能照着自己记的那份全摘掉
            foreach (SessionTreeNodeViewModel node in _watched.ToList())
            {
                Unwatch(node);
            }
        }
        foreach (SessionTreeNodeViewModel node in e.OldItems?.OfType<SessionTreeNodeViewModel>() ?? [])
        {
            Unwatch(node);
        }
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            Watch(node);
        }
        SyncRows();
    }

    /// <summary>盯住一个根级节点:它的展开状态、以及(分组的)子项增删都会改变行序。</summary>
    private void Watch(SessionTreeNodeViewModel node)
    {
        if (!_watched.Add(node))
        {
            return;
        }
        node.PropertyChanged += OnNodePropertyChanged;
        if (node.IsGroup)
        {
            node.Children.CollectionChanged += OnChildrenChanged;
        }
    }

    private void Unwatch(SessionTreeNodeViewModel node)
    {
        if (!_watched.Remove(node))
        {
            return;
        }
        node.PropertyChanged -= OnNodePropertyChanged;
        if (node.IsGroup)
        {
            node.Children.CollectionChanged -= OnChildrenChanged;
        }
    }

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTreeNodeViewModel.IsExpanded))
        {
            SyncRows();
        }
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncRows();

    /// <summary>
    /// 把 <see cref="Rows" /> 对齐到 <see cref="Nodes" /> 此刻应该摊出来的样子。
    /// </summary>
    /// <remarks>
    /// <b>只动有差异的那几行</b>(移走多余的、把错位的挪到位、补上缺的),不清空重建 ——
    /// 见 <see cref="Rows" /> 的说明。折叠把选中的会话收进去时,选中<b>上移到它那一组</b>:
    /// 不然选中项从列表里消失,而右键菜单里的命令仍然对着一个看不见的会话执行。
    /// </remarks>
    private void SyncRows()
    {
        var desired = new List<SessionTreeNodeViewModel>(Rows.Count);
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            desired.Add(node);
            if (node.IsGroup && node.IsExpanded)
            {
                desired.AddRange(node.Children);
            }
        }
        var keep = new HashSet<SessionTreeNodeViewModel>(desired);
        SessionTreeNodeViewModel? selected = SelectedNode;

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i]))
            {
                continue;
            }
            int at = Rows.IndexOf(desired[i]);
            if (at >= 0)
            {
                Rows.Move(at, i);
            }
            else
            {
                Rows.Insert(i, desired[i]);
            }
        }

        if (selected is null)
        {
            return;
        }
        // 收进去了:落到它那一组的行上。整个节点已经不在树里了(删掉了)就落空,那是对的。
        SelectedNode = keep.Contains(selected)
            ? selected
            : Nodes.FirstOrDefault(node => node.IsGroup && node.Children.Contains(selected));
    }

    /// <summary>是否当前没有任何会话,用于驱动空状态提示的显示。</summary>
    public bool HasNoSessions
    {
        get => _hasNoSessions;
        private set => this.RaiseAndSetIfChanged(ref _hasNoSessions, value);
    }

    /// <summary>无会话时的空状态提示文案(本地化)。</summary>
    public static string EmptyStateMessage => Strings.Get("Svc_AddFirstConnection");

    /// <summary>当前选中的树节点;命令的可用性依据其是否为非分组会话节点判定。</summary>
    public SessionTreeNodeViewModel? SelectedNode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>分组节点(供“移动到分组”子菜单绑定);随 LoadTreeAsync 同步。</summary>
    public ObservableCollection<SessionTreeNodeViewModel> GroupNodes { get; } = [];

    /// <summary>从仓储加载并重建整棵会话树。</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadCommand { get; }

    /// <summary>连接选中的会话,触发 <see cref="ConnectRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ConnectCommand { get; }

    /// <summary>编辑选中的会话,触发 <see cref="EditRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> EditSessionCommand { get; }

    /// <summary>删除选中的会话(含落库与树节点移除)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteSessionCommand { get; }

    // 复制选中的连接为“<名称> (副本)”并落库
    /// <summary>复制选中的会话为“&lt;名称&gt; (副本)”并落库,随后重建树。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DuplicateSessionCommand { get; }

    /// <summary>为选中的会话打开 SFTP,触发 <see cref="OpenSftpRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSftpCommand { get; }

    /// <summary>为选中的会话打开端口转发,触发 <see cref="PortForwardRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PortForwardCommand { get; }

    /// <summary>断开选中会话的连接,触发 <see cref="DisconnectRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DisconnectCommand { get; }

    /// <summary>对选中的会话发起连接诊断,触发 <see cref="DiagnoseRequested" />。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DiagnoseCommand { get; }

    /// <summary>把选中的会话移动到指定分组节点(参数为“移动到分组”子菜单项)。</summary>
    public ReactiveCommand<SessionTreeNodeViewModel, RxVoid> MoveToGroupCommand { get; }

    /// <summary>删除选中的分组,连同组内全部连接一并删除(落库 + 移除树节点)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> DeleteGroupCommand { get; }

    /// <summary>
    /// 删除分组前的确认回调,由视图提供弹窗;参数是已本地化好的提示语,返回 true 才继续删。
    /// 与 <c>FileBrowserViewModel.ConfirmDelete</c> 同形:未挂回调(无头宿主/单测)时直接删,
    /// 界面上则永远挂着——不确认就删掉整组连接是不可接受的。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDeleteGroup { get; set; }

    /// <summary>右键“连接”或双击会话时触发,由宿主发起 SSH 连接。</summary>
    public event Action<SessionProfile>? ConnectRequested;

    /// <summary>右键“编辑”时触发,由宿主打开连接配置弹窗。</summary>
    public event Action<SessionProfile>? EditRequested;

    /// <summary>右键“打开 SFTP”:由宿主连接会话并展开文件浏览面板。</summary>
    public event Action<SessionProfile>? OpenSftpRequested;

    /// <summary>右键“端口转发”:由宿主打开隧道管理面板。</summary>
    public event Action<SessionProfile>? PortForwardRequested;

    /// <summary>右键“断开连接”:由宿主断开该会话已连接的终端标签。</summary>
    public event Action<SessionProfile>? DisconnectRequested;

    /// <summary>右键“连接诊断”:由宿主打开连接诊断中心(设计 RGXg1)。</summary>
    public event Action<SessionProfile>? DiagnoseRequested;

    private void RaiseForSelected(Action<SessionProfile>? handler)
    {
        if (
            SelectedNode is { IsGroup: false } node
            && _sessionCache.TryGetValue(node.Id, out SessionProfile? session)
        )
        {
            handler?.Invoke(session);
        }
    }

    private void RaiseForSelectedSsh(Action<SessionProfile>? handler)
    {
        if (SelectedNode is { IsSshProfile: true })
        {
            RaiseForSelected(handler);
        }
    }

    /// <summary>视图双击会话行时调用:选中并触发连接。</summary>
    public void RequestConnect(Guid sessionId)
    {
        if (_sessionCache.TryGetValue(sessionId, out SessionProfile? session))
        {
            ConnectRequested?.Invoke(session);
        }
    }

    /// <summary>将一个会话加入树:无分组的挂到树根,否则挂到对应分组节点下,并刷新空状态。</summary>
    /// <param name="session">要加入树的会话配置。</param>
    public void AddSession(SessionProfile session)
    {
        _sessionCache[session.Id] = session;
        var sessionNode = new SessionTreeNodeViewModel(session.Id, session.Name, false, session.ConnectionType);
        if (session.GroupId is null)
        {
            // 未分组会话直接挂树根(设计 FrJPu),不再有“未分组”目录。
            sessionNode.IsRootLevel = true;
            Nodes.Add(sessionNode);
        }
        else
        {
            SessionTreeNodeViewModel? groupNode = Nodes.FirstOrDefault(node =>
                node.IsGroup && node.Id == session.GroupId
            );
            if (groupNode is null)
            {
                return;
            }
            groupNode.Children.Add(sessionNode);
        }
        RefreshHasNoSessions();
    }

    /// <summary>
    /// 把指定会话移动到目标分组;<paramref name="targetGroupId" /> 为 <see cref="Guid.Empty" /> 表示移回树根(未分组)。
    /// 树的改动是同步的,落库异步跟进(调用方不关心持久化时机时用这个重载)。
    /// </summary>
    /// <param name="sessionId">要移动的会话标识。</param>
    /// <param name="targetGroupId">目标分组标识;<see cref="Guid.Empty" /> 表示未分组(树根)。</param>
    public void MoveSessionToGroup(Guid sessionId, Guid targetGroupId) =>
        _ = MoveSessionToGroupAsync(sessionId, targetGroupId);

    /// <summary>
    /// 移动会话到目标分组并落库。源分组因此空掉时连同分组一并删除 —— 分组在本应用里
    /// 只是会话的容器,空容器既没有可展示的内容,也无法再被拖入(拖放落点是分组行本身),
    /// 留着只会变成永远清不掉的僵尸目录。
    /// </summary>
    /// <remarks>
    /// 树节点的增删全部排在第一个 await 之前:同步入口 <see cref="MoveSessionToGroup" />
    /// 靠这一点让界面立即更新,只把持久化留给后台。
    /// 落库顺序是先存会话、再删分组 —— 反过来的话中途失败会留下一批 GroupId 指向已消失
    /// 分组的会话,下次加载时它们既不在分组下、也不在树根,等于凭空消失
    /// (与 <see cref="DeleteSelectedGroupAsync" /> 同一处置)。
    /// </remarks>
    public async Task MoveSessionToGroupAsync(Guid sessionId, Guid targetGroupId)
    {
        SessionTreeNodeViewModel? sourceNode = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? sourceGroup
        );
        if (sourceNode is null)
        {
            return;
        }
        // 原地不动直接返回。少了这道判断,“拖回自己所在的分组”会先把节点摘下来、
        // 把分组判为空而删掉,再往这个已删除的分组里挂回去 —— 会话凭空消失。
        if ((sourceGroup?.Id ?? Guid.Empty) == targetGroupId)
        {
            return;
        }
        SessionTreeNodeViewModel? targetGroup = null;
        if (targetGroupId != Guid.Empty)
        {
            targetGroup = Nodes.FirstOrDefault(node => node.IsGroup && node.Id == targetGroupId);
            if (targetGroup is null)
            {
                // 目标分组不存在:整个移动放弃,不能把节点摘下来又挂不回去。
                return;
            }
        }
        if (sourceGroup is not null)
        {
            sourceGroup.Children.Remove(sourceNode);
        }
        else
        {
            Nodes.Remove(sourceNode);
        }
        if (targetGroup is null)
        {
            // “未分组”落点 = 树根(设计 FrJPu)。
            sourceNode.IsRootLevel = true;
            InsertRootSessionSorted(sourceNode);
        }
        else
        {
            sourceNode.IsRootLevel = false;
            InsertSorted(targetGroup.Children, sourceNode);
            // 拖进折叠着的分组时展开一下,否则会话看起来像是"没了"。
            targetGroup.IsExpanded = true;
        }
        bool sourceGroupEmptied = sourceGroup is { Children.Count: 0 };
        if (sourceGroupEmptied)
        {
            Nodes.Remove(sourceGroup!);
            // GroupNodes 里是同一批分组节点实例(“移动到分组”子菜单绑定它),
            // 不同步移除的话,菜单里会留下一个指向已删分组的落点。
            GroupNodes.Remove(sourceGroup!);
            if (ReferenceEquals(SelectedNode, sourceGroup))
            {
                SelectedNode = null;
            }
        }
        if (_sessionCache.TryGetValue(sessionId, out SessionProfile? session))
        {
            // Guid.Empty 是“未分组”落点:落库必须存 null,否则下次加载时会话会
            // 因找不到分组而从树里消失。
            session.GroupId = targetGroupId == Guid.Empty ? null : targetGroupId;
            await _repository.SaveSessionAsync(session);
        }
        if (sourceGroupEmptied)
        {
            await _repository.DeleteGroupAsync(sourceGroup!.Id);
        }
    }

    /// <summary>
    /// 拖放落点解析:把"鼠标松开时所在的节点"翻译成目标分组 Id
    /// (<see cref="Guid.Empty" /> = 未分组/树根)。放在视图模型里而非视图里,
    /// 是为了让落点规则可单测 —— 视图只负责找出鼠标下的那个节点。
    /// </summary>
    /// <param name="node">鼠标下的节点;树的空白处传 null。</param>
    public Guid ResolveDropTargetGroupId(SessionTreeNodeViewModel? node) =>
        node switch
        {
            null => Guid.Empty,              // 空白处 = 未分组
            { IsGroup: true } => node.Id,    // 分组行 = 该分组
            // 会话行 = 它所在的分组(根级会话即未分组),这样"拖到某台机器上"
            // 与"拖到它所在的分组上"是一回事,不必精确瞄准分组标题行。
            _ => FindGroupIdOfSession(node.Id) ?? Guid.Empty
        };

    /// <summary>
    /// 落点的显示名,供拖拽时跟随光标的提示标签使用:
    /// <see cref="Guid.Empty" />(树根)= “未分组”,其余取分组名;
    /// 分组已不在树上(理论上不该发生)时同样回落到“未分组”,而不是显示一个 Guid。
    /// </summary>
    public string DescribeDropTarget(Guid targetGroupId) =>
        targetGroupId == Guid.Empty
            ? Strings.Get("Svc_Ungrouped")
            : Nodes.FirstOrDefault(node => node.IsGroup && node.Id == targetGroupId)?.Name
              ?? Strings.Get("Svc_Ungrouped");

    /// <summary>返回会话当前所属分组 Id;根级(未分组)为 <see cref="Guid.Empty" />,节点不存在为 null。</summary>
    public Guid? FindGroupIdOfSession(Guid sessionId)
    {
        if (FindSessionNode(sessionId, out SessionTreeNodeViewModel? parentGroup) is null)
        {
            return null;
        }
        return parentGroup?.Id ?? Guid.Empty;
    }

    /// <summary>按名称把会话节点插进分组子集合,保持与 <see cref="LoadTreeAsync" /> 一致的排序。</summary>
    private static void InsertSorted(
        ObservableCollection<SessionTreeNodeViewModel> children,
        SessionTreeNodeViewModel node
    )
    {
        int index = 0;
        while (
            index < children.Count
            && string.Compare(children[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0
        )
        {
            index++;
        }
        children.Insert(index, node);
    }

    /// <summary>
    /// 把会话节点插进树根的未分组区段。根级布局是“全部分组在前、未分组会话在后”
    /// (见 <see cref="LoadTreeAsync" />),所以先跳过分组节点再按名称排位 ——
    /// 直接 Add 会让刚移出来的会话固定落在最后,与重新加载后的顺序对不上。
    /// </summary>
    private void InsertRootSessionSorted(SessionTreeNodeViewModel node)
    {
        int index = 0;
        while (index < Nodes.Count && Nodes[index].IsGroup)
        {
            index++;
        }
        while (
            index < Nodes.Count
            && string.Compare(Nodes[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0
        )
        {
            index++;
        }
        Nodes.Insert(index, node);
    }

    /// <summary>宿主上报某配置的连接状态,驱动状态圆点与「活跃/连接中/离线」标签。</summary>
    public void SetSessionStatus(Guid sessionId, SessionStatus status)
    {
        _statusCache[sessionId] = status;
        SessionTreeNodeViewModel? node = FindSessionNode(sessionId, out _);
        node?.Status = status;
    }

    /// <summary>宿主上报某配置的同步输入频道字母(空串 = 已退出),驱动节点名前的频道标识。</summary>
    public void SetSessionSyncChannel(Guid sessionId, string letter)
    {
        _syncChannelCache[sessionId] = letter;
        SessionTreeNodeViewModel? node = FindSessionNode(sessionId, out _);
        node?.SyncChannelLetter = letter;
    }

    /// <summary>展开父分组并选中指定会话;找不到时保留当前选择。</summary>
    public bool SelectSession(Guid sessionId)
    {
        SessionTreeNodeViewModel? node = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? parentGroup
        );
        if (node is null)
        {
            return false;
        }
        parentGroup?.IsExpanded = true;
        foreach (SessionTreeNodeViewModel current in EnumerateSessionNodes())
        {
            current.IsSelected = ReferenceEquals(current, node);
        }
        SelectedNode = node;
        return true;
    }

    private IEnumerable<SessionTreeNodeViewModel> EnumerateSessionNodes() =>
        Nodes.SelectMany(node => node.IsGroup ? node.Children : [node]);

    /// <summary>在树根与各分组下查找会话节点;<paramref name="parentGroup" /> 为 null 表示根级。</summary>
    private SessionTreeNodeViewModel? FindSessionNode(
        Guid sessionId,
        out SessionTreeNodeViewModel? parentGroup
    )
    {
        foreach (SessionTreeNodeViewModel node in Nodes)
        {
            if (node.IsGroup)
            {
                SessionTreeNodeViewModel? child = node.Children.FirstOrDefault(item =>
                    item.Id == sessionId
                );
                if (child is null)
                {
                    continue;
                }
                parentGroup = node;
                return child;
            }
            if (node.Id != sessionId)
            {
                continue;
            }
            parentGroup = null;
            return node;
        }
        parentGroup = null;
        return null;
    }

    private void RefreshHasNoSessions() =>
        HasNoSessions = !Nodes.Any(node => !node.IsGroup || node.Children.Count > 0);

    private async Task MoveSelectedToGroupAsync(SessionTreeNodeViewModel? targetGroup)
    {
        if (targetGroup is not { IsGroup: true } || SelectedNode is not { IsGroup: false } node)
        {
            return;
        }
        await MoveSessionToGroupAsync(node.Id, targetGroup.Id);
    }

    private async Task DuplicateSelectedSessionAsync()
    {
        if (
            SelectedNode is not { IsGroup: false } node
            || !_sessionCache.TryGetValue(node.Id, out SessionProfile? source)
        )
        {
            return;
        }
        SessionProfile copy = source.Clone();
        // 副本是一条新配置:换个 id、改个名字,其余原样。原先这里逐字段手写,
        // 每加一个字段就得记得回来补一行 —— 漏了的表现是"复制之后某个设置莫名丢了"。
        copy.Id = Guid.NewGuid();
        copy.Name = Strings.Format("Svc_CopySuffix", source.Name);
        // 复制出来的是一条从未连过的配置,"上次连接时间"不该跟着抄过来。
        copy.LastConnectedAt = null;
        await _repository.SaveSessionAsync(copy);
        await LoadTreeAsync();
    }

    private async Task LoadTreeAsync()
    {
        Nodes.Clear();
        GroupNodes.Clear();
        _sessionCache.Clear();

        // 以会话的 GroupId 为唯一事实来源分组;无分组的会话归入“未分组”节点。
        List<ServerGroup> groups = await _repository.GetAllGroupsAsync();
        List<SessionProfile> sessions = await _repository.GetAllSessionsAsync();
        var byGroup = sessions
            .Where(session => session.GroupId is not null)
            .GroupBy(session => session.GroupId!.Value)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());
        var ungrouped = sessions.Where(session => session.GroupId is null).ToList();
        int groupIndex = 0;
        foreach (ServerGroup group in groups.OrderBy(item => item.SortOrder))
        {
            var groupNode = new SessionTreeNodeViewModel(group.Id, group.Name, true)
            {
                // 文件夹图标按设计 FrJPu 以 warning/info/accent 轮换配色。
                GroupColorIndex = groupIndex++ % 3,
            };
            if (byGroup.TryGetValue(group.Id, out List<SessionProfile>? members))
            {
                foreach (
                    SessionProfile session in members.OrderBy(
                        s => s.Name,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    groupNode.Children.Add(CreateSessionNode(session, false));
                }
            }
            Nodes.Add(groupNode);
            GroupNodes.Add(groupNode);
        }

        // 未分组会话直接挂在树根(设计 FrJPu),不再收进“未分组”目录。
        foreach (
            SessionProfile session in ungrouped.OrderBy(
                s => s.Name,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            Nodes.Add(CreateSessionNode(session, true));
        }

        // “移动到分组”子菜单始终提供“未分组”落点(即移回树根)。
        GroupNodes.Add(new(Guid.Empty, Strings.Get("Svc_Ungrouped"), true));
        RefreshHasNoSessions();
    }

    private SessionTreeNodeViewModel CreateSessionNode(SessionProfile session, bool isRootLevel)
    {
        _sessionCache[session.Id] = session;
        var node = new SessionTreeNodeViewModel(session.Id, session.Name, false, session.ConnectionType)
        {
            IsRootLevel = isRootLevel,
        };
        if (_statusCache.TryGetValue(session.Id, out SessionStatus status))
        {
            node.Status = status;
        }
        if (_syncChannelCache.TryGetValue(session.Id, out string? letter))
        {
            node.SyncChannelLetter = letter;
        }
        return node;
    }

    /// <summary>
    /// 删除选中的分组:组内连接随分组一并删除(用户在确认框里被明确告知会删掉几条)。
    /// 落库顺序是先删会话再删分组 —— 反过来的话,中途失败会留下一批 GroupId 指向已消失分组的
    /// 会话,下次加载时它们既不在分组下、也不在树根,等于凭空消失。
    /// </summary>
    private async Task DeleteSelectedGroupAsync()
    {
        if (SelectedNode is not { IsGroup: true } group)
        {
            return;
        }
        List<Guid> memberIds = [.. group.Children.Select(child => child.Id)];
        if (ConfirmDeleteGroup is not null)
        {
            string message = memberIds.Count == 0
                ? Strings.Format("Tree_DeleteGroupConfirmEmpty", group.Name)
                : Strings.Format("Tree_DeleteGroupConfirm", group.Name, memberIds.Count);
            if (!await ConfirmDeleteGroup(message))
            {
                return;
            }
        }
        foreach (Guid sessionId in memberIds)
        {
            await _repository.DeleteSessionAsync(sessionId);
            _sessionCache.Remove(sessionId);
            _statusCache.Remove(sessionId);
            _syncChannelCache.Remove(sessionId);
        }
        await _repository.DeleteGroupAsync(group.Id);
        Nodes.Remove(group);
        // GroupNodes 里存的就是同一批分组节点实例(“移动到分组”子菜单绑定它),
        // 不同步移除的话,菜单里会留下一个指向已删分组的落点。
        GroupNodes.Remove(group);
        SelectedNode = null;
        RefreshHasNoSessions();
    }

    private async Task DeleteSelectedSessionAsync()
    {
        if (SelectedNode is null || SelectedNode.IsGroup)
        {
            return;
        }
        Guid sessionId = SelectedNode.Id;
        await _repository.DeleteSessionAsync(sessionId);
        _sessionCache.Remove(sessionId);
        _statusCache.Remove(sessionId);
        SessionTreeNodeViewModel? node = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? parentGroup
        );
        if (node is not null)
        {
            if (parentGroup is not null)
            {
                parentGroup.Children.Remove(node);
            }
            else
            {
                Nodes.Remove(node);
            }
        }
        SelectedNode = null;
        RefreshHasNoSessions();
    }
}
