using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;

namespace VelaShell.ViewModels;

/// <summary>
/// 设置 → 云同步页:GitHub Gist 多端同步的配置、手动同步与版本历史。
/// 配置保存即时生效(独立于设置窗口的“保存设置”按钮):同步配置属于本机绑定数据,
/// 不参与设置的预览/回滚,也永远不进入同步载荷。
/// </summary>
public class SyncViewModel : ReactiveObject
{
    private readonly IGistSyncService _syncService;
    private SyncSettings _config = new();

    /// <summary>注入同步服务并构建各同步/配置命令(仅在非忙碌状态下可执行)。</summary>
    public SyncViewModel(IGistSyncService syncService)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        IObservable<bool> notBusy = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        SaveConfigCommand = ReactiveCommand.CreateFromTask(SaveConfigAsync, notBusy);
        SyncNowCommand = ReactiveCommand.CreateFromTask(() => RunAsync(_syncService.SyncNowAsync), notBusy);
        PushCommand = ReactiveCommand.CreateFromTask(() => RunAsync(_syncService.PushAsync), notBusy);
        PullCommand = ReactiveCommand.CreateFromTask(() => RunAsync(_syncService.PullAsync), notBusy);
        LoadRevisionsCommand = ReactiveCommand.CreateFromTask(LoadRevisionsAsync, notBusy);
        RestoreRevisionCommand = ReactiveCommand.CreateFromTask<GistRevision>(RestoreRevisionAsync, notBusy);
    }

    // ———— 配置字段(绑定) ————

    /// <summary>是否启用云同步。</summary>
    public bool Enabled
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否在合适时机自动触发同步。</summary>
    public bool AutoSync
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>用于多端同步的 GitHub Gist ID;首次推送后由服务端回填。</summary>
    public string GistId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>本机在同步记录中显示的设备名,默认取机器名。</summary>
    public string DeviceName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = Environment.MachineName;

    /// <summary>令牌输入框内容:只写不回显;空 = 保留已保存令牌。</summary>
    public string TokenInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否已保存 GitHub 访问令牌(用于界面提示,不回显令牌本身)。</summary>
    public bool HasSavedToken
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>口令输入框内容:只写不回显;空且未点“清除”时保留已保存口令。</summary>
    public string PassphraseInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>是否已保存端到端加密口令(用于界面提示,不回显口令本身)。</summary>
    public bool HasSavedPassphrase
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否将应用设置纳入同步载荷。</summary>
    public bool SyncAppSettings
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>是否将连接配置纳入同步载荷。</summary>
    public bool SyncProfiles
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>是否将命令片段纳入同步载荷。</summary>
    public bool SyncSnippets
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    // ———— 运行状态 ————

    /// <summary>是否正在执行同步/配置操作;为 true 时禁用各命令。</summary>
    public bool IsBusy
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>最近一次操作的状态提示文本。</summary>
    public string Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>上次同步时间的本地化显示文本;从未同步时显示占位提示。</summary>
    public string LastSyncText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Get("Msg_NeverSynced");

    /// <summary>已加载的云端版本历史列表。</summary>
    public ObservableCollection<GistRevision> Revisions { get; } = [];

    /// <summary>版本历史列表是否非空(用于界面显隐)。</summary>
    public bool HasRevisions
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>保存当前同步配置(含令牌/口令输入)的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveConfigCommand { get; }

    /// <summary>立即执行一次双向同步的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> SyncNowCommand { get; }

    /// <summary>将本地数据推送到云端的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PushCommand { get; }

    /// <summary>从云端拉取数据到本地的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> PullCommand { get; }

    /// <summary>加载云端版本历史列表的命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadRevisionsCommand { get; }

    /// <summary>将数据恢复到指定历史版本的命令。</summary>
    public ReactiveCommand<GistRevision, RxVoid> RestoreRevisionCommand { get; }

    /// <summary>设置窗口打开时载入当前同步配置。</summary>
    public async Task LoadAsync()
    {
        try
        {
            _config = await _syncService.GetSyncSettingsAsync();
        }
        catch
        {
            _config = new();
        }
        Enabled = _config.Enabled;
        AutoSync = _config.AutoSync;
        GistId = _config.GistId;
        DeviceName = _config.DeviceName;
        SyncAppSettings = _config.SyncAppSettings;
        SyncProfiles = _config.SyncProfiles;
        SyncSnippets = _config.SyncSnippets;
        HasSavedToken = _syncService.HasToken(_config);
        HasSavedPassphrase = _syncService.HasPassphrase(_config);
        TokenInput = "";
        PassphraseInput = "";
        Status = "";
        UpdateLastSyncText();
    }

    private async Task SaveConfigAsync()
    {
        IsBusy = true;
        try
        {
            await SaveConfigCoreAsync();
            Status = HasSavedPassphrase
                         ? Strings.Get("Msg_SyncConfigSavedE2eOn")
                         : Strings.Get("Msg_SyncConfigSavedE2eOff");
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_SaveFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>把界面当前配置(含未保存的令牌/口令输入)落盘并刷新状态标志。</summary>
    private async Task SaveConfigCoreAsync()
    {
        // 先取最新持久化配置作为底,避免覆盖同步操作刚写入的状态字段(LastSyncAtUtc 等)。
        _config = await _syncService.GetSyncSettingsAsync();
        _config.Enabled = Enabled;
        _config.AutoSync = AutoSync;
        _config.GistId = GistId.Trim();
        _config.DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
        _config.SyncAppSettings = SyncAppSettings;
        _config.SyncProfiles = SyncProfiles;
        _config.SyncSnippets = SyncSnippets;
        _syncService.ApplyToken(_config, TokenInput);

        // 口令:输入了新值才更新;留空保持已保存值不变。
        if (!string.IsNullOrEmpty(PassphraseInput))
        {
            _syncService.ApplyPassphrase(_config, PassphraseInput);
        }
        await _syncService.SaveSyncSettingsAsync(_config);
        HasSavedToken = _syncService.HasToken(_config);
        HasSavedPassphrase = _syncService.HasPassphrase(_config);
        TokenInput = "";
        PassphraseInput = "";
    }

    private async Task RunAsync(Func<CancellationToken, Task<SyncResult>> operation)
    {
        IsBusy = true;
        Status = Strings.Get("Msg_Syncing");
        try
        {
            // 同步前先把界面当前配置落盘:防止“在输入框填了令牌/口令,
            // 没点保存就直接点立即同步”导致输入被静默丢弃(用户实际踩过的坑)。
            await SaveConfigCoreAsync();
            SyncResult result = await Task.Run(() => operation(CancellationToken.None));
            Status = result.Message;
            _config = await _syncService.GetSyncSettingsAsync();
            GistId = _config.GistId;
            HasSavedToken = _syncService.HasToken(_config);
            HasSavedPassphrase = _syncService.HasPassphrase(_config);
            UpdateLastSyncText();
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_SyncFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadRevisionsAsync()
    {
        IsBusy = true;
        try
        {
            List<GistRevision> revisions = await Task.Run(() => _syncService.GetRevisionsAsync());
            Revisions.Clear();
            foreach (GistRevision revision in revisions)
            {
                Revisions.Add(revision);
            }
            Status = revisions.Count > 0 ? Strings.Format("Msg_CloudRevisionCount", revisions.Count) : Strings.Get("Msg_NoCloudRevisions");
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_LoadRevisionsFailed", ex.Message);
        }
        finally
        {
            HasRevisions = Revisions.Count > 0;
            IsBusy = false;
        }
    }

    private async Task RestoreRevisionAsync(GistRevision revision)
    {
        IsBusy = true;
        Status = Strings.Get("Msg_Restoring");
        try
        {
            await SaveConfigCoreAsync(); // 同步操作前落盘界面配置(含未保存的口令输入)
            SyncResult result = await Task.Run(() => _syncService.RestoreRevisionAsync(revision.Version));
            Status = result.Message;
            _config = await _syncService.GetSyncSettingsAsync();
            UpdateLastSyncText();
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_RestoreFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateLastSyncText() =>
        LastSyncText = _config.LastSyncAtUtc is { } at
                           ? Strings.Format("Msg_LastSyncAt", at.ToLocalTime())
                           : Strings.Get("Msg_NeverSynced");
}
