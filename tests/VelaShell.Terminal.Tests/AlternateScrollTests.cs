using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>
/// 备用屏滚轮转方向键(xterm alternateScroll / DECSET ?1007)。
/// </summary>
/// <remarks>
/// 备用屏没有回滚区,所以未开鼠标追踪时滚轮原本什么都不做 —— less / man /
/// 未开 mouse 的 vim 里"滚轮没反应"就是这么来的。转成光标上下键发给应用,
/// 与 xterm / Windows Terminal / iTerm2 的默认行为一致。
/// </remarks>
[TestClass]
[TestCategory("Mouse")]
public sealed class AlternateScrollTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession _session => HeadlessTestSession.Current;

    /// <summary>进入备用屏(?1049h)并收集滚轮产生的输入字节。</summary>
    private static byte[] WheelOnAlternateScreen(
        double deltaY,
        string extraSetup = "",
        bool enabled = true)
    {
        byte[] captured = [];
        _session.Dispatch(() =>
        {
            var control = new VelaTerminalControl { AlternateScrollEnabled = enabled };
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            control.Feed(Encoding.UTF8.GetBytes("\e[?1049h" + extraSetup));
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(control.IsAlternateScreenActive, "样本应当已切到备用屏。");

            var sink = new List<byte>();
            control.UserInput += bytes => sink.AddRange(bytes);
            window.MouseWheel(new Point(100, 100), new Vector(0, deltaY), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            captured = [.. sink];
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return captured;
    }

    [TestMethod]
    public void WheelUp_OnAlternateScreen_SendsThreeCursorUpSequences()
    {
        // 一格滚轮 = WheelScrollLines(3)行,与本地回滚区的步长口径一致。
        byte[] sent = WheelOnAlternateScreen(1);

        Assert.AreEqual("\e[A\e[A\e[A", Encoding.ASCII.GetString(sent));
    }

    [TestMethod]
    public void WheelDown_OnAlternateScreen_SendsCursorDownSequences()
    {
        byte[] sent = WheelOnAlternateScreen(-1);

        Assert.AreEqual("\e[B\e[B\e[B", Encoding.ASCII.GetString(sent));
    }

    [TestMethod]
    public void ApplicationCursorKeys_SwitchTheSequenceToSS3()
    {
        // DECCKM(?1h)开着时应用要的是 SS3 A 而不是 CSI A —— 走 InputEncoder 就自动对了,
        // 手写 ESC [ A 会在 vim 之类开了应用光标键的程序里发错。
        byte[] sent = WheelOnAlternateScreen(1, extraSetup: "\e[?1h");

        Assert.AreEqual("\eOA\eOA\eOA", Encoding.ASCII.GetString(sent));
    }

    [TestMethod]
    public void Decset1007Reset_TurnsItOffForTheApplication()
    {
        byte[] sent = WheelOnAlternateScreen(1, extraSetup: "\e[?1007l");

        Assert.IsEmpty(sent, "应用用 CSI ?1007 l 关掉之后不应再收到方向键。");
    }

    [TestMethod]
    public void Decset1007Set_TurnsItBackOn()
    {
        byte[] sent = WheelOnAlternateScreen(1, extraSetup: "\e[?1007l\e[?1007h");

        Assert.AreEqual("\e[A\e[A\e[A", Encoding.ASCII.GetString(sent));
    }

    [TestMethod]
    public void UserSetting_Off_SuppressesIt_EvenWhenTheModeIsSet()
    {
        byte[] sent = WheelOnAlternateScreen(1, enabled: false);

        Assert.IsEmpty(sent, "用户在设置里关掉后,备用屏滚轮不应发送任何东西。");
    }

    [TestMethod]
    public void MouseTracking_TakesPrecedence_SoNoArrowKeysAreSent()
    {
        // 开了鼠标追踪的程序(htop/btop)自己处理滚轮上报,不该再收到一串方向键。
        byte[] sent = WheelOnAlternateScreen(1, extraSetup: "\e[?1000h");

        string text = Encoding.ASCII.GetString(sent);
        Assert.DoesNotContain("\e[A", text, StringComparison.Ordinal);
        Assert.IsNotEmpty(sent, "应当走鼠标上报路径而不是什么都不发。");
    }

    [TestMethod]
    public void MainScreen_StillScrollsTheLocalScrollback()
    {
        int offset = 0;
        _session.Dispatch(() =>
        {
            var control = new VelaTerminalControl();
            var window = new Window { Width = 640, Height = 360, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var output = new StringBuilder();
            for (int i = 0; i < 100; i++)
            {
                output.Append("line-").Append(i).Append("\r\n");
            }
            control.Feed(Encoding.UTF8.GetBytes(output.ToString()));
            Dispatcher.UIThread.RunJobs();

            var sink = new List<byte>();
            control.UserInput += bytes => sink.AddRange(bytes);
            window.MouseWheel(new Point(100, 100), new Vector(0, 1), RawInputModifiers.None);

            Assert.IsEmpty(sink, "主屏滚轮不应发送方向键。");
            offset = control.ScrollOffset;
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(3, offset, "主屏滚轮仍然滚本地回滚区。");
    }
}
