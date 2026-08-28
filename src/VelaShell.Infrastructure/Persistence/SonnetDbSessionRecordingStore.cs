using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using VelaShell.Core.Recording;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 会话录制存储:元数据存文档集合 <c>recordings</c>(Id 为文档键),
/// 输出块存时序 measurement <c>session_recording_chunks</c>
/// (时间 = 录制开始时刻 + offset_ms,tag = recording_id)。
/// </summary>
public sealed class SonnetDbSessionRecordingStore(SonnetDbEngine engine) : ISessionRecordingStore
{
    /// <summary>过期数据攒到这个量,启动清理才顺带做一次真正的空间回收(重建要搬两趟存活数据)。</summary>
    private const long AutoReclaimThresholdBytes = 64L * 1024 * 1024;

    /// <summary>回收时逐页读取数据块的页大小(行)。单块上限 64KB,一页约 16MB 封顶。</summary>
    private const int ChunkPageRows = 256;

    /// <summary>
    /// 回灌时单批攒够多少字节就落库并刷盘一次。写进去的点会赖在内存表里等自动刷盘
    /// (默认攒满 100 万点或 5 分钟),搬运几 GB 就等于把几 GB 顶进内存 —— 正是这次清理
    /// 要解决的毛病。实测搬运 1.2GB:完全不主动刷盘峰值 3.6GB 托管堆,按 8MB 一批刷则峰值
    /// 减半至 1.4GB、且刚写完只留 81MB(峰值来自随后的后台压实,约一分钟内自行落回)。
    /// </summary>
    private const long RestoreBatchBytes = 8L * 1024 * 1024;

    /// <summary>暂存文件的读写缓冲。</summary>
    private const int SpoolBufferBytes = 1 << 20;

    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>保存(新增或覆盖)一条会话录制的元数据文档。</summary>
    public async Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        string json = SonnetDbJson.Serialize(recording);
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.RecordingsCollection, store =>
        {
            store.Upsert(DocId(recording.Id), json);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 追加一个录制输出块:以「录制开始时刻 + <paramref name="offsetMs" />」为时间点、
    /// 录制 Id 为 tag,将 Base64 编码的数据写入时序 measurement。
    /// </summary>
    public Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var tags = new Dictionary<string, string> { ["recording_id"] = recordingId.ToString("D") };
        var fields = new Dictionary<string, FieldValue>
        {
            ["offset_ms"] = FieldValue.FromLong(offsetMs),
            ["data"] = FieldValue.FromString(Convert.ToBase64String(data))
        };
        return _engine.WritePointAsync(SonnetDbEngine.RecordingChunksMeasurement,
            ChunkTimestamp(startedAtUtc, offsetMs), tags, fields, cancellationToken);
    }

