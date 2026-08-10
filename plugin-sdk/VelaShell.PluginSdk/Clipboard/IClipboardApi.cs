namespace VelaShell.PluginSdk.Clipboard;

/// <summary>
/// 系统剪贴板能力(文本)。隔离模式下经宿主执行 —— 与进程内语义一致。
/// 剪贴板常含用户密码,读取内容**不要记日志、不要外发**。
/// </summary>
public interface IClipboardApi
{
    /// <summary>读取剪贴板文本;为空或非文本时返回 <see langword="null" />。</summary>
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    /// <summary>把文本写入剪贴板。</summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}
