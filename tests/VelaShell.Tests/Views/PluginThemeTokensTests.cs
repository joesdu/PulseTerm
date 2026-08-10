using Avalonia.Headless;
using VelaShell.PluginSdk.Rpc;
using VelaShell.Services.Plugins;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("Plugins")]
public sealed class PluginThemeTokensTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginThemeTokensTests).Assembly);

    [TestMethod]
    public void Collect_ResolvesBrushDoubleAndFontTokens()
    {
        _session.Dispatch(async () =>
        {
            IReadOnlyList<ThemeTokenDto> tokens = await PluginThemeTokens.CollectAsync();
            var byKey = tokens.ToDictionary(t => t.Key);

            // 语义画刷(主题字典,当前变体解析):#AARRGGBB。
            ThemeTokenDto error = byKey["VelaError"];
            Assert.AreEqual("brush", error.Kind);
            StringAssert.StartsWith(error.Value, "#");

            // 字号阶梯(与主题无关的 double)。
            ThemeTokenDto fontSize = byKey["VelaFontSize12"];
            Assert.AreEqual("double", fontSize.Kind);
            Assert.AreEqual(12d, double.Parse(fontSize.Value, System.Globalization.CultureInfo.InvariantCulture));

            // 字体令牌:内嵌集合段(fonts:...#)被剔除,只留可移植回退链。
            ThemeTokenDto uiFont = byKey["VelaUiFont"];
            Assert.AreEqual("font", uiFont.Kind);
            Assert.IsFalse(uiFont.Value.Contains('#'), $"内嵌字体段应被剔除: {uiFont.Value}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(uiFont.Value));

            // 图标几何等非 Vela* 或不可序列化资源不外发。
            Assert.IsFalse(byKey.Keys.Any(k => k.StartsWith("Icon.", StringComparison.Ordinal)));
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
