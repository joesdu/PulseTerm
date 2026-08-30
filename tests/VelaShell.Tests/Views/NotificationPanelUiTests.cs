using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Notifications;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>消息中心面板的真实 Avalonia 布局与截图回归。</summary>
[TestClass]
[TestCategory("Notifications")]
public sealed class NotificationPanelUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(NotificationPanelUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    /// <summary>面板渲染出四类消息:未读/已读、站内跳转/外链、普通/警示。</summary>
    [TestMethod]
    public void Panel_RendersMixedMessages()
    {
        OnUi(() =>
        {
            CultureInfo previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");
            try
            {
                var center = new NotificationCenter();
                DateTime now = DateTime.UtcNow;
                center.PublishAsync([
                    new NotificationItem
                    {
                        Id = "update:1.4.0",
                        Kind = NotificationKind.Update,
                        Title = "VelaShell 1.4.0 已发布",
                        Body = "隧道流量统计、断线自动恢复、端口冲突预检。",
                        PublishedAt = now.AddMinutes(-3),
                        Link = new() { Label = "前往关于页", CommandId = "app.settings.about" }
                    },
                    new NotificationItem
                    {
                        Id = "cve-2026-1234",
                        Kind = NotificationKind.Security,
                        Severity = NotificationSeverity.Warning,
                        Title = "OpenSSH 9.9 安全公告",
                        Body = "受影响版本存在远程可触发的内存越界，建议尽快升级服务端。",
                        PublishedAt = now.AddHours(-5),
                        Link = new() { Label = "阅读全文", Url = "https://www.openssh.com/security.html" }
                    },
                    new NotificationItem
                    {
                        Id = "news-2026-08",
                        Kind = NotificationKind.News,
                        Title = "八月产品月报",
                        Body = "SFTP 双栏、资源监控与插件市场的进展。",
                        PublishedAt = now.AddDays(-2),
                        IsRead = true,
                        Link = new() { Label = "阅读全文", Url = "https://velashell.dev/blog/2026-08" }
                    }
                ]).GetAwaiter().GetResult();

                var vm = new NotificationPanelViewModel(center, _ => true, _ => Task.CompletedTask);
                var view = new NotificationPanelView { DataContext = vm };
                var window = new Window { Width = 400, Height = 520, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Assert.HasCount(3, vm.Items);
                Assert.AreEqual(2, vm.UnreadCount);
                SaveFrame(window, "notification-panel-dark.png");
                window.Close();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
                CultureInfo.CurrentUICulture = previous;
                _localization.SetLanguage(previous.Name);
            }
        });
    }

    /// <summary>
    /// 悬停高亮必须铺满整行。此前它挂在只占内容列的按钮上,删除键那一列不跟着变色,
    /// 整行被切成深浅两块 —— 中间那道边界看着就像凭空多了一条竖分割线(用户反馈)。
    /// </summary>
    [TestMethod]
    public void Row_HoverHighlight_CoversWholeRow()
    {
        OnUi(() =>
        {
            var center = new NotificationCenter();
            center.PublishAsync([
                new NotificationItem
                {
                    Id = "hover",
                    Kind = NotificationKind.Update,
                    Title = "悬停高亮应铺满整行",
                    Body = "包括右侧的删除按钮那一列。",
                    PublishedAt = DateTime.UtcNow,
                    Link = new() { Label = "查看", CommandId = "app.settings.about" }
                }
            ]).GetAwaiter().GetResult();

            var view = new NotificationPanelView { DataContext = new NotificationPanelViewModel(center) };
            var window = new Window { Width = 400, Height = 320, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border row = view.GetVisualDescendants().OfType<Border>()
                             .Single(border => border.Classes.Contains("msg-row"));
            Assert.IsFalse(row.IsPointerOver, "初始状态不该是悬停。");

            // 指到行的**最右侧**(删除按钮所在的那一列)——问题正是出在这半边。
            Point rightEdge = row.TranslatePoint(new(row.Bounds.Width - 6, row.Bounds.Height / 2), window)
                              ?? throw new InvalidOperationException("行未参与布局。");
            window.MouseMove(rightEdge);
            Dispatcher.UIThread.RunJobs();

            Assert.IsTrue(row.IsPointerOver, "指针停在删除按钮那一列时,整行仍应处于悬停态。");
            SaveFrame(window, "notification-panel-hover.png");
            window.Close();
        });
    }

    /// <summary>
    /// 「去处」一行两端对齐:主机名钉左、动作钉右,**动作的右沿逐行共线**。
    /// <para>
    /// 用户反馈:动作刚挪到右边时一列扫下来参差不齐。两个成因都在这里钉住 ——
    /// ① 整条靠右时,主机名长短不一会把动作推到各自不同的位置(站内跳转那行根本没有主机名);
    /// ② 列表若开着横向滚动,每行按"不换行的理想宽度"各量各的,行宽本身就不一样。
    /// 因此同时断言:各行等宽,且动作右沿共线。
    /// </para>
    /// </summary>
    [TestMethod]
    public void DestinationLine_ActionsShareOneRightEdge()
    {
        OnUi(() =>
        {
            var center = new NotificationCenter();
            DateTime now = DateTime.UtcNow;
            center.PublishAsync([
                // 站内跳转:没有主机名,动作左边空空如也。
                new NotificationItem
                {
                    Id = "in-app",
                    Kind = NotificationKind.Update,
                    Title = "VelaShell 1.4.2 已发布",
                    Body = "当前版本 0.0.1-dev。到「关于」页查看并安装更新。",
                    PublishedAt = now.AddMinutes(-16),
                    Link = new() { Label = "前往关于页", CommandId = "app.settings.about" }
                },
                // 外链:主机名短。
                new NotificationItem
                {
                    Id = "short-host",
                    Kind = NotificationKind.Security,
                    Title = "CVE-2026-82644 (CVSS 7.5)",
                    Body = "WWBN AVideo contains a brute-force rate limiting bypass in enforceRateLimit().",
                    PublishedAt = now.AddHours(-3),
                    Link = new() { Label = "阅读全文", Url = "https://nvd.nist.gov/vuln/detail/CVE-2026-82644" }
                },
                // 外链:主机名长得多 —— 若动作跟着主机名走,这一行会被推得最远。
                new NotificationItem
                {
                    Id = "long-host",
                    Kind = NotificationKind.News,
                    Title = "八月产品月报",
                    Body = "SFTP 双栏、资源监控与插件市场的进展。",
                    PublishedAt = now.AddDays(-2),
                    Link = new() { Label = "阅读全文", Url = "https://blog.a-very-long-host-name.example.com/2026-08" }
                }
            ]).GetAwaiter().GetResult();

            var view = new NotificationPanelView { DataContext = new NotificationPanelViewModel(center) };
            var window = new Window { Width = 400, Height = 560, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            List<Border> rows = [.. view.GetVisualDescendants().OfType<Border>()
                                        .Where(border => border.Classes.Contains("msg-row"))];
            Assert.HasCount(3, rows);

            double[] widths = [.. rows.Select(row => Math.Round(row.Bounds.Width, 2)).Distinct()];
            Assert.HasCount(1, widths, "各行必须等宽 —— 宽度不一,行内靠右的元素就不可能对齐。");

            // 行内右对齐的前提:整行按钮真的铺满了它那一列(Button 默认是 Left,不是 Stretch)。
            foreach (Border row in rows)
            {
                Grid outer = row.GetVisualDescendants().OfType<Grid>().First();
                Button rowButton = row.GetVisualDescendants().OfType<Button>()
                                      .First(button => button.Classes.Contains("row"));
                Assert.AreEqual(outer.ColumnDefinitions[1].ActualWidth, rowButton.Bounds.Width, 0.5,
                                "整行按钮必须铺满内容列,否则里面的右对齐是相对它自己那一团内容。");
            }

            double[] actionRightEdges = [.. rows.Select(RightEdgeOf("link-action")).Distinct()];
            Assert.HasCount(1, actionRightEdges,
                            $"动作的右沿必须逐行共线,实测 {string.Join(" / ", rows.Select(RightEdgeOf("link-action")))}。");

            // 每行的删除键与标题栏的关闭键同一条竖线(右留白 16,给悬浮滚动条让位)。
            Border header = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");
            double headerCloseRight = RightEdgeOf("row-action")(header);
            Assert.AreEqual(headerCloseRight, RightEdgeOf("row-action")(rows[0]), 0.5,
                            "行内删除键应与标题栏关闭键右沿对齐。");

            SaveFrame(window, "notification-panel-destination-alignment.png");
            window.Close();

            // 取容器内**最后一个**带该类的控件(标题栏里有三个 row-action,关闭键在最右),
            // 换算到窗口坐标后的右沿。
            Func<Visual, double> RightEdgeOf(string className) => container =>
            {
                Visual target = container.GetVisualDescendants()
                                         .OfType<Control>()
                                         .Last(control => control.Classes.Contains(className));
                Point? corner = target.TranslatePoint(new(target.Bounds.Width, 0), window);
                return Math.Round(corner?.X ?? throw new InvalidOperationException("控件未参与布局。"), 2);
            };
        });
    }

    /// <summary>
    /// 卡片的下圆角不许被最后一行盖成方角(用户反馈:左侧未读竖条把左下角顶方了)。
    /// <para>
    /// Avalonia 的 <c>ClipToBounds</c> 只裁矩形、不按圆角裁子元素(见 <c>CardCornerRadiusTests</c>),
    /// 所以贴着卡片下沿、又有不透明背景的东西必须自己带上内圆角 —— 未读竖条是通高的实心色块,
    /// 正是最容易顶方角的那一个。这里直接量渲染出来的像素:卡片左下角那一格必须仍是卡片外的底色。
    /// </para>
    /// </summary>
    [TestMethod]
    public void CardBottomCorner_IsNotSquaredByUnreadBar()
    {
        OnUi(() =>
        {
            ThemeVariant previousTheme = Application.Current!.RequestedThemeVariant;
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            try
            {
                var center = new NotificationCenter();
                // 条数要足够把列表填到卡片下沿 —— 列表填不满时下沿是空白,根本测不到。
                center.PublishAsync([.. Enumerable.Range(0, 12).Select(index => new NotificationItem
                {
                    Id = $"cve-{index}",
                    Kind = NotificationKind.Security,
                    Title = $"CVE-2026-{index:0000} (CVSS 7.5)",
                    Body = "未读条目的左侧有一条通高的强调色竖条,它正是顶方左下角的那个色块。",
                    PublishedAt = DateTime.UtcNow.AddHours(-index),
                    Link = new() { Label = "阅读全文", Url = "https://nvd.nist.gov/vuln/detail/CVE" }
                })]).GetAwaiter().GetResult();

                // 与 MainWindow 里的摆法一致:视图靠边对齐、按内容收缩,卡片下沿才落在列表末尾
                // —— 若让它纵向铺满窗口,下沿会跑到列表下方的空白处,那里根本没有竖条,测了个寂寞。
                var view = new NotificationPanelView
                {
                    DataContext = new NotificationPanelViewModel(center),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // 卡片四周留白,让"卡片外"的底色有地方可量。
                var window = new Window { Width = 420, Height = 560, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Border card = view.GetVisualDescendants().OfType<Border>().First(border => border.BoxShadow.Count > 0);
                Point origin = card.TranslatePoint(new(0, 0), window)
                               ?? throw new InvalidOperationException("卡片未参与布局。");
                using WriteableBitmap frame = window.CaptureRenderedFrame()
                                              ?? throw new InvalidOperationException("headless 渲染器没出帧。");

                uint accent = ((Application.Current.FindResource(ThemeVariant.Dark, "VelaAccent") as ISolidColorBrush)
                               ?? throw new InvalidOperationException("取不到 VelaAccent。")).Color.ToUInt32();

                // 左下角 6px 圆角**弧线之外**的那一小块三角区:里面不该出现竖条的强调色。
                // (最外一列是卡片自己的 1px 描边,所以从 left+1 起量。)
                int left = (int)Math.Round(origin.X);
                int bottom = (int)Math.Round(origin.Y + card.Bounds.Height) - 1;
                List<string> squared = [];
                for (int dy = 1; dy <= 3; dy++)
                {
                    for (int dx = 1; dx <= 4 - dy; dx++)
                    {
                        if (PixelAt(frame, left + dx, bottom - dy + 1) == accent)
                        {
                            squared.Add($"({left + dx},{bottom - dy + 1})");
                        }
                    }
                }
                Assert.IsEmpty(squared,
                               "卡片左下角的圆角弧外出现强调色 —— 未读竖条把圆角顶成了方角:"
                               + string.Join(" ", squared));

                SaveFrame(window, "notification-panel-bottom-corner.png");
                window.Close();
            }
            finally
            {
                Application.Current.RequestedThemeVariant = previousTheme;
            }
        });
    }

    /// <summary>取渲染帧上某一点的 ARGB(headless 帧是 BGRA 排布)。</summary>
    private static uint PixelAt(WriteableBitmap frame, int x, int y)
    {
        int width = frame.PixelSize.Width;
        int height = frame.PixelSize.Height;
        const int stride = 4;
        int size = checked(width * height * stride);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            frame.CopyPixels(new PixelRect(0, 0, width, height), buffer, size, width * stride);
            int offset = (y * width + x) * stride;
            // headless 帧是 RGBA 排布(拿已知的强调色对过账),按 ARGB 拼回来与
            // Color.ToUInt32() 同口径。
            byte red = Marshal.ReadByte(buffer, offset);
            byte green = Marshal.ReadByte(buffer, offset + 1);
            byte blue = Marshal.ReadByte(buffer, offset + 2);
            byte alpha = Marshal.ReadByte(buffer, offset + 3);
            return ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
