using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
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
