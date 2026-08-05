using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using VelaShell.Features.Sftp;

namespace VelaShell.Tests.Views;

/// <summary>
/// 对话框动作按钮的统一视觉方案:主操作 = 强调药丸(VelaAccentPillButtonTheme),
/// 次操作 = 描边按钮(VelaOutlineButtonTheme)。见 DESIGN.md 的 Buttons 一节。
/// </summary>
/// <remarks>
/// 起因是"选择要上传的文件与文件夹"对话框:确认按钮虽然挂了药丸主题却没拿到强调色前景,
/// 取消按钮干脆是 Fluent 默认外观 —— 同一个「上传」动作在文件浏览器工具条和这个对话框里
/// 长成了两个样子。这里钉住的是【方案本身】(挂对主题、拿到对的前景),不钉具体像素:
/// 尺寸是照观感调的,该由眼睛拍板。
/// </remarks>
[TestClass]
[TestCategory("DialogButtonStyle")]
public class DialogButtonStyleTests
{
    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DialogButtonStyleTests).Assembly);

    [TestMethod]
    public void AccentPillTheme_PaintsItsContentInAccent()
    {
        OnUi(() =>
        {
            // 药丸主题自带强调色前景,调用处不必逐个元素重复写 Foreground ——
            // 少写一处就少一处漏写(上传对话框的确认按钮当初漏的就是它)。
            var button = new Button { Theme = Theme("VelaAccentPillButtonTheme") };
            var label = new TextBlock { Text = "上传" };
            button.Content = new StackPanel { Children = { label } };
            var window = new Window { Content = button };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.AreEqual(Brush("VelaAccent"), button.Foreground);
                // 内容里的文字靠继承拿到同一支画刷,不写 Foreground 也不会退回默认文字色。
                Assert.AreEqual(Brush("VelaAccent"), label.Foreground);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void UploadDialog_UsesSharedActionButtonThemes()
    {
        OnUi(() =>
        {
            // loadInitial: false —— 不去真读磁盘,本组只关心按钮长什么样。
            var window = new LocalPathPickerDialog(new LocalFilePaneViewModel(new TransferOptions()), loadInitial: false);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Button confirm = ButtonNamed(window, "ConfirmButton");
                Assert.AreEqual(Theme("VelaAccentPillButtonTheme"), confirm.Theme, "上传按钮没走强调药丸方案");

                // 没选中任何条目时确认按钮是禁用的(前景走 VelaTextMuted),放开才看得到强调色。
                confirm.IsEnabled = true;
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(Brush("VelaAccent"), confirm.Foreground);

                // 取消:与确认同处一条动作条,必须是描边次操作,而不是 Fluent 默认按钮
                // (默认按钮是实底灰,和旁边透明的药丸完全不是一套东西)。
                Button cancel = SiblingButtons(confirm).Single(b => b != confirm);
                Assert.AreEqual(Theme("VelaOutlineButtonTheme"), cancel.Theme, "取消按钮没走描边方案");

                // 两个按钮等高,否则一条动作条上高低不齐。
                Assert.AreEqual(confirm.Bounds.Height, cancel.Bounds.Height);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>确认按钮带 upload 图标:与文件浏览器工具条上的「上传」是同一个图标。</summary>
    [TestMethod]
    public void UploadDialog_ConfirmButtonCarriesUploadIcon()
    {
        OnUi(() =>
        {
            // loadInitial: false —— 不去真读磁盘,本组只关心按钮长什么样。
            var window = new LocalPathPickerDialog(new LocalFilePaneViewModel(new TransferOptions()), loadInitial: false);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.IsNotEmpty(
                    ButtonNamed(window, "ConfirmButton").GetVisualDescendants()
                        .OfType<Control>()
                        .Where(c => c.GetType().Name == "LucideIcon")
                        .ToList(),
                    "上传按钮少了 upload 图标"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Button ButtonNamed(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static IEnumerable<Button> SiblingButtons(Button button) =>
        button.GetVisualAncestors()
              .OfType<StackPanel>()
              .First()
              .GetVisualDescendants()
              .OfType<Button>();

    private static ControlTheme Theme(string key) =>
        Application.Current!.TryGetResource(key, null, out object? value) && value is ControlTheme theme
            ? theme
            : throw new AssertFailedException($"按钮主题 {key} 不存在");

    private static IBrush Brush(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out object? value) && value is IBrush brush
            ? brush
            : throw new AssertFailedException($"画刷令牌 {key} 不存在");

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
