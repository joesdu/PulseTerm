using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Core.Recording;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 回放中心的 asciicast 导出与录制切换。这里守的三条都曾经真的坏过,而且都只在特定环境下现形:
/// 换个区域设置、换个块边界、换个加载返回顺序,同一份录制的导出结果就不一样。
/// </summary>
[TestClass]
[TestCategory("RecorderUI")]
public sealed class RecordingPlayerExportTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RecordingPlayerExportTests).Assembly);

    /// <summary>
    /// 以逗号作小数点的区域设置下,事件行仍须是合法的三元素 JSON 数组。
    /// </summary>
    /// <remarks>
    /// 旧写法 <c>$"[{ms / 1000.0:0.000}, …]"</c> 在 fr-FR 下产出 <c>[1,234, "o", "x"]</c> ——
    /// 四个元素,asciinema-player 直接拒绝整个文件。开发机是 zh-CN / en-US,所以这条
    /// 在本地永远看不见,只有法语、德语、俄语用户导出后才发现文件是废的。
    /// </remarks>
    [TestMethod]
    public void BuildAsciicast_UnderCommaDecimalCulture_EmitsParsableEvents()
    {
        OnUi(() =>
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            try
            {
                RecordingPlayerViewModel viewModel = Load(new StubStore(
                    new RecordingChunk(1234, "hello"u8.ToArray())));

                string[] lines = viewModel.BuildAsciicast()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                using JsonDocument evt = JsonDocument.Parse(lines[1]);
                Assert.AreEqual(JsonValueKind.Array, evt.RootElement.ValueKind);
                Assert.AreEqual(3, evt.RootElement.GetArrayLength(), "事件行必须正好是 [时间, 类型, 数据] 三元素。");
                Assert.AreEqual(1.234, evt.RootElement[0].GetDouble(), 1e-9);
                Assert.AreEqual("o", evt.RootElement[1].GetString());
                Assert.AreEqual("hello", evt.RootElement[2].GetString());
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 一个多字节字符被块边界切开时,导出后仍须是那一个字符。
    /// </summary>
    /// <remarks>
    /// 块是按 600ms / 64KB 切的,和字符边界毫无关系。"中" = E4 B8 AD,被切成 2+1 时,
    /// 旧的逐块 <c>Encoding.UTF8.GetString</c> 会让两块各解出一个 U+FFFD,原字彻底没了。
    /// </remarks>
    [TestMethod]
    public void BuildAsciicast_MultiByteCharSplitAcrossChunks_StaysOneCharacter()
    {
        OnUi(() =>
        {
            byte[] han = Encoding.UTF8.GetBytes("中");
            Assert.AreEqual(3, han.Length, "前置:这个字在 UTF-8 下就是三个字节。");

            RecordingPlayerViewModel viewModel = Load(new StubStore(
                new RecordingChunk(0, han[..2]),
                new RecordingChunk(10, han[2..])));

            string[] lines = viewModel.BuildAsciicast()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string decoded = string.Concat(lines.Skip(1).Select(line =>
            {
                using JsonDocument evt = JsonDocument.Parse(line);
                return evt.RootElement[2].GetString();
            }));

            Assert.AreEqual("中", decoded);
            Assert.DoesNotContain("�", decoded, "出现替换字符就说明解码没有跨块延续。");
            return Task.CompletedTask;
        });
    }

    /// <summary>头部尺寸取录制里记的真实值,不是写死的 120×32。</summary>
    [TestMethod]
    public void BuildAsciicast_Header_UsesTheRecordedTerminalSize()
    {
        OnUi(() =>
        {
            var store = new StubStore(new RecordingChunk(0, "x"u8.ToArray()));
            store.Recordings[0].Columns = 203;
            store.Recordings[0].Rows = 57;

            RecordingPlayerViewModel viewModel = Load(store);

            using JsonDocument header = JsonDocument.Parse(
                viewModel.BuildAsciicast().Split('\n')[0]);
            Assert.AreEqual(203, header.RootElement.GetProperty("width").GetInt32());
            Assert.AreEqual(57, header.RootElement.GetProperty("height").GetInt32());
            return Task.CompletedTask;
        });
    }

    /// <summary>老录制(尺寸字段出现之前录的)缺尺寸时用默认值,不能写出 0×0。</summary>
    [TestMethod]
    public void BuildAsciicast_Header_FallsBackForRecordingsWithoutASize()
    {
        OnUi(() =>
        {
            RecordingPlayerViewModel viewModel = Load(new StubStore(new RecordingChunk(0, "x"u8.ToArray())));

            using JsonDocument header = JsonDocument.Parse(viewModel.BuildAsciicast().Split('\n')[0]);
            Assert.AreEqual(SessionRecording.DefaultColumns, header.RootElement.GetProperty("width").GetInt32());
            Assert.AreEqual(SessionRecording.DefaultRows, header.RootElement.GetProperty("height").GetInt32());
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 先选 A 再选 B,若 A 的读取后返回,显示的必须还是 B。
    /// </summary>
    /// <remarks>
    /// 大录制读起来慢,"点错一个再点对的"是最自然的操作。旧实现把 <c>await</c> 回来的结果
    /// 直接赋给 <c>_chunks</c>,于是界面高亮着 B、放出来的却是 A 的内容 —— 而且没有任何报错。
    /// </remarks>
    [TestMethod]
    public void SelectedRecording_LateResultFromThePreviousPick_IsDiscarded()
    {
        OnUi(() =>
        {
            var store = new GatedStore();
            var viewModel = new RecordingPlayerViewModel(store);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            Dispatcher.UIThread.RunJobs();

            RecordingItemViewModel a = viewModel.Recordings.Single(r => r.Label == "a");
            RecordingItemViewModel b = viewModel.Recordings.Single(r => r.Label == "b");

            viewModel.SelectedRecording = a;
            Dispatcher.UIThread.RunJobs();
            viewModel.SelectedRecording = b;
            Dispatcher.UIThread.RunJobs();

            // B 先落地,A 迟到 —— 正是会把 B 顶掉的那个顺序。
            store.Complete(b.Model.Id, new RecordingChunk(0, "BBB"u8.ToArray()));
            PumpUntil(() => viewModel.HasSelection);
            store.Complete(a.Model.Id, new RecordingChunk(0, "AAA"u8.ToArray()));
            // 迟到的那份没有可等的"生效"信号,只能泵够时间让它有充分机会捣乱。
            PumpUntil(() => false, iterations: 200);

            Assert.AreSame(b, viewModel.SelectedRecording);
            Assert.Contains("BBB", viewModel.BuildAsciicast(), "迟到的 A 把 B 的内容覆盖了。");
            Assert.DoesNotContain("AAA", viewModel.BuildAsciicast());
            return Task.CompletedTask;
        });
    }

    /// <summary>清空选择要把 HasSelection 通知出去,否则播放/导出按钮停在上一份录制的可用状态。</summary>
    [TestMethod]
    public void ClearingTheSelection_RaisesHasSelection()
    {
        OnUi(() =>
        {
            RecordingPlayerViewModel viewModel = Load(new StubStore(new RecordingChunk(0, "x"u8.ToArray())));
            Assert.IsTrue(viewModel.HasSelection, "前置:选中且有块。");

            var raised = new List<string?>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            viewModel.SelectedRecording = null;
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(viewModel.HasSelection);
            Assert.Contains(nameof(RecordingPlayerViewModel.HasSelection), raised);
            return Task.CompletedTask;
        });
    }

    private static RecordingPlayerViewModel Load(StubStore store)
    {
        var viewModel = new RecordingPlayerViewModel(store);
        viewModel.InitializeAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        viewModel.SelectedRecording = viewModel.Recordings[0];
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(viewModel.HasSelection, "前置:录制块没能装载进来。");
        return viewModel;
    }

    private static void OnUi(Func<Task> action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 边泵调度器边等条件成立。加载的续体走的是 UI 调度器,而本方法自己就跑在 UI 线程上 ——
    /// 光靠一次 RunJobs 会赶在续体排进队列之前就返回,断言随即打在还没更新的状态上。
    /// </summary>
    private static void PumpUntil(Func<bool> done, int iterations = 2_000)
    {
        for (int i = 0; i < iterations && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>只读的内存录制存储:一条录制、一组固定块,块马上返回。</summary>
    private class StubStore(params RecordingChunk[] chunks) : ISessionRecordingStore
    {
        public List<SessionRecording> Recordings { get; } =
            [new() { SessionLabel = "web-01", StartedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) }];

        public Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Recordings.ToList());

        public virtual Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId,
            CancellationToken cancellationToken = default) => Task.FromResult(chunks.ToList());

        public Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RecordingStorageUsage> GetStorageUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecordingStorageUsage(Recordings.Count, 0, 0));

        public Task<RecordingCleanupResult> ReclaimSpaceAsync(int keepDays, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecordingCleanupResult(0, 0, 0, false));
    }

    /// <summary>
    /// 读取由测试决定何时完成的存储:用来精确构造"先选 A 再选 B、A 后返回"的顺序,
    /// 不靠 Sleep 碰运气。取消令牌<b>刻意忽略</b> —— 要验证的正是即便结果照样送达,
    /// 过时的那一份也不会被采用。
    /// </summary>
    private sealed class GatedStore : StubStore
    {
        private readonly Dictionary<Guid, TaskCompletionSource<List<RecordingChunk>>> _pending = [];

        public GatedStore()
        {
            Recordings.Clear();
            Recordings.Add(new() { SessionLabel = "a", StartedAtUtc = DateTime.UtcNow.AddMinutes(-1) });
            Recordings.Add(new() { SessionLabel = "b", StartedAtUtc = DateTime.UtcNow });
        }

        public override Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId,
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<List<RecordingChunk>> source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[recordingId] = source;
            return source.Task;
        }

        public void Complete(Guid recordingId, params RecordingChunk[] chunks) =>
            _pending[recordingId].SetResult([.. chunks]);
    }
}
