namespace VelaShell.Services;

/// <summary>应用自动更新服务:检查、下载并应用新版本。</summary>
public interface IUpdateService
{
    /// <summary>当前正在运行的应用版本;无法确定时为 null。</summary>
    string? CurrentVersion { get; }

    /// <summary>检查后发现的可用新版本;无可用更新时为 null。</summary>
    string? AvailableVersion { get; }

    /// <summary>
    /// 能否原地自更新:应用目录可写且平台受支持时为 true。装在 Program Files 等
    /// 只读位置时为 false,发现新版本后只能提示用户手动下载。
    /// </summary>
    bool CanSelfUpdate { get; }

    /// <summary>检查是否有可用更新;返回 true 表示存在比当前版本更新的版本。</summary>
    Task<bool> CheckForUpdateAsync();

    /// <summary>
    /// 上一次换版失败的原因;没有失败记录时为 null。换版由独立进程在本应用退出后执行,
    /// 它的报错没法当场弹给用户,故落盘留到下次启动后展示。
    /// </summary>
    string? LastUpdateError { get; }

    /// <summary>清掉 <see cref="LastUpdateError" /> 的失败记录(用户重新尝试更新时调用)。</summary>
    void ClearUpdateError();

    /// <summary>下载已检测到的更新包并校验完整性,可通过 <paramref name="progress"/> 汇报下载进度百分比。</summary>
    Task DownloadUpdateAsync(IProgress<int>? progress = null);

    /// <summary>应用已下载的更新并重启应用。</summary>
    void ApplyUpdateAndRestart();

    /// <summary>
    /// 强制重置更新状态:回滚没换完的换版,清掉暂存目录(含已下载的包,下次重新下载)、
    /// 更新器临时目录与历史遗留文件。返回 false 表示仍有文件被占用,关掉占用者后可再试。
    /// 用于把因意外中断而卡住的更新流程恢复到干净起点。
    /// </summary>
    bool RepairUpdateState();
}
