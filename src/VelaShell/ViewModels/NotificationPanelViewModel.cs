using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>
/// 消息中心面板(侧边栏铃铛):列出留存的消息 —— 有新版本了、订阅源发来的公告与安全资讯,
/// 点一条即跳到它该去的地方。
/// <para>
/// 这里刻意**不收**运行时告警(会话断开、指纹变更):那些要的是当场打断,已各有归宿;
/// 混进来只会把真正要读的东西淹掉。
/// </para>
/// </summary>
public class NotificationPanelViewModel : ReactiveObject, IDisposable, IDraggablePanel
{
    /// <summary>浮层位置的存放处;与文件传输提示同一个集合,各占一个文档 Id。</summary>
    private const string LayoutCollection = "ui-layout";

    private const string PanelPositionId = "notification-panel";

    private readonly INotificationCenter _center;

    /// <summary>执行站内跳转:传命令 id,返回是否跳成功。</summary>
    private readonly Func<string, bool>? _commandInvoker;

    // 可空:无存储的宿主(单元测试)不提供,此时面板位置只在本次运行内保持。
    private readonly IAppDataStore? _dataStore;

    private readonly DispatcherTimer? _relativeTimeTimer;

    /// <summary>在浏览器里打开外链。</summary>
    private readonly Func<string, Task>? _urlOpener;

    /// <summary>
    /// 构造消息中心面板;站内跳转与外链打开均可为空(便于测试)。
    /// <paramref name="dataStore" /> 为空时拖拽位置不跨重启保留。
    /// </summary>
    public NotificationPanelViewModel(
        INotificationCenter center,
        Func<string, bool>? commandInvoker = null,
        Func<string, Task>? urlOpener = null,
        IAppDataStore? dataStore = null)
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
        _commandInvoker = commandInvoker;
        _urlOpener = urlOpener;
        _dataStore = dataStore;
        RestorePanelPosition();
        Items = [];
        MarkAllReadCommand = ReactiveCommand.CreateFromTask(async () => await _center.MarkAllReadAsync());
        ClearCommand = ReactiveCommand.CreateFromTask(async () => await _center.ClearAsync());
        RemoveCommand = ReactiveCommand.CreateFromTask<string>(async id => await _center.RemoveAsync(id));
        ActivateCommand = ReactiveCommand.CreateFromTask<string>(ActivateAsync);
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        _center.Changed += OnCenterChanged;
        Reload();

