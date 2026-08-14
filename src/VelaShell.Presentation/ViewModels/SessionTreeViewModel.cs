using System.Collections.ObjectModel;
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
        MoveToGroupCommand = ReactiveCommand.Create<SessionTreeNodeViewModel>(MoveSelectedToGroup);
    }

    /// <summary>树的根级节点集合,包含各分组节点及直接挂在根级的未分组会话。</summary>
    public ObservableCollection<SessionTreeNodeViewModel> Nodes { get; }

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

    /// <summary>把指定会话移动到目标分组并落库;<paramref name="targetGroupId" /> 为 <see cref="Guid.Empty" /> 表示移回树根(未分组)。</summary>
    /// <param name="sessionId">要移动的会话标识。</param>
    /// <param name="targetGroupId">目标分组标识;<see cref="Guid.Empty" /> 表示未分组(树根)。</param>
    public void MoveSessionToGroup(Guid sessionId, Guid targetGroupId)
    {
        SessionTreeNodeViewModel? sourceNode = FindSessionNode(
            sessionId,
            out SessionTreeNodeViewModel? sourceGroup
        );
        if (sourceNode is null)
        {
            return;
        }
        if (sourceGroup is not null)
        {
            sourceGroup.Children.Remove(sourceNode);
        }
        else
        {
            Nodes.Remove(sourceNode);
        }
        if (targetGroupId == Guid.Empty)
        {
            // “未分组”落点 = 树根(设计 FrJPu)。
            sourceNode.IsRootLevel = true;
            Nodes.Add(sourceNode);
        }
        else
        {
            SessionTreeNodeViewModel? targetGroup = Nodes.FirstOrDefault(node =>
                node.IsGroup && node.Id == targetGroupId
            );
            if (targetGroup is not null)
            {
                sourceNode.IsRootLevel = false;
                targetGroup.Children.Add(sourceNode);
            }
        }
        if (_sessionCache.TryGetValue(sessionId, out SessionProfile? session))
        {
            // Guid.Empty 是“未分组”落点:落库必须存 null,否则下次加载时会话会
            // 因找不到分组而从树里消失。
            session.GroupId = targetGroupId == Guid.Empty ? null : targetGroupId;
            _ = _repository.SaveSessionAsync(session);
        }
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

    private void MoveSelectedToGroup(SessionTreeNodeViewModel? targetGroup)
    {
        if (targetGroup is not { IsGroup: true } || SelectedNode is not { IsGroup: false } node)
        {
            return;
        }
        MoveSessionToGroup(node.Id, targetGroup.Id);
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
        var copy = new SessionProfile
        {
            ConnectionType = source.ConnectionType,
            Name = Strings.Format("Svc_CopySuffix", source.Name),
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            AuthMethod = source.AuthMethod,
            Password = source.Password,
            RememberPassword = source.RememberPassword,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase,
            GroupId = source.GroupId,
            Tags = [.. source.Tags],
            JumpHostProfileId = source.JumpHostProfileId,
            Ftp = source.Ftp?.Clone(),
            PluginProtocolId = source.PluginProtocolId,
            PluginSettings = SessionProfile.CloneSettings(source.PluginSettings),
            PluginSecrets = SessionProfile.CloneSettings(source.PluginSecrets),
        };
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
