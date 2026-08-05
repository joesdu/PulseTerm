namespace VelaShell.Services.Update;

/// <summary>更新源抽象:提供最新版本清单与产物下载。便于测试替换真实的 GitHub 源。</summary>
public interface IUpdateSource
{
    /// <summary>
    /// 获取最新发布的清单。<paramref name="includePreRelease" /> 为 true(preview 通道)时预发布版
    /// 也纳入;源上没有任何可用发布时返回 null。
    /// </summary>
    Task<UpdateManifest?> GetLatestManifestAsync(bool includePreRelease, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把 <paramref name="asset" /> 下载到 <paramref name="destinationPath" />,按百分比汇报进度。
    /// 返回下载过程中边收边算出的整包 SHA-256(十六进制小写),调用方可据此免去
    /// 落盘后重读校验;实现无法流式计算时返回 null,由调用方读文件校验兜底。
    /// </summary>
    Task<string?> DownloadAssetAsync(
        UpdateManifest manifest,
        UpdateAsset asset,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
