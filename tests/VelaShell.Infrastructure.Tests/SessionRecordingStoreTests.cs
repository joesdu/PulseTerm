using System.Text;
using VelaShell.Core.Recording;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 会话录制存储的落库与清理。重点在"清理要真的腾空间":时序库的 DELETE 只写墓碑,
/// 唯一还字节的路径是 drop 重建 measurement,所以每个清理用例都得断言存活数据一字节不差地回来了。
/// </summary>
[TestClass]
public sealed class SessionRecordingStoreTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly SonnetDbSessionRecordingStore _store;
    private readonly string _testDirectory;

    public SessionRecordingStoreTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_rectest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _store = new(_engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task GetChunks_RoundTripsWrittenBytes()
    {
        SessionRecording recording = await WriteRecordingAsync("web-01", DateTime.UtcNow.AddHours(-1), 3);
        List<RecordingChunk> chunks = await _store.GetChunksAsync(recording.Id);
        Assert.HasCount(3, chunks);
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(i * 600L, chunks[i].OffsetMs);
            Assert.AreEqual(PayloadFor(recording.Id, i), Encoding.UTF8.GetString(chunks[i].Data));
        }
    }

    [TestMethod]
    public async Task ReclaimSpace_KeepAll_PreservesEveryChunkAndDropsOrphans()
    {
        SessionRecording kept = await WriteRecordingAsync("keep", DateTime.UtcNow.AddHours(-2), 5);
        SessionRecording deleted = await WriteRecordingAsync("deleted", DateTime.UtcNow.AddHours(-3), 5);

        // 常规删除:元数据没了,数据块只是被打了墓碑,字节还在盘上。
        await _store.DeleteRecordingAsync(deleted.Id);

        RecordingCleanupResult result = await _store.ReclaimSpaceAsync(int.MaxValue);

        Assert.AreEqual(0, result.RemovedRecordings, "全部保留时不该删掉任何一条现存录制");
        List<SessionRecording> remaining = await _store.ListRecordingsAsync();
        Assert.HasCount(1, remaining);
        Assert.AreEqual(kept.Id, remaining[0].Id);

        List<RecordingChunk> chunks = await _store.GetChunksAsync(kept.Id);
        Assert.HasCount(5, chunks, "存活录制的数据块必须原样回来");
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(i * 600L, chunks[i].OffsetMs);
            Assert.AreEqual(PayloadFor(kept.Id, i), Encoding.UTF8.GetString(chunks[i].Data));
        }
        Assert.IsEmpty(await _store.GetChunksAsync(deleted.Id), "已删除录制的孤儿数据块应随重建一并消失");
    }

    [TestMethod]
    public async Task ReclaimSpace_PurgeAll_LeavesNothingBehind()
    {
        SessionRecording first = await WriteRecordingAsync("a", DateTime.UtcNow.AddMinutes(-10), 4);
        SessionRecording second = await WriteRecordingAsync("b", DateTime.UtcNow.AddMinutes(-5), 4);

        RecordingCleanupResult result = await _store.ReclaimSpaceAsync(0);

        Assert.AreEqual(2, result.RemovedRecordings);
        Assert.IsEmpty(await _store.ListRecordingsAsync());
        Assert.IsEmpty(await _store.GetChunksAsync(first.Id));
        Assert.IsEmpty(await _store.GetChunksAsync(second.Id));
    }

    [TestMethod]
    public async Task ReclaimSpace_KeepDays_DropsOnlyRecordingsOutsideTheWindow()
    {
        SessionRecording fresh = await WriteRecordingAsync("fresh", DateTime.UtcNow.AddDays(-2), 3);
        SessionRecording stale = await WriteRecordingAsync("stale", DateTime.UtcNow.AddDays(-40), 3);

        RecordingCleanupResult result = await _store.ReclaimSpaceAsync(7);

        Assert.AreEqual(1, result.RemovedRecordings);
        List<SessionRecording> remaining = await _store.ListRecordingsAsync();
        Assert.HasCount(1, remaining);
        Assert.AreEqual(fresh.Id, remaining[0].Id);
        Assert.HasCount(3, await _store.GetChunksAsync(fresh.Id));
        Assert.IsEmpty(await _store.GetChunksAsync(stale.Id));
    }

    /// <summary>
    /// 回收时数据块按 time 游标逐页搬运(页大小 256 行),这里特意跨过两页多一点:
    /// 分页只要漏一块或重一块,回放就会缺一段或串一段,而条数断言最容易漏掉这种错。
    /// </summary>
    [TestMethod]
    public async Task ReclaimSpace_PreservesChunksAcrossPageBoundaries()
    {
        const int chunkCount = 600;
        SessionRecording recording = await WriteRecordingAsync("long-session", DateTime.UtcNow.AddHours(-4), chunkCount);

        await _store.ReclaimSpaceAsync(int.MaxValue);

        List<RecordingChunk> chunks = await _store.GetChunksAsync(recording.Id);
        Assert.HasCount(chunkCount, chunks, "跨页搬运不能漏块也不能重块");
        for (int i = 0; i < chunkCount; i++)
        {
            Assert.AreEqual(i * 600L, chunks[i].OffsetMs, $"第 {i} 块的偏移错位");
            Assert.AreEqual(PayloadFor(recording.Id, i), Encoding.UTF8.GetString(chunks[i].Data), $"第 {i} 块内容不符");
        }
    }

    [TestMethod]
    public async Task GetStorageUsage_ReportsCountAndLogicalBytes()
    {
        SessionRecording first = await WriteRecordingAsync("a", DateTime.UtcNow.AddMinutes(-9), 2);
        SessionRecording second = await WriteRecordingAsync("b", DateTime.UtcNow.AddMinutes(-8), 3);

        RecordingStorageUsage usage = await _store.GetStorageUsageAsync();

        Assert.AreEqual(2, usage.RecordingCount);
        Assert.AreEqual(first.ByteSize + second.ByteSize, usage.LiveBytes);
        Assert.IsGreaterThan(0, usage.DiskBytes, "数据库目录应有实际磁盘占用");
    }

    [TestMethod]
    public async Task CleanupExpired_RemovesExpiredAndKeepsRecent()
    {
        SessionRecording fresh = await WriteRecordingAsync("fresh", DateTime.UtcNow.AddDays(-1), 2);
        await WriteRecordingAsync("stale", DateTime.UtcNow.AddDays(-90), 2);

        await _store.CleanupExpiredAsync(30);

        List<SessionRecording> remaining = await _store.ListRecordingsAsync();
        Assert.HasCount(1, remaining);
        Assert.AreEqual(fresh.Id, remaining[0].Id);
        Assert.HasCount(2, await _store.GetChunksAsync(fresh.Id));
    }

    /// <summary>写入一条录制及其数据块;块内容按序号编码,便于逐块比对搬运是否忠实。</summary>
    private async Task<SessionRecording> WriteRecordingAsync(string label, DateTime startedAtUtc, int chunkCount)
    {
        var recording = new SessionRecording
        {
            SessionLabel = label,
            StartedAtUtc = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc),
            EndedAtUtc = DateTime.UtcNow,
            ChunkCount = chunkCount
        };
        for (int i = 0; i < chunkCount; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes(PayloadFor(recording.Id, i));
            await _store.AppendChunkAsync(recording.Id, recording.StartedAtUtc, i * 600L, payload);
            recording.ByteSize += payload.Length;
            recording.DurationMs = i * 600L;
        }
        await _store.SaveRecordingAsync(recording);
        return recording;
    }

    private static string PayloadFor(Guid recordingId, int index) => $"{recordingId:N}#{index:D5}";
}
