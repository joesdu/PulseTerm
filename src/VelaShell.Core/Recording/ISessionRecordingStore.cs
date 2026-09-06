namespace VelaShell.Core.Recording;

/// <summary>一次会话录制的元数据(文档集合 recordings,Id 为文档键)。</summary>
public class SessionRecording
{
    /// <summary>录制唯一标识(文档键)。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>会话展示名(标签标题,如服务器名)。</summary>
    public string SessionLabel { get; set; } = string.Empty;

    /// <summary>录制开始时刻(UTC)。</summary>
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>null = 录制中(或应用异常退出未收尾)。</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>录制输出的累计字节数。</summary>
    public long ByteSize { get; set; }

    /// <summary>录制包含的数据块数量。</summary>
    public int ChunkCount { get; set; }

    /// <summary>录制时长(毫秒);以最后一块输出的偏移为准。</summary>
    public long DurationMs { get; set; }

    /// <summary>录制开始时终端的列数。</summary>
    /// <remarks>
    /// 导出 asciicast 时要写进头部,播放器据此决定画布宽度。此前这里是写死的 120×32,
    /// 于是任何非该尺寸的会话导出后回放都会错行。<see cref="DefaultColumns" /> 只作为
    /// 本字段出现之前录下的老数据的兜底值。
    /// <para>
    /// 会话中途的 resize 目前不入录:那需要在数据块之外再开一条事件流,
    /// 属于独立改动。头部尺寸取的是录制开始的那一刻。
    /// </para>
    /// </remarks>
    public int Columns { get; set; } = DefaultColumns;

    /// <summary>录制开始时终端的行数。见 <see cref="Columns" />。</summary>
    public int Rows { get; set; } = DefaultRows;

    /// <summary>尺寸缺失(本字段出现之前的老录制)时使用的列数。</summary>
    public const int DefaultColumns = 120;

    /// <summary>尺寸缺失(本字段出现之前的老录制)时使用的行数。</summary>
    public const int DefaultRows = 32;
}

/// <summary>一块录制数据:相对录制开始的毫秒偏移 + 原始终端输出字节。</summary>
/// <param name="OffsetMs">相对录制开始时刻的毫秒偏移。</param>
/// <param name="Data">该时刻的原始终端输出字节。</param>
public sealed record RecordingChunk(long OffsetMs, byte[] Data);

/// <summary>
/// 录制数据的占用快照(回放中心"清理"入口的决策依据)。
/// </summary>
/// <param name="RecordingCount">现存录制条数。</param>
/// <param name="LiveBytes">现存录制的逻辑字节数(各条元数据 ByteSize 之和)。</param>
/// <param name="DiskBytes">数据库目录在磁盘上的实际占用。</param>
public readonly record struct RecordingStorageUsage(int RecordingCount, long LiveBytes, long DiskBytes)
{
    /// <summary>
    /// 预计可回收的字节数:磁盘占用减去现存录制的逻辑体积。删除只写墓碑不腾空间,
    /// 差额即"已删除但仍占着盘"的孤儿数据(也含审计日志等其它时序数据,故只是估算)。
    /// </summary>
    public long ReclaimableBytes => Math.Max(0, DiskBytes - LiveBytes);
}

/// <summary>
/// 一次清理的结果。
/// </summary>
/// <param name="RemovedRecordings">被删除的录制条数。</param>
/// <param name="DiskBytesBefore">清理前的数据库磁盘占用。</param>
/// <param name="DiskBytesAfter">清理后的数据库磁盘占用(受内存映射影响,可能要下次启动才降下来)。</param>
/// <param name="DeferredToRestart">段文件仍被内存映射占用,字节数要等下次启动才真正释放。</param>
public readonly record struct RecordingCleanupResult(
    int RemovedRecordings,
    long DiskBytesBefore,
    long DiskBytesAfter,
    bool DeferredToRestart);

/// <summary>
/// 会话录制存储(设置 → 安全审计 → 会话录制):
/// 元数据存文档集合,输出块存 SonnetDB 时序 measurement(时间 = 开始时刻 + 偏移)。
/// </summary>
public interface ISessionRecordingStore
{
    /// <summary>写入/更新录制元数据(开始时创建,结束与周期刷新时更新)。</summary>
    Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default);

    /// <summary>追加一块输出数据。</summary>
    Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>全部录制,按开始时间倒序。</summary>
    Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default);

    /// <summary>某次录制的全部数据块,按偏移升序(回放输入)。</summary>
    Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId, CancellationToken cancellationToken = default);

    /// <summary>删除一次录制(元数据 + 数据块)。</summary>
    Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default);

    /// <summary>清理超过保留天数的录制(随会话日志保留天数,启动时调用)。</summary>
    Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>录制数据的占用快照(条数 / 逻辑体积 / 磁盘占用)。</summary>
    Task<RecordingStorageUsage> GetStorageUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 真正腾空间的清理:只保留最近 <paramref name="keepDays" /> 天的录制,其余连同
    /// 历史删除留下的孤儿数据块一并抹掉。
    /// <para>
    /// 时序库的 DELETE 只写墓碑、不回收字节,唯一能腾出空间的是整块 drop 掉 measurement。
    /// 因此实现为"存活数据先落到磁盘暂存文件 → drop 重建 measurement → 回灌"三步:
    /// 内存占用只与单个数据块有关,与录制总量无关。
    /// </para>
    /// </summary>
    /// <param name="keepDays">
    /// 保留窗口(天)。<c>0</c> = 一条不留(最快,直接 drop 重建);
    /// <see cref="int.MaxValue" /> = 全部保留,只回收已删除录制占的空间。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<RecordingCleanupResult> ReclaimSpaceAsync(int keepDays, CancellationToken cancellationToken = default);
}
