using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

/// <summary>Issue #212:Alt+滚轮在终端本地回滚区中快速滚动。</summary>
[TestClass]
[TestCategory("Input")]
public sealed class FastWheelScrollTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _) =>
        _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public void AltWheel_ScrollsFiveTimesFartherThanPlainWheel()
    {
        (int plain, int fast) result = default;
        _session
            .Dispatch(
                () =>
                {
                    var control = new VelaTerminalControl();
                    var window = new Window
                    {
                        Width = 640,
                        Height = 360,
                        Content = control,
                    };
                    window.Show();
                    Dispatcher.UIThread.RunJobs();

                    var output = new StringBuilder();
                    for (int i = 0; i < 100; i++)
                    {
                        output.Append("line-").Append(i).Append("\r\n");
                    }
                    control.Feed(Encoding.UTF8.GetBytes(output.ToString()));
                    Dispatcher.UIThread.RunJobs();
                    Assert.IsGreaterThanOrEqualTo(15, control.MaxScrollOffset);

                    Point point = new(100, 100);
                    window.MouseWheel(point, new(0, 1), RawInputModifiers.None);
                    result.plain = control.ScrollOffset;

                    control.ScrollOffset = 0;
                    window.MouseWheel(point, new(0, 1), RawInputModifiers.Alt);
                    result.fast = control.ScrollOffset;
                    window.Close();
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();

        Assert.AreEqual(3, result.plain);
        Assert.AreEqual(15, result.fast);
    }
}
