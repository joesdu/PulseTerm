using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using Avalonia.VisualTree;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 内置远程编辑器的脏状态。守的是一条很容易丢改动的时序:保存是异步的(写临时文件 + 上传),
/// 而编辑器在这几秒里照常收键盘输入。
/// </summary>
[TestClass]
[TestCategory("EditorUI")]
public sealed class RemoteFileEditorDirtyStateTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteFileEditorDirtyStateTests).Assembly);

    /// <summary>
    /// 保存 A → 上传途中敲出 B → A 上传成功:B 必须仍算未保存。
    /// </summary>
    /// <remarks>
    /// 旧实现在上传返回后无条件 <c>_dirty = false</c>,于是 B 被当成已落盘 ——
    /// 关窗不再提示,内容直接丢失,而且全程没有任何报错。上传越慢(远端、大文件)
    /// 窗口越大,本地测试几乎必然错过。
    /// </remarks>
    [TestMethod]
    public void EditsMadeWhileSavingAreStillDirtyAfterTheUploadSucceeds()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Open("original");

            fixture.SetText("saved-A");
            Assert.IsTrue(fixture.View.IsDirtyForTest, "前置:改过就该是脏的。");

            Task save = fixture.View.SaveForTestAsync();
            Fixture.PumpUntil(() => fixture.Upload.Started);

            // A 还在上传,用户接着敲。
            fixture.SetText("typed-B");

            fixture.Upload.Finish();
            Fixture.PumpUntil(() => save.IsCompleted);

            Assert.IsTrue(
                fixture.View.IsDirtyForTest,
                "A 上传成功把 B 也标成已保存了 —— 关窗时不会再提示,B 就此丢掉。");
            return Task.CompletedTask;
        });
    }

    /// <summary>没有并发编辑的普通保存,成功后必须变干净(别为了修上一条把正常路径也弄脏)。</summary>
    [TestMethod]
    public void APlainSaveClearsTheDirtyFlag()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Open("original");

            fixture.SetText("edited");
            Task save = fixture.View.SaveForTestAsync();
            Fixture.PumpUntil(() => fixture.Upload.Started);
            fixture.Upload.Finish();
            Fixture.PumpUntil(() => save.IsCompleted);

            Assert.IsFalse(fixture.View.IsDirtyForTest);
            Assert.AreEqual("edited", File.ReadAllText(fixture.LocalPath));
            return Task.CompletedTask;
        });
    }

    /// <summary>上传失败不能清脏状态,否则用户以为存上了。</summary>
    [TestMethod]
    public void AFailedUploadKeepsTheChangesDirty()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Open("original");

            fixture.SetText("edited");
            Task save = fixture.View.SaveForTestAsync();
            Fixture.PumpUntil(() => fixture.Upload.Started);
            fixture.Upload.Fail(new IOException("connection reset"));
            Fixture.PumpUntil(() => save.IsCompleted);

            Assert.IsTrue(fixture.View.IsDirtyForTest);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 上传途中再按一次保存,不能被静静吃掉 —— 那一轮结束后要把最新内容补传上去。
    /// </summary>
    [TestMethod]
    public void ASecondSaveRequestDuringAnUploadIsHonouredAfterwards()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Open("original");

            fixture.SetText("first");
            Task save = fixture.View.SaveForTestAsync();
            Fixture.PumpUntil(() => fixture.Upload.Started);

            fixture.SetText("second");
            _ = fixture.View.SaveForTestAsync(); // 在途,应被记为"待补一轮"

            fixture.Upload.Finish();
            Fixture.PumpUntil(() => fixture.Upload.Started); // 第二轮起跑
            fixture.Upload.Finish();
            Fixture.PumpUntil(() => save.IsCompleted && !fixture.View.IsDirtyForTest);

            Assert.AreEqual(2, fixture.Upload.Count, "第二次保存请求被丢掉了。");
            Assert.IsFalse(fixture.View.IsDirtyForTest);
            Assert.AreEqual("second", File.ReadAllText(fixture.LocalPath));
            return Task.CompletedTask;
        });
    }

    private static void OnUi(Func<Task> action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>完成时机完全由测试决定的上传替身:不靠 Sleep 去碰时序。</summary>
    private sealed class ControlledUpload
    {
        private TaskCompletionSource? _current;

        public bool Started => _current is not null;

        public int Count { get; private set; }

        public Task RunAsync()
        {
            Count++;
            _current = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _current.Task;
        }

        public void Finish()
        {
            TaskCompletionSource source = Take();
            source.SetResult();
        }

        public void Fail(Exception error)
        {
            TaskCompletionSource source = Take();
            source.SetException(error);
        }

        private TaskCompletionSource Take()
        {
            TaskCompletionSource source = _current ?? throw new InvalidOperationException("上传还没开始。");
            _current = null;
            return source;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;

        private Fixture(string directory, string localPath, RemoteFileEditorView view, ControlledUpload upload)
        {
            _directory = directory;
            LocalPath = localPath;
            View = view;
            Upload = upload;
        }

        public string LocalPath { get; }

        public RemoteFileEditorView View { get; }

        public ControlledUpload Upload { get; }

        public static Fixture Open(string content)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"vela-editor-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string localPath = Path.Combine(directory, "config.conf");
            File.WriteAllText(localPath, content);

            var upload = new ControlledUpload();
            var view = new RemoteFileEditorView("config.conf", "/etc/config.conf", localPath, upload.RunAsync);
            view.Show();
            var fixture = new Fixture(directory, localPath, view, upload);
            // 构造里发起的读盘是异步的,内容进来之前编辑器是只读的。
            PumpUntil(() => !fixture.Editor.IsReadOnly);
            Assert.AreEqual(content, fixture.Editor.Text, "前置:文件内容没能装载进来。");
            return fixture;
        }

        public TextEditor Editor => View.GetVisualDescendants().OfType<TextEditor>().Single();

        public void SetText(string text)
        {
            Editor.Text = text;
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>边泵调度器边等:续体排在 UI 调度器上,而本方法自己就跑在 UI 线程。</summary>
        public static void PumpUntil(Func<bool> done)
        {
            for (int i = 0; i < 2_000 && !done(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(done(), "等待的条件没能在超时内成立。");
        }

        public void Dispose()
        {
            View.Close();
            Dispatcher.UIThread.RunJobs();
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论。
            }
        }
    }
}
