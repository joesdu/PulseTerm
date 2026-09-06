using VelaShell.Core.Recording;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 会话录制器的持久化收尾与失败可见性。
/// </summary>
/// <remarks>
/// 录制是"开着就不管了"的后台功能,所以它的失败方式格外要紧:悄悄停掉,用户会一直以为
/// 整场生产操作都留着记录,直到事后要用时才发现只有开头几秒。
/// </remarks>
[TestClass]
[TestCategory("Recorder")]
public sealed class SessionRecorderTests
{
    /// <summary>收尾必须<b>等到</b>最后一块和结束元数据落盘,而不是发出去就走。</summary>
    /// <remarks>
    /// 旧实现的收尾是 <c>_ = PersistAsync(…)</c> —— 不等待。应用退出时它和数据库释放赛跑,
    /// 输的那次录制就只剩一条没有时长、没有结束时间的半截元数据。
    /// </remarks>
    [TestMethod]
    public async Task DisposeAsync_WaitsForTheFinalChunkAndMetadata()
    {
        var store = new RecordingStoreSpy();
        var recorder = new SessionRecorder(store, "web-01", 100, 30);

        recorder.Write("hello world"u8.ToArray());
        await recorder.DisposeAsync();

        Assert.AreEqual(1, store.Chunks.Count, "最后一块没有落盘。");
        Assert.AreEqual("hello world", System.Text.Encoding.UTF8.GetString(store.Chunks[0].Data));
        SessionRecording saved = store.LastSaved!;
        Assert.IsNotNull(saved.EndedAtUtc, "结束时间没写进去 —— 列表里会显示成一条永远在录的记录。");
        Assert.AreEqual(1, saved.ChunkCount);
    }

    /// <summary>录制开始时的尺寸要跟着元数据落盘(asciicast 导出的头部要它)。</summary>
    [TestMethod]
    public async Task TheRecordedTerminalSize_IsPersisted()
    {
        var store = new RecordingStoreSpy();
        var recorder = new SessionRecorder(store, "web-01", 203, 57);

        recorder.Write("x"u8.ToArray());
        await recorder.DisposeAsync();

        Assert.AreEqual(203, store.LastSaved!.Columns);
        Assert.AreEqual(57, store.LastSaved.Rows);
    }

    /// <summary>
    /// 存储写入失败时,录制要停下来并<b>报出原因</b>。
    /// </summary>
    /// <remarks>
    /// 以前只是把一个私有的 <c>_failed</c> 置上,再无下文。
    /// </remarks>
    [TestMethod]
    public async Task WhenTheStoreFails_RecordingStopsAndSaysWhy()
    {
        var store = new RecordingStoreSpy { FailChunks = true };
        var reasons = new List<string>();
        var recorder = new SessionRecorder(store, "web-01", onStopped: reasons.Add);

        recorder.Write("some output"u8.ToArray());
        await recorder.DisposeAsync();

        Assert.IsTrue(recorder.IsStoppedForTest);
        Assert.ContainsSingle(reasons, "录制停了却没有告诉任何人。");
        Assert.Contains("disk is full", reasons[0]);
    }

    /// <summary>
    /// 存储慢下来时待写数据有上限:超过就停止并报告,不无声地一直攒。
    /// </summary>
    /// <remarks>
    /// 旧写法每 600ms 起一个不等待的任务,存储一慢就在内存里一份份攒着 payload,
    /// 涨到几百 MB 也没人看得见 —— 录制是后台功能,没有任何界面会显示它的积压。
    /// </remarks>
    [TestMethod]
    public async Task WhenTheStoreStalls_TheBacklogIsBoundedAndReported()
    {
        var store = new RecordingStoreSpy();
        var reasons = new List<string>();
        var recorder = new SessionRecorder(store, "web-01", onStopped: reasons.Add);
        store.BlockChunks();

        // 每次 Write 满 64KB 就触发一次刷盘;灌到远超上限为止。
        byte[] chunk = new byte[64 * 1024];
        for (int i = 0; i < 1_500 && !recorder.IsStoppedForTest; i++)
        {
            recorder.Write(chunk);
        }

        Assert.IsTrue(recorder.IsStoppedForTest, "灌了 ~96MB 也没触顶 —— 待写队列仍然没有上限。");
        Assert.IsLessThanOrEqualTo(
            64L * 1024 * 1024,
            recorder.QueuedBytesForTest,
            "排队字节数超出了应有的量级。");
        Assert.ContainsSingle(reasons);

        store.ReleaseChunks();
        await recorder.DisposeAsync();
    }

    /// <summary>停止之后再写进来的数据被忽略,不会复活录制。</summary>
    [TestMethod]
    public async Task WritesAfterAFailure_AreIgnored()
    {
        var store = new RecordingStoreSpy { FailChunks = true };
        var recorder = new SessionRecorder(store, "web-01", onStopped: _ => { });

        recorder.Write("first"u8.ToArray());
        await WaitUntilAsync(() => recorder.IsStoppedForTest);
        int chunksAtFailure = store.Chunks.Count;
        recorder.Write("second"u8.ToArray());
        await recorder.DisposeAsync();

        Assert.AreEqual(chunksAtFailure, store.Chunks.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        for (int i = 0; i < 1_000 && !done(); i++)
        {
            await Task.Delay(5);
        }
        Assert.IsTrue(done(), "等待的条件没能在超时内成立。");
    }

    /// <summary>记录被写了什么的内存存储,可按需失败或挂住。</summary>
    private sealed class RecordingStoreSpy : ISessionRecordingStore
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _block;

        public List<RecordingChunk> Chunks { get; } = [];

        public SessionRecording? LastSaved { get; private set; }

        /// <summary>写块时抛异常(磁盘满)。</summary>
        public bool FailChunks { get; init; }

        /// <summary>让写块挂住,模拟存储跟不上。</summary>
        public void BlockChunks()
        {
            lock (_gate)
            {
                _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseChunks()
        {
            lock (_gate)
            {
                _block?.TrySetResult();
                _block = null;
            }
        }

        public Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default)
        {
            // 存的是同一个实例的快照语义:这里只记引用,断言读的是最终状态。
            LastSaved = recording;
            return Task.CompletedTask;
        }

        public async Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data,
            CancellationToken cancellationToken = default)
        {
            Task? block;
            lock (_gate)
            {
                block = _block?.Task;
            }
            if (block is not null)
            {
                await block.ConfigureAwait(false);
            }
            if (FailChunks)
            {
                throw new IOException("the disk is full");
            }
            lock (_gate)
            {
                Chunks.Add(new(offsetMs, data));
            }
        }

        public Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<SessionRecording>());

        public Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Chunks);

        public Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RecordingStorageUsage> GetStorageUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecordingStorageUsage(0, 0, 0));

        public Task<RecordingCleanupResult> ReclaimSpaceAsync(int keepDays, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecordingCleanupResult(0, 0, 0, false));
    }
}
