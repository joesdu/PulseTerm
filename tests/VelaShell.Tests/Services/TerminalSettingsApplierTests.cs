using System.Text;
using Avalonia.Headless;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Localization;
using VelaShell.Services;
using VelaShell.Terminal.Rendering;

namespace VelaShell.Tests.Services;

/// <summary>
/// 「设置 → 终端控件」的映射。
/// </summary>
/// <remarks>
/// 这段映射原先埋在 <c>MainWindowViewModel</c> 里,想验一条"会话级编码覆盖了全局"
/// 就得先把整个主窗口(三十个构造参数)造出来。拆成无状态的映射类之后,
/// 每条规则都可以单独摆上台面。
/// </remarks>
[TestClass]
[TestCategory("TerminalSettings")]
public sealed class TerminalSettingsApplierTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TerminalSettingsApplierTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
        // GBK / Big5 一族在旧代码页里,注册之后才取得到(生产路径由 Program.Main 注册)。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static UiTheme Theme() => UiThemeCatalog.Resolve("VelaDark", systemPrefersDark: true);

    [TestMethod]
    public void AnUnknownEncodingNameFallsBackToUtf8()
    {
        // 名字来自设置文件与会话配置,两者都可以手改;一个打错的编码名不该把连接流程炸掉。
        Assert.AreEqual(Encoding.UTF8, TerminalSettingsApplier.ResolveEncoding("不是编码"));
        Assert.AreEqual(Encoding.UTF8, TerminalSettingsApplier.ResolveEncoding(null));
        Assert.AreEqual(Encoding.UTF8, TerminalSettingsApplier.ResolveEncoding("   "));
    }

    /// <summary>旧代码页(GBK / Big5 一族)取得到。</summary>
    /// <remarks>
    /// 断言代码页号而不是 <c>WebName</c>:.NET 把 "GBK" 归到代码页 936,而那一页的
    /// <c>WebName</c> 是 "gb2312" —— 名字对不上不代表取错了编码。
    /// </remarks>
    [TestMethod]
    public void ALegacyCodePageResolves()
    {
        Assert.AreEqual(936, TerminalSettingsApplier.ResolveEncoding("GBK").CodePage);
        Assert.AreEqual(950, TerminalSettingsApplier.ResolveEncoding("Big5").CodePage);
    }

    [TestMethod]
    public void TheBundledFontNeedsItsCollectionPrefix()
    {
        // 内置字体不带集合 URI 前缀就命不中字体管理器,终端会静默退回系统等宽字体。
        Assert.AreEqual("fonts:VelaShell#Cascadia Mono", TerminalSettingsApplier.ResolveFontFamily("Cascadia Mono"));
        Assert.AreEqual("JetBrains Mono", TerminalSettingsApplier.ResolveFontFamily("JetBrains Mono"));
    }

    [TestMethod]
    public void TheSessionEncodingOverrideWinsOverTheGlobalOne() =>
        OnUi(() =>
        {
            // F-06 的主场景:同一个人同时连 UTF-8 的容器和 GBK 的老服务器。
            var control = new VelaTerminalControl();
            AppSettings settings = new() { TerminalEncoding = "UTF-8" };
            SessionProfile profile = new() { Terminal = new() { Encoding = "GBK" } };

            TerminalSettingsApplier.Apply(control, settings, Theme(), profile: profile);

            // 端到端验:喂 GBK 的「中」(0xD6 0xD0)。按 UTF-8 解会出两个替换字符。
            control.Feed([0xD6, 0xD0]);
            Assert.AreEqual('中', control.GetBufferLine(0)[0]);
        });

    [TestMethod]
    public void ALocalShellIsAlwaysUtf8RegardlessOfTheOverride() =>
        OnUi(() =>
        {
            // 本地终端(ConPTY)输出恒为 UTF-8,不套用面向远端主机的编码设置 ——
            // 套上去就是满屏乱码,而用户完全想不到是"远端编码"设置干的。
            var control = new VelaTerminalControl();
            AppSettings settings = new() { TerminalEncoding = "GBK" };
            SessionProfile profile = new() { Terminal = new() { Encoding = "Big5" } };

            TerminalSettingsApplier.Apply(control, settings, Theme(), forceUtf8: true, profile: profile);

            // 喂 UTF-8 的「中」:强制 UTF-8 时才解得出来。
            control.Feed(Encoding.UTF8.GetBytes("中"));
            Assert.AreEqual('中', control.GetBufferLine(0)[0]);
        });

    [TestMethod]
    public void BehaviourSwitchesLandOnTheControl() =>
        OnUi(() =>
        {
            // 保存设置后要对**所有**已打开标签重新应用一次(#3/#15/#21):
            // 用户改一个开关期望的是当场生效,而不是"下次开的标签才有"。
            var control = new VelaTerminalControl();
            AppSettings settings = new()
            {
                ScrollbackLines = 12_345,
                TerminalFontSize = 17,
                TerminalBehavior = new()
                {
                    CopyOnSelect = true,
                    AlternateScroll = true,
                    ShowLineNumber = true,
                    ConfirmMultilinePaste = true,
                },
            };

            TerminalSettingsApplier.Apply(control, settings, Theme());

            Assert.AreEqual(12_345, control.ScrollbackLines);
            Assert.AreEqual(17, control.FontSize);
            Assert.IsTrue(control.CopyOnSelect);
            Assert.IsTrue(control.AlternateScrollEnabled);
            Assert.IsTrue(control.ShowLineNumber);
            Assert.IsTrue(control.ConfirmMultilinePaste);
        });

    [TestMethod]
    public void TheTerminalAlwaysAssumesThePeerEchoes() =>
        OnUi(() =>
        {
            // 用户为串口设备打开「本地回显」之后,所有 SSH 与本地标签都会变成每个字符两遍 ——
            // 那两类的对端本来就自己回显。
            var control = new VelaTerminalControl();
            AppSettings settings = new() { TerminalBehavior = new() { LocalEcho = true } };

            TerminalSettingsApplier.Apply(control, settings, Theme());

            Assert.IsTrue(control.LocalEchoEnabled, "开关本身照常传下去。");
            Assert.IsTrue(control.PeerEchoesInput, "但当前两种传输一律按「对端会回显」处理。");
        });

    [TestMethod]
    public void ABackgroundImageMakesTheTerminalFillTransparent() =>
        OnUi(() =>
        {
            // 背景图开着时终端自绘填充必须全透明:tint 由 TerminalHost 的边框单层承担,
            // 这里再上一层色会两层叠加,保存后终端又变得几乎不透明。
            var control = new VelaTerminalControl();
            AppSettings withImage = new() { Appearance = new() { BackgroundImagePath = @"C:\bg.png" } };
            AppSettings without = new();

            TerminalSettingsApplier.Apply(control, withImage, Theme());
            Assert.AreEqual(0.0, control.BackgroundOpacity);

            TerminalSettingsApplier.Apply(control, without, Theme());
            Assert.AreEqual(1.0, control.BackgroundOpacity);
        });

    [TestMethod]
    public void ASessionColourSchemeOverridesTheGlobalPalette() =>
        OnUi(() =>
        {
            var control = new VelaTerminalControl();
            AppSettings settings = new();
            string scheme = TerminalColorScheme.BuiltIn[1].Name;
            SessionProfile profile = new() { Terminal = new() { ColorScheme = scheme } };

            TerminalSettingsApplier.Apply(control, settings, Theme(), profile: profile);

            Assert.IsNotNull(control.PaletteOverrides,
                "会话指定了配色方案时必须整套压下去,而不是沿用全局那组颜色。");
        });

    [TestMethod]
    public void AnEmptyFontNameLeavesTheExistingFontAlone() =>
        OnUi(() =>
        {
            // 设置里字体是自由文本框,清空是常见操作;那时应当保持现状而不是把字体设成空。
            var control = new VelaTerminalControl();
            TerminalSettingsApplier.Apply(control, new() { TerminalFont = "Consolas" }, Theme());
            var applied = control.FontFamily;

            TerminalSettingsApplier.Apply(control, new() { TerminalFont = "   " }, Theme());

            Assert.AreEqual(applied, control.FontFamily);
        });
}
