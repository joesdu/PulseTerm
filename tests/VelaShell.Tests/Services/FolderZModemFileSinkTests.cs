using VelaShell.Core.Models;
using VelaShell.Core.ZModem.Model;
using VelaShell.Services.ZModem;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="FolderZModemFileSink" /> 的目录选择与防误触重弹逻辑。
/// 用户放弃保存目录时,首次取消应再弹一次(防不小心点关闭/取消),二次取消才真正中止。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class FolderZModemFileSinkTests
{
    private static Func<Task<AppSettings>> Settings()
    {
        var settings = new AppSettings();
        settings.Transfer.LocalDownloadDirectory = Path.GetTempPath();
        settings.Transfer.ConflictPolicy = "rename";
        return () => Task.FromResult(settings);
    }

    private static ZModemFileMetadata Meta(string name) => new() { FileName = name, Size = 10 };

    /// <summary>首次取消(picker 返回 null)后,应再弹一次目录框,而不是立即中止。</summary>
    [TestMethod]
    public async Task FirstCancel_RepromptsOnceBeforeAborting()
    {
        var calls = new List<ZModemFolderPromptRequest>();
        Task<string?> picker(ZModemFolderPromptRequest req, CancellationToken _)
        {
            calls.Add(req);
            return Task.FromResult<string?>(null); // 两次都取消。
        }

        var sink = new FolderZModemFileSink(picker, Settings());
        (ZModemFileDisposition disposition, _) =
            await sink.OnFileOfferedAsync(Meta("a.bin"), new ZModemTransferItem { FileName = "a.bin" }, CancellationToken.None);

        Assert.AreEqual(ZModemFileDisposition.Abort, disposition);
        Assert.HasCount(2, calls, "首次取消应重弹一次,共两次");
        Assert.IsFalse(calls[0].IsRetryAfterCancel, "第一次弹窗不是重试");
        Assert.IsTrue(calls[1].IsRetryAfterCancel, "第二次弹窗应标记为重试(标题提示再次取消即中止)");
    }

    /// <summary>首次取消、第二次选定目录:应接受该文件并写入所选目录,不再中止。</summary>
    [TestMethod]
    public async Task CancelThenChoose_AcceptsIntoChosenFolder()
    {
        string chosen = Path.Combine(Path.GetTempPath(), "vela-zmodem-reprompt-" + Guid.NewGuid().ToString("N"));
        int callCount = 0;
        Task<string?> picker(ZModemFolderPromptRequest _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult(callCount == 1 ? null : chosen); // 先取消,后选定。
        }

        var sink = new FolderZModemFileSink(picker, Settings());
        var item = new ZModemTransferItem { FileName = "b.bin" };
        (ZModemFileDisposition disposition, _) =
            await sink.OnFileOfferedAsync(Meta("b.bin"), item, CancellationToken.None);

        try
        {
            Assert.AreEqual(ZModemFileDisposition.Accept, disposition);
            Assert.AreEqual(2, callCount);
            Assert.IsNotNull(item.LocalPath);
            Assert.IsTrue(item.LocalPath!.StartsWith(chosen, StringComparison.Ordinal));
        }
        finally
        {
            await sink.DisposeAsync();
            try { Directory.Delete(chosen, recursive: true); } catch { /* 清理测试目录,失败无碍 */ }
        }
    }

    /// <summary>目录一旦选定,同会话后续文件不再弹窗(选择缓存在 sink 内)。</summary>
    [TestMethod]
    public async Task FolderChosenOnce_NotPromptedAgainForLaterFiles()
    {
        string chosen = Path.Combine(Path.GetTempPath(), "vela-zmodem-once-" + Guid.NewGuid().ToString("N"));
        int callCount = 0;
        Task<string?> picker(ZModemFolderPromptRequest _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult<string?>(chosen);
        }

        var sink = new FolderZModemFileSink(picker, Settings());
        try
        {
            await sink.OnFileOfferedAsync(Meta("f1.bin"), new ZModemTransferItem { FileName = "f1.bin" }, CancellationToken.None);
            await sink.OnFileOfferedAsync(Meta("f2.bin"), new ZModemTransferItem { FileName = "f2.bin" }, CancellationToken.None);
            Assert.AreEqual(1, callCount, "同会话只应弹一次目录框");
        }
        finally
        {
            await sink.DisposeAsync();
            try { Directory.Delete(chosen, recursive: true); } catch { /* ignore */ }
        }
    }
}