    /// <summary>数据块的写入时刻 = 录制开始时刻 + 偏移(回放时间轴即由此还原)。</summary>
    private static DateTimeOffset ChunkTimestamp(DateTime startedAtUtc, long offsetMs) =>
        new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc)).AddMilliseconds(offsetMs);

    /// <summary>构造一个数据块数据点(回灌批量写入用,与 <see cref="AppendChunkAsync" /> 同构)。</summary>
    private static Point BuildChunkPoint(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data) =>
        Point.Create(SonnetDbEngine.RecordingChunksMeasurement,
            ChunkTimestamp(startedAtUtc, offsetMs).ToUnixTimeMilliseconds(),
            new Dictionary<string, string> { ["recording_id"] = recordingId.ToString("D") },
            new Dictionary<string, FieldValue>
            {
                ["offset_ms"] = FieldValue.FromLong(offsetMs),
                ["data"] = FieldValue.FromString(Convert.ToBase64String(data))
            });

    /// <summary>列出所有会话录制元数据,按开始时间倒序排列。</summary>
    public async Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default)
    {
        List<SessionRecording?> rows = await _engine.WithCollectionAsync(SonnetDbEngine.RecordingsCollection, store =>
                                               store.Scan().Select(row => SonnetDbJson.Deserialize<SessionRecording>(row.Json)).ToList(),
                                           cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(r => r is not null).Cast<SessionRecording>().OrderByDescending(r => r.StartedAtUtc)];
    }

    /// <summary>
    /// 读取指定录制的全部输出块并按偏移量升序返回;单块 Base64 解码失败时跳过该块,不影响其余回放。
    /// </summary>
    public async Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        // SonnetDB 方言要求 ORDER BY time 时 SELECT 列表必须包含 time 列。
        SelectExecutionResult result = await _engine.QueryAsync(
                                           $"SELECT time, offset_ms, data FROM {SonnetDbEngine.RecordingChunksMeasurement} " +
                                           $"WHERE recording_id = '{recordingId:D}' ORDER BY time ASC LIMIT 1000000",
                                           cancellationToken).ConfigureAwait(false);
        int offsetIndex = IndexOf(result, "offset_ms");
        int dataIndex = IndexOf(result, "data");
        var chunks = new List<RecordingChunk>(result.Rows.Count);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            if (DecodeChunk(row, offsetIndex, dataIndex) is { } chunk)
            {
                chunks.Add(chunk);
            }
        }
        chunks.Sort((a, b) => a.OffsetMs.CompareTo(b.OffsetMs));
        return chunks;
    }

    /// <summary>
    /// 删除指定录制:先删元数据,再尽力删除其数据块;数据块删除失败时留作孤儿,
    /// 待后续清理的压缩重建路径统一回收。
    /// </summary>
    public async Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        await DeleteMetadataAsync(recordingId, cancellationToken).ConfigureAwait(false);

        // 数据块尽力删除:SQL 方言不支持 DELETE 时留作不可见孤儿
        // (元数据已删,列表/回放均不可达);孤儿字节由 CleanupExpiredAsync
        // 的压缩重建路径在下次启动清理时统一回收。
        await TryDeleteChunksAsync(recordingId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 清理超过保留天数的过期录制。删除只写墓碑不腾空间,因此过期数据体积可观时
    /// (超过 <see cref="AutoReclaimThresholdBytes" />)顺带跑一次真正的空间回收。
    /// </summary>
    public async Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays < 1)
        {
            return;
        }
        DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        List<SessionRecording> all = await ListRecordingsAsync(cancellationToken).ConfigureAwait(false);
        List<SessionRecording> expired = [.. all.Where(r => r.StartedAtUtc < cutoff)];
        if (expired.Count == 0)
        {
            return;
        }

        // 过期数据够大才做重建:重建要把存活数据搬两趟,为了几 MB 的过期块不值当,
        // 留给用户手动清理或下一轮攒够量再说。
        if (expired.Sum(r => r.ByteSize) >= AutoReclaimThresholdBytes)
        {
            await ReclaimSpaceAsync(retentionDays, cancellationToken).ConfigureAwait(false);
            return;
        }
        foreach (SessionRecording recording in expired)
        {
            await DeleteRecordingAsync(recording.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>录制数据的占用快照(条数 / 逻辑体积 / 数据库目录磁盘占用)。</summary>
    public async Task<RecordingStorageUsage> GetStorageUsageAsync(CancellationToken cancellationToken = default)
    {
        List<SessionRecording> all = await ListRecordingsAsync(cancellationToken).ConfigureAwait(false);
        return new(all.Count, all.Sum(r => r.ByteSize), MeasureDiskBytes());
    }

    /// <summary>
    /// 真正腾空间的清理。时序库的 DELETE 只写墓碑,唯一能把字节还给磁盘的是整块 drop 掉
    /// measurement,所以走"存活数据落磁盘暂存 → drop 重建 → 回灌"三步:峰值内存只与单个
    /// 数据块有关,与录制总量无关(几 GB 录制也不会把内存顶起来)。
    /// </summary>
    /// <param name="keepDays">
    /// 保留窗口(天)。<c>0</c> = 一条不留(跳过暂存,直接 drop 重建);
    /// <see cref="int.MaxValue" /> = 全部保留,只回收已删除录制留下的孤儿字节。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<RecordingCleanupResult> ReclaimSpaceAsync(int keepDays, CancellationToken cancellationToken = default)
    {
        long diskBefore = MeasureDiskBytes();
        List<SessionRecording> all = await ListRecordingsAsync(cancellationToken).ConfigureAwait(false);
        List<SessionRecording> survivors = keepDays switch
        {
            <= 0 => [],
            int.MaxValue => all,
            _ => [.. all.Where(r => r.StartedAtUtc >= DateTime.UtcNow.AddDays(-keepDays))]
        };
        var survivorIds = survivors.Select(r => r.Id).ToHashSet();

        string? spoolPath = null;
        try
        {
            // 1) 存活数据块先流到磁盘暂存文件(边查边写,内存里只留当前一页)。
            if (survivors.Count > 0)
            {
                spoolPath = Path.Combine(Path.GetTempPath(), $"velashell-recspool-{Guid.NewGuid():N}.bin");
                await SpoolSurvivorsAsync(survivors, spoolPath, cancellationToken).ConfigureAwait(false);
            }

            // 2) drop 重建 measurement:墓碑、孤儿块与全部历史字节一起没。
            await _engine.ResetMeasurementAsync(SonnetDbEngine.RecordingChunksMeasurement, cancellationToken).ConfigureAwait(false);

            // 3) 回灌存活数据。
            if (spoolPath is not null)
            {
                await RestoreSpoolAsync(spoolPath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDeleteFile(spoolPath);
        }

        // 元数据收尾:被清掉的录制不能再留在列表里。
        foreach (SessionRecording recording in all.Where(r => !survivorIds.Contains(r.Id)))
        {
            await DeleteMetadataAsync(recording.Id, cancellationToken).ConfigureAwait(false);
        }

        long diskAfter = MeasureDiskBytes();

        // 段文件走内存映射后,Windows 上 drop 掉的文件在本进程退出前删不掉 —— SonnetDB 会在
        // 下次开库时把它们清掉。此时磁盘没降,得如实告诉用户"重启后彻底释放"。
        return new(all.Count - survivors.Count, diskBefore, diskAfter, diskAfter >= diskBefore);
    }

    /// <summary>把存活录制的数据块逐页写入暂存文件(每条记录:录制 Id、起始时刻、偏移、数据)。</summary>
    private async Task SpoolSurvivorsAsync(List<SessionRecording> survivors, string spoolPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            SpoolBufferBytes, FileOptions.SequentialScan);
        await using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        foreach (SessionRecording recording in survivors)
        {
            await foreach (RecordingChunk chunk in ReadChunksPagedAsync(recording.Id, cancellationToken).ConfigureAwait(false))
            {
                writer.Write(recording.Id.ToByteArray());
                writer.Write(DateTime.SpecifyKind(recording.StartedAtUtc, DateTimeKind.Utc).Ticks);
                writer.Write(chunk.OffsetMs);
                writer.Write(chunk.Data.Length);
                writer.Write(chunk.Data);
            }
        }
    }

    /// <summary>把暂存文件回灌进重建后的 measurement,按批写入以摊薄引擎加锁开销。</summary>
    private async Task RestoreSpoolAsync(string spoolPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(spoolPath, FileMode.Open, FileAccess.Read, FileShare.None,
            SpoolBufferBytes, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        var batch = new List<Point>();
        long batchBytes = 0;
        byte[] guidBuffer = new byte[16];
        while (stream.Position < stream.Length)
        {
            if (reader.Read(guidBuffer, 0, 16) != 16)
            {
                break;
            }
            var id = new Guid(guidBuffer);
            var startedAtUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            long offsetMs = reader.ReadInt64();
            byte[] data = reader.ReadBytes(reader.ReadInt32());
            batch.Add(BuildChunkPoint(id, startedAtUtc, offsetMs, data));
            batchBytes += data.Length;
            if (batchBytes < RestoreBatchBytes)
            {
                continue;
            }
            await WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            batchBytes = 0;
        }
        await WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>落一批数据点并立刻刷盘,随后清空批次(刷盘的必要性见 <see cref="RestoreBatchBytes" />)。</summary>
    private async Task WriteBatchAsync(List<Point> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }
        await _engine.WriteManyAsync(batch, cancellationToken).ConfigureAwait(false);
        await _engine.FlushAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    /// <summary>
    /// 按 time 游标逐页读取一条录制的数据块。同一序列同一时刻只存得下一个点(后写覆盖先写),
    /// 因此"下一页从 lastTime + 1 起"既不漏也不重 —— 实测 500 块在 7/64/1000 三种页大小下
    /// 都是精确等价的时间升序全集。
    /// </summary>
    private async IAsyncEnumerable<RecordingChunk> ReadChunksPagedAsync(Guid recordingId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long cursor = long.MinValue;
        while (true)
        {
            var parameters = new SqlParameters();
            parameters.AddNamed("rid", recordingId.ToString("D"));
            string cursorClause = "";
            if (cursor != long.MinValue)
            {
                cursorClause = " AND time >= @cursor";
                parameters.AddNamed("cursor", cursor);
            }
            SelectExecutionResult result = await _engine.QueryAsync(
                                               $"SELECT time, offset_ms, data FROM {SonnetDbEngine.RecordingChunksMeasurement} " +
                                               $"WHERE recording_id = @rid{cursorClause} LIMIT {ChunkPageRows}",
                                               parameters, cancellationToken).ConfigureAwait(false);
            if (result.Rows.Count == 0)
            {
                yield break;
            }
            int timeIndex = IndexOf(result, "time");
            int offsetIndex = IndexOf(result, "offset_ms");
            int dataIndex = IndexOf(result, "data");
            long lastTime = cursor;
            foreach (IReadOnlyList<object?> row in result.Rows)
            {
                if (timeIndex < row.Count)
                {
                    lastTime = Convert.ToInt64(row[timeIndex] ?? 0L);
                }
                if (DecodeChunk(row, offsetIndex, dataIndex) is { } chunk)
                {
                    yield return chunk;
                }
            }

            // 不足一页 = 已到末尾;时间戳没往前走则说明再翻也是同一页,就此打住防死循环。
            if (result.Rows.Count < ChunkPageRows || lastTime == long.MinValue || lastTime + 1 <= cursor)
            {
                yield break;
            }
            cursor = lastTime + 1;
        }
    }

    /// <summary>数据库目录的磁盘占用;目录被并发改动时按已统计到的部分返回。</summary>
    private long MeasureDiskBytes()
    {
        try
        {
            return new DirectoryInfo(_engine.RootDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暂存文件在临时目录,删不掉交给系统清理。
        }
    }

    private static RecordingChunk? DecodeChunk(IReadOnlyList<object?> row, int offsetIndex, int dataIndex)
    {
        if (offsetIndex >= row.Count || dataIndex >= row.Count)
        {
            return null;
        }
        string? base64 = row[dataIndex]?.ToString();
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }
        try
        {
            return new(Convert.ToInt64(row[offsetIndex] ?? 0L), Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            // 单块损坏跳过,不影响其余数据。
            return null;
        }
    }

    private Task<object?> DeleteMetadataAsync(Guid recordingId, CancellationToken cancellationToken) =>
        _engine.WithCollectionAsync<object?>(SonnetDbEngine.RecordingsCollection, store =>
        {
            store.Delete(DocId(recordingId));
            return null;
        }, cancellationToken);

    private Task<bool> TryDeleteChunksAsync(Guid recordingId, CancellationToken cancellationToken) =>
        _engine.TryExecuteAsync(
            $"DELETE FROM {SonnetDbEngine.RecordingChunksMeasurement} WHERE recording_id = '{recordingId:D}'",
            cancellationToken);

    private static string DocId(Guid id) => id.ToString("D");

    private static int IndexOf(SelectExecutionResult result, string column)
    {
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return int.MaxValue;
    }
}
