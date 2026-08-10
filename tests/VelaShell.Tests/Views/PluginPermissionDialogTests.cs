using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 授权对话框的字段填充回归:曾因手写 InitializeComponent 覆盖编译器生成的
/// 字段填充,导致 x:Name 控件(TitleText 等)为 null,构造即抛 NRE
/// (宿主侧被 RPC 折叠成错误码回传插件,表现为终端回写命令报"对象引用为 null")。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class PluginPermissionDialogTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginPermissionDialogTests).Assembly);

    [TestMethod]
    public void ParameterizedConstructor_PopulatesNamedFields_AndDoesNotThrow()
    {
        _session.Dispatch(() =>
        {
            // 私有的带参构造会给 TitleText/MessageText/… 赋文本;字段没被 XAML 填充就 NRE。
            ConstructorInfo ctor = typeof(PluginPermissionDialog).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                [typeof(string), typeof(string), typeof(string)], null)!;
            object dialog = ctor.Invoke(["acme.plugin", "prod-1", "echo hi"]);

            // 命名控件应已由编译器生成的填充逻辑绑定(非 null),且带上了文案。
            var title = (TextBlock)typeof(PluginPermissionDialog)
                .GetField("TitleText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
            Assert.IsNotNull(title);
            Assert.IsFalse(string.IsNullOrEmpty(title.Text));

            ((Window)dialog).Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
