namespace VelaShell.Core.Sync;

/// <summary>
/// 基于 GitHub Gist 的多端同步:用户数据打包为单文件存入 secret Gist,
/// 版本管理直接复用 Gist 原生 revision 历史;可选端到端口令加密。
/// </summary>
public interface IGistSyncService
{
    /// <summary>
    /// 远端连接配置已成功应用到本地仓储后触发。订阅方可据此刷新会话树等内存视图;
    /// 事件可能从后台线程触发。
    /// </summary>
    event EventHandler? ProfilesApplied;

    /// <summary>正在把远端数据应用到本地(此期间的本地保存事件不算“本地改动”,防止拉取后立刻回推)。</summary>
    bool IsApplyingRemote { get; }

    /// <summary>读取当前同步配置。</summary>
    Task<SyncSettings> GetSyncSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>持久化同步配置。</summary>
    Task SaveSyncSettingsAsync(SyncSettings settings, CancellationToken cancellationToken = default);

    /// <summary>用明文令牌更新配置(空/null = 保留已存令牌);内部经 ISecretProtector 加密。</summary>
    void ApplyToken(SyncSettings settings, string? plainToken);

    /// <summary>用明文口令更新配置(null = 保留;空串 = 清除口令,退回非加密同步)。</summary>
    void ApplyPassphrase(SyncSettings settings, string? plainPassphrase);

    /// <summary>是否已保存令牌(界面据此显示“已配置”而不回显明文)。</summary>
    bool HasToken(SyncSettings settings);

    /// <summary>是否已设置端到端加密口令。</summary>
    bool HasPassphrase(SyncSettings settings);

    /// <summary>记录“本地有未同步改动”(设置/数据保存后由宿主调用);应用远端期间自动忽略。</summary>
    Task MarkLocalChangedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 智能同步:比对本地改动标记与远端 revision,自动决定推送、拉取或无操作;
    /// 双端都有改动时按“较新者胜”,被覆盖的一方仍可从版本历史恢复。
    /// </summary>
    Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default);

    /// <summary>强制推送本地数据到云端(Gist 不存在时自动创建并回填 GistId)。</summary>
    Task<SyncResult> PushAsync(CancellationToken cancellationToken = default);

    /// <summary>强制拉取云端数据并应用到本地。</summary>
    Task<SyncResult> PullAsync(CancellationToken cancellationToken = default);

    /// <summary>列出云端版本历史(Gist revisions,新→旧)。</summary>
    Task<List<GistRevision>> GetRevisionsAsync(CancellationToken cancellationToken = default);

    /// <summary>恢复到指定历史版本:取该版本内容应用到本地,并作为新版本推送(历史不丢)。</summary>
    Task<SyncResult> RestoreRevisionAsync(string version, CancellationToken cancellationToken = default);
}
