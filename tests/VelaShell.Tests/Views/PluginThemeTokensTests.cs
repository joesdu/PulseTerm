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
            Assert.StartsWith("#", error.Value);

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
            Assert.DoesNotContain(k => k.StartsWith("Icon.", StringComparison.Ordinal), byKey.Keys);
                    // **这一行不是凑数,少了它整条用例的断言全部失效。**
            // HeadlessUnitTestSession 只有 Dispatch(Action) 与 Dispatch<T>(Func<Task<T>>) 两族重载,
            // **没有 Func<Task> 那一支**。不返回值的 async lambda 于是被绑到 Action 上、变成 async void:
            // 断言异常落在调度线程上没人接,而 Dispatch 返回的 Task 早就完成了 —— 编译通过、测试恒绿。
            // 实测:把 Assert.Fail 放在用例第一行,dotnet test 照样报全过。
            // 有了返回值才会绑到 Func<Task<T>>,异常才会随 Task 传回来。
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
