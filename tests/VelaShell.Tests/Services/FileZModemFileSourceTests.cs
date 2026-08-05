using VelaShell.Core.ZModem.Model;
using VelaShell.Services.ZModem;

namespace VelaShell.Tests.Services;

/// <summary>
/// <see cref="FileZModemFileSource" />(远端 <c>rz</c> 上传来源)的文件选择与防误触重弹逻辑。
/// 与下载目录选择一致:首次取消(未选文件)再给一次机会,二次取消才真正中止。
/// </summary>
[TestClass]
[TestCategory("ZModem")]
public class FileZModemFileSourceTests
{
    /// <summary>首次取消(picker 返回空)后,应再弹一次文件框,第二次标记为重试。</summary>
    [TestMethod]
    public async Task FirstCancel_RepromptsOnceBeforeGivingUp()
    {
        var retryFlags = new List<bool>();
        Task<IReadOnlyList<string>> picker(bool isRetry, CancellationToken _)
        {
            retryFlags.Add(isRetry);
            return Task.FromResult<IReadOnlyList<string>>([]); // 两次都取消。
        }

        var source = new FileZModemFileSource(picker);
        IReadOnlyList<ZModemOutgoingFile> files = await source.GetFilesAsync(CancellationToken.None);

        Assert.IsEmpty(files, "两次都取消应最终返回空清单(触发发送方优雅收尾)");
        Assert.HasCount(2, retryFlags, "首次取消应重弹一次,共两次");
        Assert.IsFalse(retryFlags[0], "第一次弹窗不是重试");
        Assert.IsTrue(retryFlags[1], "第二次弹窗应标记为重试");
    }

    /// <summary>首次取消、第二次选定文件:应返回该文件,不再视为取消。</summary>
    [TestMethod]
    public async Task CancelThenChoose_ReturnsChosenFiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "vela-zmodem-upload-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(tmp, [1, 2, 3, 4, 5]);
        int callCount = 0;
        Task<IReadOnlyList<string>> picker(bool _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<string>>(callCount == 1 ? [] : [tmp]); // 先取消,后选定。
        }

        try
        {
            var source = new FileZModemFileSource(picker);
            IReadOnlyList<ZModemOutgoingFile> files = await source.GetFilesAsync(CancellationToken.None);

            Assert.AreEqual(2, callCount);
            Assert.HasCount(1, files);
            Assert.AreEqual(Path.GetFileName(tmp), files[0].RemoteName, "远端文件名应为纯文件名");
            Assert.AreEqual(5, files[0].Size);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>首次就选定文件时不应重弹。</summary>
    [TestMethod]
    public async Task ChosenFirstTry_DoesNotReprompt()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "vela-zmodem-upload1-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(tmp, [9]);
        int callCount = 0;
        Task<IReadOnlyList<string>> picker(bool _1, CancellationToken _2)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<string>>([tmp]);
        }

        try
        {
            var source = new FileZModemFileSource(picker);
            IReadOnlyList<ZModemOutgoingFile> files = await source.GetFilesAsync(CancellationToken.None);

            Assert.AreEqual(1, callCount, "首次即选定不应重弹");
            Assert.HasCount(1, files);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