        // 「5 分钟前」会随时间变成「1 小时前」,每分钟刷一次即可;
        // 无 Avalonia 应用(单元测试)时没有计时器。
        if (Application.Current is not null)
        {
            _relativeTimeTimer = new()
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _relativeTimeTimer.Tick += (_, _) => RefreshRelativeTimes();
            _relativeTimeTimer.Start();
        }
    }

    /// <summary>当前展示的消息(按发布时间倒序;未读优先的排序交给源数据,这里不再重排)。</summary>
    public ObservableCollection<NotificationItemViewModel> Items { get; }

    /// <summary>未读条数。</summary>
    public int UnreadCount => _center.UnreadCount;

    /// <summary>是否有未读(控制标题栏角标显隐)。</summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>列表是否为空(控制空态提示)。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>只看未读。</summary>
    public bool UnreadOnly
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            Reload();
        }
    }

    /// <summary>空态提示文案:全部已读时说"没有未读",本来就没消息时说"暂无消息"。</summary>
    public string EmptyHint => Strings.Get(UnreadOnly ? "Notify_EmptyUnread" : "Notify_Empty");

    /// <summary>全部标为已读。</summary>
    public ReactiveCommand<RxVoid, RxVoid> MarkAllReadCommand { get; }

    /// <summary>清空全部消息。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; }

    /// <summary>删除一条消息。</summary>
    public ReactiveCommand<string, RxVoid> RemoveCommand { get; }

    /// <summary>点开一条消息:标记已读并跳到它的去处。</summary>
    public ReactiveCommand<string, RxVoid> ActivateCommand { get; }

    /// <summary>收起面板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    // ---- 面板拖拽位置(与文件传输提示同一套:见 IDraggablePanel) ----

    /// <summary>面板相对默认锚点(左下角贴着铃铛)的水平偏移(像素)。</summary>
    public double PanelOffsetX
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>面板相对默认锚点的垂直偏移(像素,向上为负)。</summary>
    public double PanelOffsetY
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>拖拽结束时由视图调用:把当前位置落盘,供下次打开恢复。失败不影响使用。</summary>
    public void PersistPanelPosition()
    {
        if (_dataStore is null)
        {
            return;
        }
        var position = new PanelPosition { OffsetX = PanelOffsetX, OffsetY = PanelOffsetY };
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await _dataStore.UpsertAsync(LayoutCollection, PanelPositionId, position).ConfigureAwait(false);
            }
            catch
            {
                // 位置记不住不该影响消息中心本身;下次拖动会再试一次。
            }
        }
    }

    /// <summary>启动时异步取回上次的位置。取不到就保持默认锚点。</summary>
    private void RestorePanelPosition()
    {
        if (_dataStore is null)
        {
            return;
        }
        _ = LoadAsync();

        async Task LoadAsync()
        {
            try
            {
                PanelPosition? saved = await _dataStore
                                            .GetAsync<PanelPosition>(LayoutCollection, PanelPositionId)
                                            .ConfigureAwait(true);
                if (saved is null)
                {
                    return;
                }
                PanelOffsetX = saved.OffsetX;
                PanelOffsetY = saved.OffsetY;
            }
            catch
            {
                // 读不出来就用默认位置,不打扰用户。
            }
        }
    }

    /// <summary>释放面板资源:停表并退订消息中心。</summary>
    public void Dispose()
    {
        _relativeTimeTimer?.Stop();
        _center.Changed -= OnCenterChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>面板收起请求(右上角关闭 / 跳转后自动收起)。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 点开一条消息:先标已读,再跳。
    /// <para>
    /// 站内命令优先于外链 —— 站内能办的事不该把人赶去浏览器。
    /// </para>
    /// </summary>
    private async Task ActivateAsync(string id)
    {
        NotificationItemViewModel? item = Items.FirstOrDefault(entry => entry.Id == id);
        if (item is null)
        {
            return;
        }
        await _center.MarkReadAsync(id).ConfigureAwait(true);
        if (item.Link is not { } link)
        {
            return;
        }
        if (link.CommandId is { Length: > 0 } commandId && _commandInvoker?.Invoke(commandId) == true)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 外链只放行 https:内容来自远端源,http 等于让投递方把用户导去一条可被改写的链路。
        if (link.Url is not { Length: > 0 } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            _urlOpener is null)
        {
            return;
        }
        try
        {
            await _urlOpener(url).ConfigureAwait(true);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 没有浏览器可用/被系统拒绝时静默:面板本身仍然可用。
        }
    }

    /// <summary>
    /// 消息中心在任意线程触发变更,刷新列表必须回到 UI 线程。
    /// 无 Avalonia 应用(单元测试)时没有 dispatcher 循环,Post 进去的回调永远不会跑,
    /// 此时直接同步刷新。
    /// </summary>
    private void OnCenterChanged()
    {
        if (Application.Current is null)
        {
            Reload();
            return;
        }
        Dispatcher.UIThread.Post(Reload);
    }

    /// <summary>按当前筛选重建列表。</summary>
    private void Reload()
    {
        IEnumerable<NotificationItem> source = _center.Items;
        if (UnreadOnly)
        {
            source = source.Where(item => !item.IsRead);
        }
        Items.Clear();
        foreach (NotificationItem item in source)
        {
            Items.Add(new(item));
        }
        this.RaisePropertyChanged(nameof(UnreadCount));
        this.RaisePropertyChanged(nameof(HasUnread));
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(EmptyHint));
    }

    private void RefreshRelativeTimes()
    {
        foreach (NotificationItemViewModel item in Items)
        {
            item.RefreshRelativeTime();
        }
    }
}
