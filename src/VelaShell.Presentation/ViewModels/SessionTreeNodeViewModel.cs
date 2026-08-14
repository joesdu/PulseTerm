using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Presentation.ViewModels;

/// <summary>会话树中的一个节点视图模型,既可表示分组目录,也可表示单个会话行。</summary>
public sealed class SessionTreeNodeViewModel(
    Guid id,
    string name,
    bool isGroup,
    ConnectionType connectionType = ConnectionType.SSH) : ReactiveObject
{
    /// <summary>节点唯一标识(对应分组或会话的 Id)。</summary>
    public Guid Id { get; } = id;

    /// <summary>该节点是否为分组目录(而非叶子会话)。</summary>
    public bool IsGroup { get; } = isGroup;

    /// <summary>叶子节点的协议类型;分组节点使用 SSH 默认值。</summary>
    public ConnectionType ConnectionType { get; } = connectionType;

    /// <summary>该节点是否允许 SSH 专属操作(终端侧 SFTP/隧道)。</summary>
    public bool IsSshProfile => !IsGroup && ConnectionType == ConnectionType.SSH;

    /// <summary>该节点是否为独立的 SFTP 配置文件。</summary>
    public bool IsSftpProfile => !IsGroup && ConnectionType == ConnectionType.SFTP;

    /// <summary>该节点是否支持独立的 SFTP 操作。</summary>
    public bool CanOpenSftp => IsSshProfile || IsSftpProfile;

    /// <summary>节点显示名称,可在重命名时更新并触发通知。</summary>
    public string Name
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = name;

    /// <summary>分组节点是否处于展开状态(默认分组展开、会话不适用)。</summary>
    public bool IsExpanded
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = isGroup;

    /// <summary>与 TreeViewItem 的选中态双向同步;选中行按设计 FrJPu 高亮(bg-active + accent 名称)。</summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 未分组会话直接挂在树根(设计 FrJPu:不再有“未分组”目录),此时行内缩进
    /// 与分组行对齐而不是子项缩进。
    /// </summary>
    public bool IsRootLevel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 状态圆点与标签(设计 FrJPu):Connected→绿点+「活跃」,Connecting→黄点+
    /// 「连接中」,Error→红点+「离线」,Disconnected→红点、无标签。
    /// </summary>
    public SessionStatus Status
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsConnected));
            this.RaisePropertyChanged(nameof(IsConnecting));
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(HasStatusTag));
            this.RaisePropertyChanged(nameof(StatusTagText));
        }
    } = SessionStatus.Disconnected;

    /// <summary>
    /// 同步输入频道字母(A/B/C/D),显示在状态圆点之前并以频道色着色(颜色由视图层
    /// 按字母解析);空串 = 该配置没有任何标签在频道中。由宿主随标签的频道变化上报。
    /// </summary>
    public string SyncChannelLetter
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasSyncChannel));
        }
    } = string.Empty;

    /// <summary>是否显示同步输入频道字母(<see cref="SyncChannelLetter" /> 非空)。</summary>
    public bool HasSyncChannel => SyncChannelLetter.Length > 0;

    /// <summary>会话是否已连接(状态为 <see cref="SessionStatus.Connected" />)。</summary>
    public bool IsConnected => Status == SessionStatus.Connected;

    /// <summary>会话是否正在连接中(状态为 <see cref="SessionStatus.Connecting" />)。</summary>
    public bool IsConnecting => Status == SessionStatus.Connecting;

    /// <summary>会话是否处于错误/离线状态(状态为 <see cref="SessionStatus.Error" />)。</summary>
    public bool IsError => Status == SessionStatus.Error;

    /// <summary>是否需要显示状态标签(除断开状态外均显示)。</summary>
    public bool HasStatusTag => Status != SessionStatus.Disconnected;

    /// <summary>根据当前状态返回的本地化状态标签文本。</summary>
    public string StatusTagText => Status switch
    {
        SessionStatus.Connected => Strings.Get("Svc_Active"),
        SessionStatus.Connecting => Strings.Connecting,
        SessionStatus.Error => Strings.Get("Svc_Offline"),
        _ => string.Empty
    };

    /// <summary>
    /// 分组文件夹图标的配色序号(设计 FrJPu 按 warning/info/accent 轮换),
    /// 由树加载时按分组顺序赋值。
    /// </summary>
    public int GroupColorIndex
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsFolderInfo));
            this.RaisePropertyChanged(nameof(IsFolderAccent));
        }
    }

    /// <summary>分组图标是否使用 info 配色(<see cref="GroupColorIndex" /> 为 1)。</summary>
    public bool IsFolderInfo => GroupColorIndex == 1;

    /// <summary>分组图标是否使用 accent 配色(<see cref="GroupColorIndex" /> 为 2)。</summary>
    public bool IsFolderAccent => GroupColorIndex == 2;

    /// <summary>
    /// 拖放中该节点是否为当前落点(仅分组行渲染高亮)。拖会话经过时由视图置位,
    /// 离开/放下即清除 —— 没有这个反馈,用户无从知道松手会落进哪个分组。
    /// </summary>
    public bool IsDropTarget
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>该分组节点下的子节点集合(会话或子分组)。</summary>
    public ObservableCollection<SessionTreeNodeViewModel> Children { get; } = [];
}
