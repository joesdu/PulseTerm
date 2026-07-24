using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// OSC 7(shell 上报当前工作目录 file://host/path)解析:驱动「文件浏览器跟随终端目录」。
/// 由 VelaShell 注入的 bash 提示符脚本发出;解析结果须为绝对路径,非法/非绝对一律不触发。
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public class Osc7WorkingDirectoryTests
{
    private static TerminalEmulator New() => new(20, 6, TerminalType.XtermColor256);

    private static string? CaptureCwd(string oscSequence)
    {
        TerminalEmulator e = New();
        string? captured = null;
        e.WorkingDirectoryChanged += path => captured = path;
        e.Feed(Encoding.UTF8.GetBytes(oscSequence));
        return captured;
    }

    [TestMethod]
    public void Osc7_WithHost_StTerminator_ExtractsPath()
    {
        // ESC ] 7 ; file://host/root/temp ESC \
        Assert.AreEqual("/root/temp", CaptureCwd("\e]7;file://myhost/root/temp\e\\"));
    }

    [TestMethod]
    public void Osc7_EmptyHost_ExtractsPath()
    {
        Assert.AreEqual("/var/log", CaptureCwd("\e]7;file:///var/log\e\\"));
    }

    [TestMethod]
    public void Osc7_PercentEncoded_IsDecoded()
    {
        Assert.AreEqual("/a b/c", CaptureCwd("\e]7;file://h/a%20b/c\e\\"));
    }

    [TestMethod]
    public void Osc7_NonFileScheme_DoesNotFire()
    {
        Assert.IsNull(CaptureCwd("\e]7;http://example.com/x\e\\"));
    }

    [TestMethod]
    public void Osc7_MalformedNoPath_DoesNotFire()
    {
        Assert.IsNull(CaptureCwd("\e]7;file://host\e\\"));
    }
}
