using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public sealed class ExternalEditSessionManagerTests
{
    [TestCleanup]
    public void CleanupExternalEditSessions() => ExternalEditSessionManager.CleanupAll();

    [TestMethod]
    [TestCategory("ExternalEdit")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape.txt")]
    [DataRow("../escape.txt")]
    [DataRow("nested/name.txt")]
    [DataRow("nested\\name.txt")]
    public async Task OpenAsync_RejectsUnsafeRemoteLeafNameBeforeTempOrEditor(string fileName)
    {
        ExternalEditSessionManager.CleanupAll();
        string tempRoot = Path.Combine(Path.GetTempPath(), "VelaShell", "remote-edit");
        ISftpService sftpService = Substitute.For<ISftpService>();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ExternalEditSessionManager.OpenAsync(
                sftpService,
                Guid.NewGuid(),
                "/home/user/" + fileName,
                fileName,
                "not-a-real-editor",
                null
            )
        );

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), exception.Message);
        await sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
        Assert.IsFalse(Directory.Exists(tempRoot));
    }

    // ———————————————————— 编辑器退出后的上传收尾 ————————————————————
    //
    // 这里以前是"退出后无条件等 1.5 秒,然后停 watcher、删临时目录"。1.5 秒要同时装下
    // 600ms 防抖 + 等文件解锁 + 一次真实网络上传 —— 慢链路上根本不够。超时的后果不是慢,
    // 而是把用户刚存的内容连同本地副本一起删掉,远端还是旧的,且不报错。

    /// <summary>末次保存还在防抖窗口里就收尾:那一次必须被补传上去。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task Shutdown_FlushesASaveThatIsStillInsideTheDebounceWindow()
    {
        using var fixture = new SessionFixture();

        await fixture.SaveAsync("edited by the user");
        // 防抖是 600ms;这里刻意在它到点之前就收尾。
        bool landed = await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(landed);
        Assert.AreEqual(1, fixture.Uploads.Count, "防抖窗口里的那次保存被丢掉了。");
        Assert.AreEqual("edited by the user", fixture.Uploads[0]);
    }

    /// <summary>上传比旧的 1.5 秒还慢时,收尾要等它真的完成。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task Shutdown_WaitsForAnUploadSlowerThanTheOldFixedDelay()
    {
        using var fixture = new SessionFixture { UploadDelay = TimeSpan.FromSeconds(3) };

        await fixture.SaveAsync("slow link");
        bool landed = await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(landed, "上传还没跑完就宣告收尾完成。");
        Assert.AreEqual(1, fixture.Uploads.Count);
    }

    /// <summary>上传成功后临时副本才可以删。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task AfterASuccessfulUpload_TheLocalCopyIsCleanedUp()
    {
        using var fixture = new SessionFixture();

        await fixture.SaveAsync("done");
        await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));
        fixture.Session.Dispose();

        Assert.IsFalse(Directory.Exists(fixture.Directory));
    }

    /// <summary>
    /// 上传失败时保留本地副本,并把它在哪儿告诉用户。
    /// </summary>
    /// <remarks>
    /// 远端没拿到这份内容,本地副本就是它唯一的存身之处 —— 旧实现照删不误。
    /// </remarks>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task WhenTheUploadFails_TheDraftIsKeptAndReported()
    {
        using var fixture = new SessionFixture { FailUploads = true };

        await fixture.SaveAsync("precious changes");
        bool landed = await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30));
        fixture.Session.Dispose();

        Assert.IsFalse(landed);
        Assert.IsTrue(File.Exists(fixture.LocalPath), "上传失败却把本地副本删了 —— 改动就此丢失。");
        Assert.IsTrue(
            fixture.Errors.Any(e => e.Contains(fixture.LocalPath, StringComparison.Ordinal)),
            "没有告诉用户草稿留在哪儿。");
    }

    /// <summary>没有任何改动就收尾:不该凭空上传一次。</summary>
    [TestMethod]
    [TestCategory("ExternalEdit")]
    public async Task Shutdown_WithoutAnyEdit_UploadsNothing()
    {
        using var fixture = new SessionFixture();

        Assert.IsTrue(await fixture.Session.ShutdownAsync(TimeSpan.FromSeconds(30)));
        Assert.IsEmpty(fixture.Uploads);
    }

    /// <summary>
    /// 一个直接驱动的编辑会话:不起编辑器进程,上传走可控回调。
    /// </summary>
    private sealed class SessionFixture : IDisposable
    {
        public SessionFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), $"vela-extedit-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            LocalPath = Path.Combine(Directory, "app.conf");
            File.WriteAllText(LocalPath, "original");
            Session = new ExternalEditSession(
                Substitute.For<ISftpService>(),
                Guid.NewGuid(),
                "/etc/app.conf",
                LocalPath,
                Errors.Add,
                UploadAsync);
        }

        public string Directory { get; }

        public string LocalPath { get; }

        public ExternalEditSession Session { get; }

        /// <summary>每次上传时本地文件的内容。</summary>
        public List<string> Uploads { get; } = [];

        public List<string> Errors { get; } = [];

        /// <summary>模拟慢链路。</summary>
        public TimeSpan UploadDelay { get; init; }

        /// <summary>模拟上传失败(断网、权限)。</summary>
        public bool FailUploads { get; init; }

        /// <summary>写一次文件,并等到 watcher 真的看见它 —— 不用固定 Sleep 赌时序。</summary>
        public async Task SaveAsync(string content)
        {
            await File.WriteAllTextAsync(LocalPath, content);
            for (int i = 0; i < 2_000 && !Session.HasPendingUploadForTest; i++)
            {
                await Task.Delay(5);
            }
            Assert.IsTrue(Session.HasPendingUploadForTest, "文件监视没能在超时内看到这次写入。");
        }

        public void Dispose()
        {
            Session.Dispose();
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, true);
                }
            }
            catch (IOException)
            {
                // 清理是尽力而为。
            }
        }

        private async Task UploadAsync(string localPath, string remotePath)
        {
            if (UploadDelay > TimeSpan.Zero)
            {
                await Task.Delay(UploadDelay);
            }
            if (FailUploads)
            {
                throw new IOException("connection reset");
            }
            lock (Uploads)
            {
                Uploads.Add(File.ReadAllText(localPath));
            }
        }
    }
}
