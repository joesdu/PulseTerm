using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Recording;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 回放中心标题栏的"清理"入口。这里守的是它真的画在可视树里 —— 时序库删除只写墓碑不腾空间,
/// 这个按钮是用户把几个 GB 要回来的唯一路径,XAML 里写错名字或漏掉事件都不会报错,只是按钮消失。
/// </summary>
[TestClass]
[TestCategory("RecorderUI")]
public sealed class RecordingPlayerCleanupUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RecordingPlayerCleanupUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void TitleBar_CarriesCleanupButton_WithVisibleLabel()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            Button cleanup = fixture.Find<Button>("CleanupButton");
            Assert.IsTrue(cleanup.IsVisible, "清理按钮必须真的画出来。");
            Assert.IsTrue(cleanup.IsEnabled, "没有录制时也要能进清理:孤儿数据正是元数据已删的那部分。");
            Assert.IsGreaterThan(0, cleanup.Bounds.Width, "按钮被挤成零宽等于没有。");
            Assert.AreEqual("清理", cleanup.Content as string);
            return Task.CompletedTask;
        });
    }

    /// <summary>清空全部后列表要空,且"暂无录制"的空态要顶上来。</summary>
    [TestMethod]
    public void CleanupPurgeAll_EmptiesTheList()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();
            Assert.IsTrue(fixture.ViewModel.HasRecordings, "前置:列表里本来有录制。");

            Pump(fixture.ViewModel.CleanupAsync(0));
            Dispatcher.UIThread.RunJobs();
            fixture.Window.UpdateLayout();

            Assert.IsFalse(fixture.ViewModel.HasRecordings);
            Assert.IsEmpty(fixture.ViewModel.Recordings);
            Assert.IsNull(fixture.ViewModel.SelectedRecording);
            Assert.AreEqual(1, fixture.Store.ReclaimCalls);
            Assert.AreEqual(0, fixture.Store.LastKeepDays);
            return Task.CompletedTask;
        });
    }

    /// <summary>"只回收已删除的"这一档必须原样保留现存录制,不能顺手把用户的东西删了。</summary>
    [TestMethod]
    public void CleanupKeepAll_KeepsExistingRecordings()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            Pump(fixture.ViewModel.CleanupAsync(int.MaxValue));
            Dispatcher.UIThread.RunJobs();

            Assert.HasCount(2, fixture.ViewModel.Recordings);
            Assert.AreEqual(int.MaxValue, fixture.Store.LastKeepDays);
            return Task.CompletedTask;
        });
    }

    private static void OnUi(Func<Task> action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// 在 UI 线程上等一个异步操作完成。清理会把重活扔到后台线程,回来的续体要排进
    /// UI 调度器 —— 直接 GetResult 会把 UI 线程堵死等一个只有 UI 线程才能推进的续体
    /// (headless 全套测试共用这一条线程,一堵就是整套挂到超时)。这里改成边泵调度器边等。
    /// </summary>
    private static T Pump<T>(Task<T> task)
    {
        for (int i = 0; i < 10_000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.IsTrue(task.IsCompleted, "清理没能在超时内跑完。");
        return task.GetAwaiter().GetResult();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

        private Fixture(RecordingPlayerView window, RecordingPlayerViewModel viewModel, StubRecordingStore store)
        {
            Window = window;
            ViewModel = viewModel;
            Store = store;
        }

        public RecordingPlayerView Window { get; }

        public RecordingPlayerViewModel ViewModel { get; }

        public StubRecordingStore Store { get; }

        public static Fixture Show()
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");

            var store = new StubRecordingStore();
            var viewModel = new RecordingPlayerViewModel(store);
            viewModel.InitializeAsync().GetAwaiter().GetResult();

            var window = new RecordingPlayerView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return new(window, viewModel, store);
        }

        public T Find<T>(string name)
            where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

        public void Dispose()
        {
            Window.Close();
            CultureInfo.CurrentCulture = _previousCulture;
            CultureInfo.CurrentUICulture = _previousUiCulture;
        }
    }

    /// <summary>内存版录制存储:清理按保留窗口筛掉元数据,足够驱动界面这一层。</summary>
    private sealed class StubRecordingStore : ISessionRecordingStore
    {
        private readonly List<SessionRecording> _recordings =
        [
            new() { SessionLabel = "web-01", StartedAtUtc = DateTime.UtcNow.AddDays(-1), ByteSize = 2048 },
            new() { SessionLabel = "db-01", StartedAtUtc = DateTime.UtcNow.AddDays(-40), ByteSize = 4096 }
        ];

        public int ReclaimCalls { get; private set; }

        public int LastKeepDays { get; private set; } = -1;

        public Task SaveRecordingAsync(SessionRecording recording, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AppendChunkAsync(Guid recordingId, DateTime startedAtUtc, long offsetMs, byte[] data,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<SessionRecording>> ListRecordingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_recordings.OrderByDescending(r => r.StartedAtUtc).ToList());

        public Task<List<RecordingChunk>> GetChunksAsync(Guid recordingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<RecordingChunk>());

        public Task DeleteRecordingAsync(Guid recordingId, CancellationToken cancellationToken = default)
        {
            _recordings.RemoveAll(r => r.Id == recordingId);
            return Task.CompletedTask;
        }

        public Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RecordingStorageUsage> GetStorageUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecordingStorageUsage(_recordings.Count, _recordings.Sum(r => r.ByteSize), 1024 * 1024));

        public Task<RecordingCleanupResult> ReclaimSpaceAsync(int keepDays, CancellationToken cancellationToken = default)
        {
            ReclaimCalls++;
            LastKeepDays = keepDays;
            int before = _recordings.Count;
            if (keepDays <= 0)
            {
                _recordings.Clear();
            }
            else if (keepDays != int.MaxValue)
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-keepDays);
                _recordings.RemoveAll(r => r.StartedAtUtc < cutoff);
            }
            return Task.FromResult(new RecordingCleanupResult(before - _recordings.Count, 1024 * 1024, 0, false));
        }
    }
}
