using Avalonia.Headless;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 「最大并发传输数」是<b>整个窗口</b>的上限,不是每个批次的。
/// </summary>
/// <remarks>
/// 设置项里写的是一个数,没有任何限定词。而闸此前挂在每个批次上:双栏面板左右各拖一个
/// 文件夹是 2N,开三个服务器标签同时传是 3N,同一面板先后拖两批也是 2N。用户把这个值
/// 调小,想要的恰恰是"别把线路占满"(跳板机、按流量计费的链路、生产机上不想抢 IO),
/// 实际并发却在悄悄翻倍。
/// </remarks>
[TestClass]
[TestCategory("FileTransfer")]
public sealed class GlobalTransferConcurrencyTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GlobalTransferConcurrencyTests).Assembly);

    /// <summary>两个面板共用一个传输浮窗时,同时在传的文件数不得越过上限。</summary>
    [TestMethod]
    public Task TwoPanelsUploadingAtOnce_StayWithinTheGlobalLimit() => OnUi(async () =>
    {
        const int limit = 2;
        var sink = new FileTransferViewModel(null);
        var uploads = new ConcurrencyProbe();

        // 两个面板 = 两台服务器,各传到自己的目录。共用的只有传输浮窗(以及它里面的名额)。
        FileBrowserViewModel left = CreatePanel(sink, uploads, limit, "/upload-left");
        FileBrowserViewModel right = CreatePanel(sink, uploads, limit, "/upload-right");

        // 两个面板同时各传 6 个文件。若闸是"每批次一个",峰值会是 2 × limit。
        Task first = left.UploadLocalPathsAsync(MakeFiles("left", 6));
        Task second = right.UploadLocalPathsAsync(MakeFiles("right", 6));
        uploads.ReleaseAll();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        // 面板把规划/执行期的异常吞进 ErrorMessage,不先看一眼的话,"少传了几个"会伪装成
        // 并发上限生效,把这条用例变成一条永远绿的空跑。
        Assert.IsNull(left.ErrorMessage, $"左面板报错了:{left.ErrorMessage}");
        Assert.IsNull(right.ErrorMessage, $"右面板报错了:{right.ErrorMessage}");
        Assert.AreEqual(6, uploads.CountUnder("/upload-left"), $"左面板的 6 个没传完。实际记录:{uploads.Describe()}");
        Assert.AreEqual(6, uploads.CountUnder("/upload-right"), $"右面板的 6 个没传完。实际记录:{uploads.Describe()}");
        Assert.IsLessThanOrEqualTo(
            limit,
            uploads.Peak,
            $"同时在传 {uploads.Peak} 个,超过了设置里的 {limit} —— 上限又变回每批次的了。");
    });

    /// <summary>单个面板的一批仍然能用满上限(别为了封顶把正常吞吐也压没了)。</summary>
    [TestMethod]
    public Task OnePanel_StillUsesTheFullLimit() => OnUi(async () =>
    {
        const int limit = 3;
        var sink = new FileTransferViewModel(null);
        var uploads = new ConcurrencyProbe { HoldUntilConcurrent = limit };

        FileBrowserViewModel panel = CreatePanel(sink, uploads, limit);

        Task upload = panel.UploadLocalPathsAsync(MakeFiles("only", 9));
        await upload.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(9, uploads.Total);
        Assert.AreEqual(limit, uploads.Peak, "一批之内没能跑满上限。");
    });

    /// <summary>
    /// 在 headless UI 会话里跑。
    /// </summary>
    /// <remarks>
    /// <b>不是为了摆样子。</b>传输执行与传输面板的记账都改绑定集合,产品要求它们在 UI 线程上
    /// 完成 —— 靠的是"从 UI 线程调用异步方法,续体经同步上下文回到 UI 线程"。裸跑单元测试里
    /// 根本没有同步上下文,几个工作任务的续体就散在线程池上并发改同一个 ObservableCollection,
    /// 结果是集合内部抛 NullReferenceException,而那个异常又会被面板吞进 ErrorMessage、
    /// 随后被 RefreshAsync 里的 `ErrorMessage = null` 抹掉 —— 表面上只看到"少传了几个文件"。
    /// <para>
    /// Windows 上线程池调度碰巧把它们串行化了,所以一直是绿的;Linux/macOS 上稳定复现。
    /// 换句话说,裸跑是在验一个产品并不支持的线程配置。
    /// </para>
    /// </remarks>
    private static async Task OnUi(Func<Task> body) =>
        await _session.Dispatch(body, CancellationToken.None);

    private static FileBrowserViewModel CreatePanel(
        FileTransferViewModel sink, ConcurrencyProbe probe, int limit, string remoteDir = "/upload")
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        sftp.UploadFileAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            // 记下远端路径,好分辨这一次上传是哪个面板发起的。
            .Returns(call => probe.RunAsync(call.ArgAt<string>(2)));
        // 目标目录列举:每次都给一份新的空列表(空目录 = 没有同名冲突要处理)。
        sftp.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<RemoteFileInfo>()));

        var options = new TransferOptions { MaxConcurrentTransfers = limit };
        return new FileBrowserViewModel(sftp, Guid.NewGuid())
        {
            TransferSink = sink,
            TransferOptions = options,
            CurrentPath = remoteDir
        };
    }

    /// <summary>造 <paramref name="count" /> 个真实存在的小文件(上传规划会 stat 它们)。</summary>
    private static List<string> MakeFiles(string prefix, int count)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vela-conc-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var paths = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(directory, $"f{i}.bin");
            File.WriteAllText(path, "x");
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// 数"同时有几个上传在跑"的探针。先把所有上传挂住攒到峰值,再一起放行 ——
    /// 靠 Sleep 去撞并发峰值既慢又测不准。
    /// </summary>
    private sealed class ConcurrencyProbe
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _current;
        private bool _released;

        /// <summary>攒够这么多并发就自动放行(用来验证"能跑满上限")。</summary>
        public int HoldUntilConcurrent { get; init; }

        /// <summary>观察到的并发峰值。</summary>
        public int Peak { get; private set; }

        /// <summary>一共跑过多少次上传。</summary>
        public int Total { get; private set; }

        /// <summary>每次上传的远端路径(用来分辨是哪个面板发起的)。</summary>
        private readonly List<string> _remotePaths = [];

        /// <summary>把记录到的远端路径按出现次数列出来(失败时用来定位)。</summary>
        public string Describe()
        {
            lock (_gate)
            {
                return string.Join(", ", _remotePaths.GroupBy(p => p).Select(g => $"{g.Key}×{g.Count()}"));
            }
        }

        /// <summary>某个远端目录下传过多少个文件。</summary>
        public int CountUnder(string remoteDir)
        {
            lock (_gate)
            {
                return _remotePaths.Count(p => p.StartsWith(remoteDir + "/", StringComparison.Ordinal));
            }
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                _released = true;
            }
            _release.TrySetResult();
        }

        public async Task RunAsync(string remotePath = "")
        {
            bool wait;
            lock (_gate)
            {
                _current++;
                Total++;
                _remotePaths.Add(remotePath);
                Peak = Math.Max(Peak, _current);
                // 攒够上限就自己放行:否则"跑满上限"那条用例会一直等下去。
                if (HoldUntilConcurrent > 0 && _current >= HoldUntilConcurrent)
                {
                    _released = true;
                    _release.TrySetResult();
                }
                wait = !_released;
            }
            if (wait)
            {
                await _release.Task;
            }
            lock (_gate)
            {
                _current--;
            }
        }
    }
}
