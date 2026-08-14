using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 传输进度上报。
/// <para>
/// 这里**不做节流** —— 宿主侧已经按 ≥100ms 收敛并处理了并发乱序下的单调性
/// (见宿主的 <c>PluginProtocolFileService.ProgressBridge</c>)。插件重复做一遍不但多余,
/// 还会让两层的"最后一次上报"互相盖掉。本类只负责一件宿主替不了的事:
/// **并发分片各报各的增量时,把它们累加成一个全局已传字节数**。
/// </para>
/// </summary>
/// <param name="sink">进度接收方;为 null 时整条链路短路。</param>
/// <param name="fileName">文件名。宿主从路径自己取,这里只为保持调用点可读。</param>
/// <param name="totalBytes">总字节数。</param>
internal sealed class S3ProgressReporter(IProgress<RemoteTransferProgress>? sink, string fileName, long totalBytes)
{
    private long _transferred;

    /// <summary>是否需要上报(sink 为空时全链路短路)。</summary>
    public bool IsEnabled => sink is not null;

    /// <summary>展示用的文件名。</summary>
    public string FileName => fileName;

    /// <summary>上报累计已传字节数(取单调最大值,避免乱序回调让进度条回退)。</summary>
    /// <param name="bytesTransferred">累计已传字节数。</param>
    public void Report(long bytesTransferred)
    {
        if (sink is null)
        {
            return;
        }
        sink.Report(new(Monotonic(bytesTransferred), totalBytes));
    }

    /// <summary>在已传字节数上累加一个增量后上报(并发分片各只知道自己传了多少)。</summary>
    /// <param name="delta">本次新增的字节数。</param>
    public void Advance(long delta)
    {
        if (sink is null || delta <= 0)
        {
            return;
        }
        sink.Report(new(Interlocked.Add(ref _transferred, delta), totalBytes));
    }

    /// <summary>收尾上报:必须有这一次,否则进度会停在最后一个回调的值上。</summary>
    /// <param name="bytesTransferred">最终已传字节数。</param>
    public void ReportFinal(long bytesTransferred) => Report(bytesTransferred);

    private long Monotonic(long bytesTransferred)
    {
        long current = Volatile.Read(ref _transferred);
        while (bytesTransferred > current)
        {
            long previous = Interlocked.CompareExchange(ref _transferred, bytesTransferred, current);
            if (previous == current)
            {
                return bytesTransferred;
            }
            current = previous;
        }
        return current;
    }
}
