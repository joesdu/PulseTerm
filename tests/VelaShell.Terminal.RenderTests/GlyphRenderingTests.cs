using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.RenderTests;

/// <summary>
/// 终端字形绘制的<b>像素级</b>回归。
/// <para>
/// 覆盖的是 <c>VelaTerminalControl.AppendGlyph</c> / <c>FlushGlyphRun</c> 这条批处理路径:
/// 连续同风格同色的格子攒成一个 <see cref="GlyphRun" /> 一次画出。这条路径有两个改动
/// 无法靠逻辑测试证伪、只会在屏幕上表现出来:
/// </para>
/// <list type="number">
/// <item>交给 GlyphRun 的字符/字形缓冲改成了跨帧复用(不再每个 run 各 ToArray 一份)。
/// 若 Avalonia 其实是延后取用这些数组,画出来的就是错乱字形。</item>
/// <item>GlyphRun 画完即 Dispose(它持有引用计数的原生文本 blob,不释放就是泄漏)。
/// 若渲染数据没有自持引用,提前释放会让文本整片消失或直接崩。</item>
/// </list>
/// <para>
/// 两者都只有真实光栅化才验得到,故本项目挂 Skia 软件后端(见 csproj 注释)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("GlyphRendering")]
public class GlyphRenderingTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) => _session = HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessApp));

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>渲染一帧并返回 BGRA 像素。</summary>
    private static (uint[] Pixels, int Width, int Height) RenderFrame(VelaTerminalControl control, Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using WriteableBitmap bitmap = window.CaptureRenderedFrame()
            ?? throw new AssertFailedException(
                "没有拿到渲染帧。若 Skia 后端未生效(UseHeadlessDrawing 仍为 true),这里恒为 null。");

        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        const int bytesPerPixel = 4;
        int bufferSize = checked(width * height * bytesPerPixel);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, bufferSize, width * bytesPerPixel);
            uint[] pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (uint)Marshal.ReadInt32(buffer, i * bytesPerPixel);
            }
            return (pixels, width, height);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>统计与背景色不同的像素数(= 画出来的"墨水量")。</summary>
    private static int InkPixels(uint[] pixels)
    {
        // 取出现次数最多的颜色当作背景(终端底色铺满全窗)。
        Dictionary<uint, int> histogram = [];
        foreach (uint p in pixels)
        {
            histogram[p] = histogram.GetValueOrDefault(p) + 1;
        }
        uint background = 0;
        int best = -1;
        foreach ((uint color, int count) in histogram)
        {
            if (count > best)
            {
                best = count;
                background = color;
            }
        }
        int ink = 0;
        foreach (uint p in pixels)
        {
            if (p != background)
            {
                ink++;
            }
        }
        return ink;
    }

    private static (VelaTerminalControl Control, Window Window) ShowTerminal(string text)
    {
        var control = new VelaTerminalControl
        {
            ShowLineNumber = false,
            ShowLineTimestamp = false,
            ShowFoldMarker = false,
            CursorBlink = false
        };
        control.Feed(Encoding.UTF8.GetBytes(text));
        var window = new Window { Width = 640, Height = 360, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (control, window);
    }

    [TestMethod]
    public void PlainText_IsActuallyRasterized()
    {
        OnUi(() =>
        {
            (VelaTerminalControl control, Window window) = ShowTerminal("hello world");
            (uint[] withText, _, _) = RenderFrame(control, window);
            window.Close();

            (VelaTerminalControl empty, Window emptyWindow) = ShowTerminal("");
            (uint[] blank, _, _) = RenderFrame(empty, emptyWindow);
            emptyWindow.Close();

            int textInk = InkPixels(withText);
            int blankInk = InkPixels(blank);

            // "hello world" 是 11 个字形,墨水量必须显著高于空屏(空屏只有光标那一小块)。
            Assert.IsGreaterThan(
                blankInk + 200,
                textInk,
                $"渲染出的墨水量({textInk})没有明显超过空屏({blankInk}) —— 字形没有真正画出来。");
        });
    }

    [TestMethod]
    public void ManyStyledRuns_AllRasterize_SoBufferReuseDoesNotCorruptEarlierRuns()
    {
        OnUi(() =>
        {
            // 一行里塞进多段不同 SGR 颜色/粗体 → 强制拆成多个 GlyphRun,每段各走一次
            // FlushGlyphRun。缓冲是跨 run 复用的:若 Avalonia 延后取用,先发出的那几个 run
            // 会被后面的内容覆写,墨水量随之塌陷。这里用"单色单 run"作对照基准。
            var many = new StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                many.Append("[3").Append((char)('1' + (i % 7))).Append('m');
                many.Append(i % 2 == 0 ? "[1m" : "[22m");
                many.Append("SEGMENT");
            }
            many.Append("[0m");

            (VelaTerminalControl multi, Window multiWindow) = ShowTerminal(many.ToString());
            (uint[] multiPixels, _, _) = RenderFrame(multi, multiWindow);
            multiWindow.Close();

            // 对照:同样多的字符,但全程单一风格 → 单个 run。
            (VelaTerminalControl single, Window singleWindow) =
                ShowTerminal(string.Concat(Enumerable.Repeat("SEGMENT", 12)));
            (uint[] singlePixels, _, _) = RenderFrame(single, singleWindow);
            singleWindow.Close();

            int multiInk = InkPixels(multiPixels);
            int singleInk = InkPixels(singlePixels);

            Assert.IsGreaterThan(500, multiInk, "多段彩色文本几乎没画出东西。");

            // 字形数量相同,墨水量应当同量级(粗体略粗,故给一半的宽容度)。
            // 若复用缓冲被延后取用,先前的 run 会画错或画空,这个比值会明显塌陷。
            Assert.IsGreaterThan(
                singleInk / 2,
                multiInk,
                $"多 run({multiInk})相对单 run({singleInk})墨水量塌陷 —— 说明先发出的 GlyphRun 被复用缓冲覆写了。");
        });
    }

    [TestMethod]
    public void RepeatedRepaints_KeepRasterizingText()
    {
        OnUi(() =>
        {
            // GlyphRun 画完即释放:若渲染数据没有自持引用,第一帧之后原生文本 blob 就没了,
            // 后续帧会画不出文本(或崩)。连画多帧,每帧都必须有同等墨水量。
            (VelaTerminalControl control, Window window) = ShowTerminal("persistent text across frames");

            int first = InkPixels(RenderFrame(control, window).Pixels);
            Assert.IsGreaterThan(200, first, "首帧就没画出文本。");

            for (int frame = 0; frame < 5; frame++)
            {
                control.InvalidateTerminal();
                int ink = InkPixels(RenderFrame(control, window).Pixels);
                Assert.IsGreaterThan(
                    first / 2,
                    ink,
                    $"第 {frame + 2} 帧的墨水量({ink})相对首帧({first})塌陷 —— 文本 blob 可能被提前释放了。");
            }

            window.Close();
        });
    }
}

/// <summary>挂 Skia 软件光栅的 headless 宿主:与逻辑测试项目的空绘制后端刻意分开。</summary>
public class SkiaHeadlessApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <summary>供 <see cref="HeadlessUnitTestSession" /> 反射调用。</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SkiaHeadlessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
