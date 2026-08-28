using System.Security.Cryptography;
using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sync;

namespace VelaShell.Infrastructure.Sync;

/// <summary>
/// Gist 云同步编排:采集(设置/连接/片段)→ 打包(可选端到端加密)→ 推送;
/// 拉取 → 解包 → 应用(保留设备本地字段、upsert 合并不删本地独有数据)。
/// 智能方向:本地改动标记 × 远端 revision 比对;双端都改按“较新者胜”,
/// 被覆盖的一方永远可从 Gist 修订历史恢复。
/// </summary>
public sealed class GistSyncService(
    ISettingsService settingsService,
    ISessionRepository sessionRepository,
    IAppDataStore appDataStore,
    IQuickCommandRepository quickCommandRepository,
    ISecretProtector secretProtector,
    Core.Services.IBackgroundActivityService? backgroundActivity = null
) : IGistSyncService
{
    private const int CurrentSchemaVersion = 2;
    private const string ConfigCollection = "app_config";
    private const string ConfigDocId = "sync";
    private const string GistFileName = "velashell-sync.json";
    private const string GistDescription = "VelaShell settings sync (managed by VelaShell)";
    private const string TunnelsCollection = "tunnels";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>同步操作串行化:防止自动推送与手动同步并发互踩。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 在状态栏右下角的圆环上开一条"同步中"的活动;副标题用设置页上同名按钮的文案
    /// (仅推送 / 仅拉取 / 立即同步),用户看到的措辞与他点下去的那个按钮一致。
    /// <para>
    /// 走不确定态:整段是网络往返,给不出有意义的百分比,而这条活动要回答的问题
    /// 本来也只有一个 —— "现在到底有没有在同步"。
    /// </para>
    /// <para>
    /// 自动同步一向静默(启动拉取、保存后防抖推送,失败都不打扰用户),但"不打扰"
    /// 不该等于"什么都看不见":用户的连接配置正在被上传或覆盖,这件事值得有个去处。
    /// </para>
    /// </summary>
    /// <param name="actionKey">设置页动作按钮的资源键。</param>
    /// <returns>活动句柄;未注入账本时为 <see langword="null" />。</returns>
    private Core.Services.IBackgroundActivityScope? BeginSyncActivity(string actionKey) =>
        backgroundActivity?.Begin(Strings.Get("Msg_Syncing"), Strings.Get(actionKey));

    /// <summary>是否正在应用远端数据;为 true 时忽略由此触发的本地保存事件,避免拉取后被误判为本地改动而立即回推。</summary>
    public bool IsApplyingRemote { get; private set; }

    /// <inheritdoc />
    public event EventHandler? ProfilesApplied;

    /// <summary>读取持久化的同步配置;不存在时返回默认 <see cref="SyncSettings"/>。</summary>
    public async Task<SyncSettings> GetSyncSettingsAsync(
        CancellationToken cancellationToken = default
    ) =>
        await appDataStore
            .GetAsync<SyncSettings>(ConfigCollection, ConfigDocId, cancellationToken)
            .ConfigureAwait(false)
        ?? new SyncSettings();

    /// <summary>将同步配置保存到应用数据存储。</summary>
    public Task SaveSyncSettingsAsync(
        SyncSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        return appDataStore.UpsertAsync(ConfigCollection, ConfigDocId, settings, cancellationToken);
    }

    /// <summary>将明文 GitHub 令牌加密后写入配置;空白令牌则保留原有值不变。</summary>
    public void ApplyToken(SyncSettings settings, string? plainToken)
    {
        if (!string.IsNullOrWhiteSpace(plainToken))
        {
            settings.ProtectedToken = secretProtector.Protect(plainToken.Trim());
        }
    }

    /// <summary>设置端到端加密口令:传 null 保留现有口令,传空字符串清除口令,其余值加密后保存。</summary>
    public void ApplyPassphrase(SyncSettings settings, string? plainPassphrase)
    {
        if (plainPassphrase is null)
        {
            return; // 保留现有口令
        }
        settings.ProtectedPassphrase =
            plainPassphrase.Length == 0 ? null : secretProtector.Protect(plainPassphrase);
    }

    /// <summary>配置中是否已保存 GitHub 令牌。</summary>
    public bool HasToken(SyncSettings settings) => !string.IsNullOrEmpty(settings.ProtectedToken);

    /// <summary>配置中是否已设置端到端加密口令。</summary>
    public bool HasPassphrase(SyncSettings settings) =>
        !string.IsNullOrEmpty(settings.ProtectedPassphrase);

    /// <summary>标记本地发生用户改动(更新改动时间戳),供后续同步判定推送方向;应用远端数据期间的保存不计入。</summary>
    public async Task MarkLocalChangedAsync(CancellationToken cancellationToken = default)
    {
        // 应用远端数据期间触发的保存事件不是用户改动,忽略,否则拉取后会立刻无谓回推。
        if (IsApplyingRemote)
        {
            return;
        }
        SyncSettings settings = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
        settings.LastLocalChangeAtUtc = DateTime.UtcNow;
        await SaveSyncSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行智能双向同步:比对本地改动标记与远端 revision,按“较新者胜”决定推送或拉取。</summary>
    public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_SyncNow");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            string token = Token(config);

            // 尚未绑定 Gist:首次推送即创建。
            if (string.IsNullOrEmpty(config.GistId))
            {
                return await PushCoreAsync(config, token, cancellationToken).ConfigureAwait(false);
            }
            (string? content, string remoteVersion) = await GistApiClient
                .GetFileAsync(token, config.GistId, GistFileName, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                return await PushCoreAsync(config, token, cancellationToken).ConfigureAwait(false);
            }
            bool localChanged =
                config.LastLocalChangeAtUtc is not null
                && (
                    config.LastSyncAtUtc is null
                    || config.LastLocalChangeAtUtc > config.LastSyncAtUtc
                );
            bool remoteChanged = !string.Equals(
                remoteVersion,
                config.LastRemoteVersion,
                StringComparison.Ordinal
            );
            if (!localChanged && !remoteChanged)
            {
                return new(SyncAction.UpToDate, true, Strings.Get("SyncSvc_UpToDate"));
            }
            if (localChanged && !remoteChanged)
            {
                return await PushCoreAsync(config, token, cancellationToken).ConfigureAwait(false);
            }
            SyncEnvelope envelope = ParseEnvelope(content);
            if (!localChanged)
            {
                return await ApplyRemoteAsync(config, envelope, remoteVersion, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 双端都有改动:较新者胜(LWW);被覆盖的内容在 Gist 修订历史中可随时找回。
            if (envelope.UpdatedAtUtc >= config.LastLocalChangeAtUtc)
            {
                SyncResult pulled = await ApplyRemoteAsync(
                        config,
                        envelope,
                        remoteVersion,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return pulled with
                {
                    Message = pulled.Message + Strings.Get("SyncSvc_BothChangedRemoteWins"),
                };
            }
            SyncResult pushed = await PushCoreAsync(config, token, cancellationToken)
                .ConfigureAwait(false);
            return pushed with
            {
                Message = pushed.Message + Strings.Get("SyncSvc_BothChangedLocalWins"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>强制将本地数据打包推送到 Gist(尚未绑定时会创建新的 Gist)。</summary>
    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_PushOnly");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            return await PushCoreAsync(config, Token(config), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>强制从 Gist 拉取远端数据并应用到本地(合并式,不删除本地独有数据)。</summary>
    public async Task<SyncResult> PullAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_PullOnly");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            if (string.IsNullOrEmpty(config.GistId))
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NoGistBound"));
            }
            (string? content, string version) = await GistApiClient
                .GetFileAsync(Token(config), config.GistId, GistFileName, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NoRemoteFile"));
            }
            return await ApplyRemoteAsync(
                    config,
                    ParseEnvelope(content),
                    version,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>获取 Gist 的历史版本列表,并尽力补全前若干个版本的提交来源设备名。</summary>
    public async Task<List<GistRevision>> GetRevisionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        SyncSettings config = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (Validate(config) is not null || string.IsNullOrEmpty(config.GistId))
        {
            return [];
        }
        string token = Token(config);
        List<GistRevision> revisions = await GistApiClient
            .GetCommitsAsync(token, config.GistId, cancellationToken)
            .ConfigureAwait(false);

        // 提交来源设备不在 commits API 中,而在各版本载荷信封的顶层 deviceName
        // (无论是否端到端加密都可读)。逐版本读取有额外请求开销:限 4 并发、
        // 只补前 20 个版本;单个失败不影响列表(设备名留空)。
        var enriched = new GistRevision[revisions.Count];
        using var throttle = new SemaphoreSlim(4);
        await Task.WhenAll(
                revisions.Select(
                    async (revision, index) =>
                    {
                        string? device = null;
                        if (index < 20)
                        {
                            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                (string? content, _) = await GistApiClient
                                    .GetFileAtRevisionAsync(
                                        token,
                                        config.GistId,
                                        revision.Version,
                                        GistFileName,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                                if (content is not null)
                                {
                                    using var doc = JsonDocument.Parse(content);
                                    if (
                                        doc.RootElement.TryGetProperty(
                                            "deviceName",
                                            out JsonElement name
                                        )
                                    )
                                    {
                                        device = name.GetString();
                                    }
                                }
                            }
                            catch
                            {
                                // 元数据补全失败不影响版本列表本身。
                            }
                            finally
                            {
                                throttle.Release();
                            }
                        }
                        enriched[index] = revision with { DeviceName = device };
                    }
                )
            )
            .ConfigureAwait(false);
        return [.. enriched];
    }

    /// <summary>将指定历史版本的内容恢复为当前状态并重新推送,使版本链保持线性、任何一步都可再恢复。</summary>
    public async Task<SyncResult> RestoreRevisionAsync(
        string version,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Core.Services.IBackgroundActivityScope? activity = BeginSyncActivity("SetSync_RestoreRevision");
        try
        {
            SyncSettings config = await GetSyncSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Validate(config) is { } error)
            {
                return SyncResult.Fail(error);
            }
            if (string.IsNullOrEmpty(config.GistId))
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NoGistNoRestore"));
            }
            string token = Token(config);
            (string? content, _) = await GistApiClient
                .GetFileAtRevisionAsync(
                    token,
                    config.GistId,
                    version,
                    GistFileName,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (content is null)
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_RevisionNoFile"));
            }
            SyncResult applied = await ApplyRemoteAsync(
                    config,
                    ParseEnvelope(content),
                    remoteVersion: null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!applied.Success)
            {
                return applied;
            }

            // 恢复即把该历史内容作为最新状态重新推送:版本链保持线性,任何一步都可再恢复。
            config = await GetSyncSettingsAsync(cancellationToken).ConfigureAwait(false);
            SyncResult pushed = await PushCoreAsync(config, token, cancellationToken)
                .ConfigureAwait(false);
            return pushed.Success
                ? new(
                    SyncAction.Pulled,
                    true,
                    Strings.Format(
                        "SyncSvc_RestoredAndPushed",
                        version[..Math.Min(7, version.Length)]
                    )
                )
                : pushed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SyncResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ———— 推送 ————

    private async Task<SyncResult> PushCoreAsync(
        SyncSettings config,
        string token,
        CancellationToken cancellationToken
    )
    {
        string content = await BuildGistContentAsync(config, cancellationToken)
            .ConfigureAwait(false);
        string version;
        if (string.IsNullOrEmpty(config.GistId))
        {
            (config.GistId, version) = await GistApiClient
                .CreateGistAsync(token, GistDescription, GistFileName, content, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            version = await GistApiClient
                .UpdateGistAsync(token, config.GistId, GistFileName, content, cancellationToken)
                .ConfigureAwait(false);
        }
        config.LastSyncAtUtc = DateTime.UtcNow;
        config.LastRemoteVersion = version;
        config.LastLocalChangeAtUtc = null;
        await SaveSyncSettingsAsync(config, cancellationToken).ConfigureAwait(false);
        return new(SyncAction.Pushed, true, Strings.Format("SyncSvc_Pushed", Short(version)));
    }

    private async Task<string> BuildGistContentAsync(
        SyncSettings config,
        CancellationToken cancellationToken
    )
    {
        bool encrypted = HasPassphrase(config);
        var payload = new SyncPayload
        {
            UpdatedAtUtc = DateTime.UtcNow,
            DeviceName = config.DeviceName,
        };
        if (config.SyncAppSettings)
        {
            AppSettings settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
            payload.Settings = ScrubDeviceLocalFields(settings);
        }
        if (config.SyncProfiles)
        {
            payload.Groups = await sessionRepository.GetAllGroupsAsync().ConfigureAwait(false);
            List<SessionProfile> profiles = await sessionRepository
                .GetAllSessionsAsync()
                .ConfigureAwait(false);
            if (!encrypted)
            {
                // 未启用端到端口令:凭据绝不明文上云(secret Gist 仅是“不被检索”,知道链接即可读)。
                foreach (SessionProfile profile in profiles)
                {
                    profile.Password = null;
                    profile.PrivateKeyPassphrase = null;
                }
            }
            payload.Profiles = profiles;

            // 端口转发隧道配置随连接同步(tunnels 集合按 profileId 分文档)。
            var tunnels = new Dictionary<Guid, List<TunnelConfig>>();
            foreach (SessionProfile profile in profiles)
            {
                List<TunnelConfig>? configs = await appDataStore
                    .GetAsync<List<TunnelConfig>>(
                        TunnelsCollection,
                        profile.Id.ToString("D"),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (configs is { Count: > 0 })
                {
                    tunnels[profile.Id] = configs;
                }
            }
            payload.Tunnels = tunnels.Count > 0 ? tunnels : null;
        }
        if (config.SyncSnippets)
        {
            payload.Snippets = await quickCommandRepository
                .ExportSyncAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        var envelope = new SyncEnvelope
        {
            UpdatedAtUtc = payload.UpdatedAtUtc,
            DeviceName = config.DeviceName,
            Encrypted = encrypted,
        };
        if (encrypted)
        {
            envelope.CipherText = SyncCrypto.Encrypt(
                JsonSerializer.Serialize(payload, JsonOptions),
                Passphrase(config)!
            );
        }
        else
        {
            envelope.Payload = payload;
        }
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    // ———— 拉取与应用 ————

    internal async Task<SyncResult> ApplyRemoteAsync(
        SyncSettings config,
        SyncEnvelope envelope,
        string? remoteVersion,
        CancellationToken cancellationToken
    )
    {
        if (envelope.SchemaVersion > CurrentSchemaVersion)
        {
            return SyncResult.Fail(
                $"Remote sync version {envelope.SchemaVersion} is newer than supported version {CurrentSchemaVersion}."
            );
        }
        SyncPayload payload;
        if (envelope.Encrypted)
        {
            string? passphrase = Passphrase(config);
            if (string.IsNullOrEmpty(passphrase))
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_NeedPassphrase"));
            }
            try
            {
                payload =
                    JsonSerializer.Deserialize<SyncPayload>(
                        SyncCrypto.Decrypt(envelope.CipherText ?? "", passphrase),
                        JsonOptions
                    ) ?? throw new InvalidOperationException(Strings.Get("SyncSvc_EmptyPayload"));
            }
            catch (CryptographicException)
            {
                return SyncResult.Fail(Strings.Get("SyncSvc_WrongPassphrase"));
            }
        }
        else
        {
            payload =
                envelope.Payload
                ?? throw new InvalidOperationException(Strings.Get("SyncSvc_MissingPayload"));
        }
        if (payload.SchemaVersion > CurrentSchemaVersion)
        {
            return SyncResult.Fail(
                $"Remote sync payload version {payload.SchemaVersion} is newer than supported version {CurrentSchemaVersion}."
            );
        }

        IsApplyingRemote = true;
        try
        {
            if (config.SyncAppSettings && payload.Settings is not null)
            {
                // 设备本地字段(窗口尺寸/开机自启/本机路径等)保留本机现值,其余采用云端。
                AppSettings local = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                AppSettings incoming = payload.Settings;
                PreserveDeviceLocalFields(incoming, local);
                incoming.Normalize();
                await settingsService.SaveSettingsAsync(incoming).ConfigureAwait(false);
            }
            if (config.SyncProfiles)
            {
                // upsert 合并:云端条目按 Id 覆盖/新增,本地独有的连接不删除(防误删)。
                foreach (ServerGroup group in payload.Groups ?? [])
                {
                    await sessionRepository.SaveGroupAsync(group).ConfigureAwait(false);
                }
                if (payload.Profiles is { } profiles)
                {
                    var localProfiles = (
                        await sessionRepository.GetAllSessionsAsync().ConfigureAwait(false)
                    ).ToDictionary(p => p.Id);
                    foreach (SessionProfile incoming in profiles)
                    {
                        // 非加密载荷不带凭据:保留本机已存的密码/私钥口令,避免拉取把凭据抹掉。
                        if (
                            incoming.Password is null
                            && localProfiles.TryGetValue(incoming.Id, out SessionProfile? existing)
                        )
                        {
                            incoming.Password = existing.Password;
                            incoming.PrivateKeyPassphrase ??= existing.PrivateKeyPassphrase;
                        }
                        await sessionRepository.SaveSessionAsync(incoming).ConfigureAwait(false);
                    }
                }

                // 隧道配置:按 profileId 整文档覆盖(文档本身即“该服务器的隧道列表”)。
                foreach ((Guid profileId, List<TunnelConfig> configs) in payload.Tunnels ?? [])
                {
                    await appDataStore
                        .UpsertAsync(
                            TunnelsCollection,
                            profileId.ToString("D"),
                            configs,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }
            if (config.SyncSnippets && payload.Snippets is not null)
            {
                await quickCommandRepository
                    .ApplySyncAsync(payload.Snippets, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            IsApplyingRemote = false;
        }
        config.LastSyncAtUtc = DateTime.UtcNow;
        if (remoteVersion is not null)
        {
            config.LastRemoteVersion = remoteVersion;
            config.LastLocalChangeAtUtc = null;
        }
        await SaveSyncSettingsAsync(config, cancellationToken).ConfigureAwait(false);
        if (config.SyncProfiles)
        {
            // 仓储已经完整应用远端分组/Profile/隧道后再通知界面。同步通常在后台线程执行,
            // 订阅方负责封送到自己的 UI 线程。历史版本恢复也复用本路径,因此无需另行接线。
            ProfilesApplied?.Invoke(this, EventArgs.Empty);
        }
        return new(
            SyncAction.Pulled,
            true,
            Strings.Format(
                "SyncSvc_Applied",
                payload.DeviceName,
                payload.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            )
        );
    }

    // ———— 设备本地字段(不参与同步) ————

    /// <summary>推送前清空设备本地字段:本机路径含用户名等隐私,窗口状态跨设备无意义。</summary>
    private static AppSettings ScrubDeviceLocalFields(AppSettings settings)
    {
        var defaults = new AppSettings();
        settings.General.LaunchAtStartup = defaults.General.LaunchAtStartup;
        settings.General.LastOpenProfileIds = [];
        settings.Appearance.LastWindowWidth = defaults.Appearance.LastWindowWidth;
        settings.Appearance.LastWindowHeight = defaults.Appearance.LastWindowHeight;
        settings.Appearance.LastWindowMaximized = defaults.Appearance.LastWindowMaximized;
        // 硬件加速取决于这台机器的显卡与内存,跨设备同步只会把弱机的将就设置推给强机(反之亦然)。
        settings.Appearance.HardwareAcceleration = defaults.Appearance.HardwareAcceleration;
        settings.Transfer.LocalDownloadDirectory = defaults.Transfer.LocalDownloadDirectory;
        settings.Transfer.DefaultEditorPath = defaults.Transfer.DefaultEditorPath;
        settings.Transfer.LogDirectory = defaults.Transfer.LogDirectory;
        settings.Keys.DefaultKeyName = defaults.Keys.DefaultKeyName;
        return settings;
    }

    /// <summary>拉取应用时同样保留本机的设备本地字段,云端值不覆盖。</summary>
    private static void PreserveDeviceLocalFields(AppSettings incoming, AppSettings local)
    {
        incoming.General.LaunchAtStartup = local.General.LaunchAtStartup;
        incoming.General.LastOpenProfileIds = local.General.LastOpenProfileIds;
        incoming.Appearance.LastWindowWidth = local.Appearance.LastWindowWidth;
        incoming.Appearance.LastWindowHeight = local.Appearance.LastWindowHeight;
        incoming.Appearance.LastWindowMaximized = local.Appearance.LastWindowMaximized;
        incoming.Appearance.HardwareAcceleration = local.Appearance.HardwareAcceleration;
        incoming.Transfer.LocalDownloadDirectory = local.Transfer.LocalDownloadDirectory;
        incoming.Transfer.DefaultEditorPath = local.Transfer.DefaultEditorPath;
        incoming.Transfer.LogDirectory = local.Transfer.LogDirectory;
        incoming.Keys.DefaultKeyName = local.Keys.DefaultKeyName;
    }

    // ———— 小工具 ————

    private string? Validate(SyncSettings config)
    {
        if (!config.Enabled)
        {
            return Strings.Get("SyncSvc_NotEnabled");
        }
        return HasToken(config) ? null : Strings.Get("SyncSvc_NoToken");
    }

    private string Token(SyncSettings config) =>
        secretProtector.Unprotect(config.ProtectedToken)
        ?? throw new InvalidOperationException(Strings.Get("SyncSvc_TokenDecryptFailed"));

    private string? Passphrase(SyncSettings config) =>
        string.IsNullOrEmpty(config.ProtectedPassphrase)
            ? null
            : secretProtector.Unprotect(config.ProtectedPassphrase);

    private static SyncEnvelope ParseEnvelope(string content) =>
        JsonSerializer.Deserialize<SyncEnvelope>(content, JsonOptions)
        ?? throw new InvalidOperationException(Strings.Get("SyncSvc_BadFormat"));

    private static string Short(string version) => version.Length <= 7 ? version : version[..7];
}
