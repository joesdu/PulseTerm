using VelaShell.Plugin.S3.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 把协议动作接到宿主能力上:剪贴板写入与两扇面板。
/// <para>
/// 同一个「会话 + 桶 + 对象」重复触发时聚焦已开的面板而不是叠一扇新的 ——
/// 与宿主的任务管理器、插件管理器同一套非模态窗口规矩。
/// </para>
/// </summary>
/// <param name="context">插件上下文。</param>
public sealed class S3ActionHandler(IPluginContext context) : IS3ActionHandler
{
    private readonly Dictionary<string, IPluginPanel> _panels = [with(StringComparer.Ordinal)];
    private readonly Lock _gate = new();
    private IS3ManagementService? _management;

    /// <summary>
    /// 接上文件系统。管理服务与文件服务共用同一条会话与同一个客户端,
    /// 因此要等文件系统建好才能造它。
    /// </summary>
    /// <param name="fileSystem">同一条会话上的 S3 文件系统。</param>
    public void Attach(S3ProtocolFileSystem fileSystem) =>
        _management = S3ManagementService.Create(fileSystem);

    /// <inheritdoc />
    public Task CopyShareLinkAsync(string url, CancellationToken cancellationToken = default) =>
        // 链接自带签名凭据:只进剪贴板,不记日志。
        context.Clipboard.SetTextAsync(url);

    /// <inheritdoc />
    public Task OpenObjectInspectorAsync(Guid sessionId, string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (_management is not { } management)
        {
            return Task.CompletedTask;
        }
        var loc = new Loc(context.Host.Locale);
        return OpenPanelAsync($"{sessionId}|{bucket}|{key}", $"{bucket}/{key}", 760, 620,
            () => new S3ObjectInspectorView(new(management, sessionId, bucket, key, text => context.Clipboard.SetTextAsync(text), loc), loc));
    }

    /// <inheritdoc />
    public Task OpenBucketManagerAsync(Guid sessionId, string bucket, CancellationToken cancellationToken = default)
    {
        if (_management is not { } management)
        {
            return Task.CompletedTask;
        }
        var loc = new Loc(context.Host.Locale);
        return OpenPanelAsync($"{sessionId}|{bucket}|<manager>", loc.Format("S3Mgr_Title", bucket), 1040, 680,
            () => new S3ManagerView(new(management, sessionId, bucket, loc), loc));
    }

    /// <inheritdoc />
    public async Task CloseSessionPanelsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        string prefix = $"{sessionId}|";
        List<IPluginPanel> closing = [];
        lock (_gate)
        {
            foreach (string token in _panels.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                closing.Add(_panels[token]);
                _panels.Remove(token);
            }
        }
        // 出锁再关:CloseAsync 会回调 panel.Closed,而那个回调也要拿 _gate。
        foreach (IPluginPanel panel in closing)
        {
            try
            {
                await panel.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // 关窗失败不该拖住会话关闭。
            }
        }
    }

    private async Task OpenPanelAsync(string token, string title, int width, int height, Func<object> factory)
    {
        IPluginPanel? opened;
        lock (_gate)
        {
            _panels.TryGetValue(token, out opened);
        }
        if (opened is { IsOpen: true })
        {
            // 已开着:把它激活到前台。第二扇同尺寸的 CenterOwner 窗口会像素级盖住第一扇,
            // 最小化后更是完全看不见 —— 直接 return 的话用户只会反复点而毫无反应。
            await opened.ActivateAsync().ConfigureAwait(false);
            return;
        }
        IPluginPanel panel = await context.Ui.ShowPanelAsync(
            new PanelOptions
            {
                Title = title,
                // 独立窗口而不是停靠标签页:这两扇是「对着某个对象/桶做一串操作」的工具窗,
                // 用户通常要一边看文件列表一边改配置,占掉一个标签位反而碍事。
                DisplayMode = PanelDisplayMode.Window,
                WindowWidth = width,
                WindowHeight = height,
            },
            factory).ConfigureAwait(false);
        // **先订阅再入表**:窗口在 ShowPanelAsync 内部就已经显示出来了,而这一段跑在线程池续体上。
        // 线程池一忙,用户完全来得及在续体恢复前点掉那扇窗 —— 订阅排在后面就会挂到一个
        // 永不触发的事件上,于是表里留下一个 IsOpen 恒 false 的死条目,此后这条动作
        // 每次都"开一扇窗立刻自己关掉"。
        panel.Closed += () =>
        {
            lock (_gate)
            {
                // 按引用比对再删:晚一步关闭的旧面板不该把新面板的条目抹掉,那样去重就永久失效了。
                if (_panels.TryGetValue(token, out IPluginPanel? current) && ReferenceEquals(current, panel))
                {
                    _panels.Remove(token);
                }
            }
        };
        bool won;
        lock (_gate)
        {
            // 连点右键菜单会有两路同时走到这里;另外表里那条若已经死了(见上),直接顶替。
            if (_panels.TryGetValue(token, out IPluginPanel? existing) && existing.IsOpen)
            {
                won = false;
            }
            else
            {
                _panels[token] = panel;
                won = true;
            }
        }
        if (!won)
        {
            await panel.CloseAsync().ConfigureAwait(false);
            return;
        }
        // 入表之后再复查一次:入表前那一刻被关掉的话,上面的 Closed 回调可能已经跑过了。
        if (!panel.IsOpen)
        {
            lock (_gate)
            {
                if (_panels.TryGetValue(token, out IPluginPanel? current) && ReferenceEquals(current, panel))
                {
                    _panels.Remove(token);
                }
            }
        }
    }
}
