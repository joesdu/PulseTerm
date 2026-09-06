using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Resources;
using VelaShell.Localization;

namespace VelaShell.Tests.Views;

/// <summary>
/// 滚动条的两态方案(Themes/ScrollBarThemes.axaml):未激活 = 贴边细条,悬停展开 = Windows 那套
/// (滑道 + 两端箭头 + 居中的圆头细滑块),而不是 Fluent 那样把滑块铺满整条滑道。
/// </summary>
/// <remarks>
/// 钉的是【方案】:滑块在两态下的粗细与落位、展开时箭头/滑道要现身。具体像素(16/2/6)写在
/// 主题里,由眼睛拍板;这里只保证"展开后滑块比滑道窄、且左右对称居中",这正是 Fluent 默认
/// 主题不满足、也是本次改动的全部目的 —— 换 Avalonia 版本后若模板被顶回默认,这组测试会红。
///
/// 展开态用 AllowAutoHide=false 触发(ScrollBar.UpdateIsExpandedState:不许自动隐藏就是常驻展开),
/// 不模拟指针:Show 之前设好,值就是直接落定的,不会被 0.1s 过渡动画拖成中间值。
/// </remarks>
[TestClass]
[TestCategory("ScrollBarStyle")]
public sealed class ScrollBarStyleTests
{
    /// <summary>滑道宽度(= 主题里的 VelaScrollBarSize)。</summary>
    private const double BarSize = 16;

    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ScrollBarStyleTests).Assembly);

    [TestMethod]
    public void Collapsed_KeepsTheThinLineHuggingTheOuterEdge()
    {
        OnUi(() =>
        {
            WithScrollBar(Orientation.Vertical, allowAutoHide: true, (bar, thumb) =>
            {
                Assert.IsFalse(bar.IsExpanded);
                // 竖条:左侧内缩 14,只剩右边缘 2px —— 与 Fluent 未激活态一致,这一态刻意不动。
                Assert.AreEqual(new Thickness(14, 0, 0, 0), thumb.Padding);
                Assert.IsTrue(ArrowsAndTrack(bar).All(part => part.Opacity == 0),
                    "未激活态不该露出滑道和两端箭头");
            });

            WithScrollBar(Orientation.Horizontal, allowAutoHide: true, (_, thumb) =>
                // 横条:上方内缩 14,细条贴在下边缘。
                Assert.AreEqual(new Thickness(0, 14, 0, 0), thumb.Padding));
        });
    }

    [TestMethod]
    public void Expanded_ShowsTrackAndArrowsWithACenteredSlimThumb()
    {
        OnUi(() =>
        {
            WithScrollBar(Orientation.Vertical, allowAutoHide: false, (bar, thumb) =>
            {
                Assert.IsTrue(bar.IsExpanded);
                Assert.IsTrue(ArrowsAndTrack(bar).All(part => part.Opacity == 1),
                    "展开态要显出滑道与两端箭头(Windows 资源管理器的那套)");

                // 滑块比滑道窄、且左右等宽 —— Fluent 默认是铺满(左右内缩都是 0),那正是要改掉的。
                Assert.AreEqual(thumb.Padding.Left, thumb.Padding.Right, "展开后的滑块没居中");
                Assert.IsGreaterThan(0, thumb.Padding.Left, "展开后的滑块仍铺满滑道");
                Assert.IsLessThan(BarSize / 2, ThumbVisualThickness(thumb, Orientation.Vertical),
                    "展开后的滑块该明显比滑道细");
                // 圆头,不是方块。
                Assert.IsGreaterThan(0, thumb.CornerRadius.TopLeft);
            });

            WithScrollBar(Orientation.Horizontal, allowAutoHide: false, (_, thumb) =>
            {
                Assert.AreEqual(thumb.Padding.Top, thumb.Padding.Bottom, "展开后的滑块没居中");
                Assert.IsGreaterThan(0, thumb.Padding.Top, "展开后的滑块仍铺满滑道");
                Assert.IsLessThan(BarSize / 2, ThumbVisualThickness(thumb, Orientation.Horizontal),
                    "展开后的滑块该明显比滑道细");
            });
        });
    }

    /// <summary>
    /// 滚动条各部件的无障碍名字要跟界面语言走,不能是硬编码的英文。
    /// </summary>
    /// <remarks>
    /// 模板里这十处名字("Line up"、"Page down"、"Position"……)是替换 Fluent 模板时一并
    /// 抄进来的英文。整个应用支持五种语言,读屏器用户听到的却是中文界面里蹦出英文部件名。
    /// </remarks>
    [TestMethod]
    public void ThePartsOfAScrollBar_AreNamedInTheUiLanguage()
    {
        OnUi(() =>
        {
            LocalizedStrings.Instance.Attach(new LocalizationService());

            WithScrollBar(Orientation.Vertical, allowAutoHide: false, (bar, thumb) =>
            {
                Assert.AreEqual(Strings.Get("ScrollBar_VerticalThumb"), AutomationProperties.GetName(thumb));
                foreach ((string part, string key) in new[]
                         {
                             ("PART_LineUpButton", "ScrollBar_LineUp"),
                             ("PART_LineDownButton", "ScrollBar_LineDown"),
                             ("PART_PageUpButton", "ScrollBar_PageUp"),
                             ("PART_PageDownButton", "ScrollBar_PageDown")
                         })
                {
                    Control control = bar.GetVisualDescendants().OfType<Control>().Single(c => c.Name == part);
                    string name = AutomationProperties.GetName(control);
                    Assert.AreEqual(Strings.Get(key), name, $"{part} 的名字没走本地化。");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(name));
                }
            });

            WithScrollBar(Orientation.Horizontal, allowAutoHide: false, (bar, thumb) =>
            {
                Assert.AreEqual(Strings.Get("ScrollBar_HorizontalThumb"), AutomationProperties.GetName(thumb));
                Control left = bar.GetVisualDescendants().OfType<Control>()
                    .Single(c => c.Name == "PART_LineUpButton");
                Assert.AreEqual(Strings.Get("ScrollBar_ColumnLeft"), AutomationProperties.GetName(left));
            });
        });
    }

    /// <summary>滑块上真正被画出来的那根条子的粗细(模板里的 Border 按 Padding 内缩后剩下的)。</summary>
    private static double ThumbVisualThickness(Thumb thumb, Orientation orientation) =>
        orientation == Orientation.Vertical
            ? BarSize - thumb.Padding.Left - thumb.Padding.Right
            : BarSize - thumb.Padding.Top - thumb.Padding.Bottom;

    /// <summary>展开时该一起淡入的部件:滑道底 + 两端箭头按钮。</summary>
    private static IEnumerable<Visual> ArrowsAndTrack(ScrollBar bar) =>
        bar.GetVisualDescendants()
           .Where(v => v is Shape { Name: "TrackRect" }
               or RepeatButton { Name: "PART_LineUpButton" or "PART_LineDownButton" });

    /// <summary>把一条滚动条挂进窗口跑完布局,交给断言;收尾一律关窗。</summary>
    private static void WithScrollBar(Orientation orientation, bool allowAutoHide, Action<ScrollBar, Thumb> body)
    {
        var bar = new ScrollBar
        {
            Orientation = orientation,
            AllowAutoHide = allowAutoHide,
            Minimum = 0,
            Maximum = 100,
            ViewportSize = 10,
        };
        var window = new Window { Width = 200, Height = 200, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            body(bar, bar.GetVisualDescendants().OfType<Thumb>().Single());
        }
        finally
        {
            window.Close();
        }
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(
            () =>
            {
                body();
                return Task.CompletedTask;
            },
            CancellationToken.None
        ).GetAwaiter().GetResult();
}
