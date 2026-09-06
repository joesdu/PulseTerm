using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Controls.Controls;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Behaviors;

/// <summary>
/// 数字输入框被清空时界面上出现的是人话,而不是绑定抛出的
/// "System.InvalidCastException: Could not convert '(null)' (null) to System.Int32."。
/// </summary>
/// <remarks>
/// 这条守的是用户真的会做的操作:选中"延迟(秒)"按退格删空。控件的 Value 是 decimal?,
/// 目标属性是 int,清空那一刻绑定转换失败,默认行为是把异常对象原样摆到输入框旁边 ——
/// 既没人看得懂,又把 80px 宽的框挤成一条缝。
/// </remarks>
[TestClass]
[TestCategory("NumericInputGuardUi")]
public sealed class NumericInputGuardUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(NumericInputGuardUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void Every_number_box_gets_the_guard_from_the_global_style()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            // 兜底挂在 DockStyles.axaml 的 NumericUpDown 样式上:新加的数字框自动带上,
            // 不需要谁记得在每个 axaml 里补一句。
            Assert.IsTrue(NumericInputGuard.GetEnabled(fixture.Box),
                          "全局样式没把空值兜底挂上,新增的数字框会退回显示转换异常。");
        });
    }

    [TestMethod]
    public void Clearing_the_box_reports_the_allowed_range_instead_of_the_cast_exception()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();

            fixture.Box.Text = "";
            Dispatcher.UIThread.RunJobs();

            Assert.IsTrue(DataValidationErrors.GetHasErrors(fixture.Box),
                          "清空之后绑定确实失败了,界面本就该给出提示。");
            List<string> messages = [.. DataValidationErrors.GetErrors(fixture.Box)!
                .Select(error => error?.ToString() ?? "")];

            Assert.HasCount(1, messages);
            Assert.DoesNotContain("Exception", messages[0], StringComparison.Ordinal);
            Assert.Contains("0", messages[0], StringComparison.Ordinal);
            Assert.Contains("3600", messages[0], StringComparison.Ordinal);
            Assert.AreEqual(NumericInputGuard.Hint(fixture.Box), messages[0]);
        });
    }

    [TestMethod]
    public void Leaving_an_emptied_box_restores_the_last_value_and_clears_the_complaint()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();
            Assert.IsTrue(fixture.Editor.Focus());
            Dispatcher.UIThread.RunJobs();

            fixture.Box.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.IsNull(fixture.Box.Value);

            Assert.IsTrue(fixture.Elsewhere.Focus());
            Dispatcher.UIThread.RunJobs();

            // 清空只是编辑中的一个中间态。人走开之后不该留一个红着的空框 ——
            // 那时视图模型里其实一直是旧值,空框是在说谎。
            Assert.AreEqual(30m, fixture.Box.Value);
            Assert.AreEqual(30, fixture.Model.DelaySeconds);
            Assert.IsFalse(DataValidationErrors.GetHasErrors(fixture.Box));
        });
    }

    [TestMethod]
    public void Restoring_keeps_the_two_way_binding_alive()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();
            Assert.IsTrue(fixture.Editor.Focus());
            Dispatcher.UIThread.RunJobs();

            fixture.Box.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(fixture.Elsewhere.Focus());
            Dispatcher.UIThread.RunJobs();

            // 恢复走的是 SetCurrentValue:写本地值会把绑定压住,此后改设置再也传不回视图模型。
            fixture.Box.Text = "12";
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(12, fixture.Model.DelaySeconds);
        });
    }

    [TestMethod]
    public void The_complaint_is_a_fixed_size_icon_not_a_block_of_text_beside_the_field()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();
            double clean = fixture.Box.Bounds.Width;

            fixture.Box.Text = "";
            fixture.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // Fluent 默认把错误文字贴在控件右侧,设置页每行只有 80–110px,一段文字进来
            // 就把输入框压成一条缝(截图里"延迟(秒)"变成了一个黑方块)。
            LucideIcon icon = fixture.Box.GetVisualDescendants().OfType<LucideIcon>().Single();
            Assert.IsLessThanOrEqualTo(clean + 24, fixture.Box.Bounds.Width,
                                       "校验提示把输入框挤宽了 —— 图标之外又冒出了整段文字。");

            // 话没丢,只是挪进了悬停提示里 —— 这条一旦断了,界面上就只剩一个没人看得懂的红三角。
            Assert.AreEqual(NumericInputGuard.Hint(fixture.Box),
                            ToolTip.GetTip((Control)icon.Parent!) as string);
        });
    }

    [TestMethod]
    public void Typing_letters_never_reaches_the_binding_at_all()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show();
            Assert.IsTrue(fixture.Editor.Focus());
            Dispatcher.UIThread.RunJobs();

            fixture.Box.Text = "abc";
            Dispatcher.UIThread.RunJobs();

            // 解析不了的文本控件自己就拦下了,Value 不动 —— 所以这一路从来不会有转换异常;
            // 真正会漏到绑定上的只有"清空"。这条钉住这个前提,免得日后有人以为提示丢了。
            Assert.AreEqual(30m, fixture.Box.Value);
            Assert.IsFalse(DataValidationErrors.GetHasErrors(fixture.Box));

            Assert.IsTrue(fixture.Elsewhere.Focus());
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("30", fixture.Box.Text);
        });
    }

    [TestMethod]
    public void An_unbounded_box_just_asks_for_a_number()
    {
        OnUi(() =>
        {
            var box = new NumericUpDown();
            string hint = NumericInputGuard.Hint(box);

            // 没设上下界时说不出区间;那就别硬拼一句 "-79228162514264337593543950335 到 …"。
            Assert.DoesNotContain("79228162514264337593543950335", hint, StringComparison.Ordinal);
            Assert.IsNotEmpty(hint);
        });
    }

    [TestMethod]
    public void A_decimal_box_states_its_bounds_the_way_it_shows_them()
    {
        OnUi(() =>
        {
            // 终端行高就是这么配的:0.8–2.0,一位小数。提示里写成 "0.8 到 2" 会让人以为上界是整数。
            var box = new NumericUpDown { Minimum = 0.8m, Maximum = 2.0m, FormatString = "0.0" };

            Assert.Contains("0.8", NumericInputGuard.Hint(box), StringComparison.Ordinal);
            Assert.Contains("2.0", NumericInputGuard.Hint(box), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// 设置窗口是数字输入框最密的地方(端口、超时、保活、日志留存、界面字号、行高、内边距、
    /// 回滚行数、并发数、限速、重试次数……)。逐个清空,逐个核对:提示是区间而不是异常,
    /// 失焦之后值回到原样。
    /// </summary>
    /// <remarks>
    /// 单独列这一条,是因为兜底靠的是全局样式选择器。哪天有人给某个页面加一条更靠后的
    /// <c>NumericUpDown</c> 样式、或者把某个框换成别的控件,这里会立刻红 —— 而只测一个
    /// 孤立的数字框永远发现不了。
    /// </remarks>
    [TestMethod]
    public void Every_number_box_in_the_settings_window_is_covered()
    {
        OnSettingsWindow((window, viewModel) =>
        {
            int swept = 0;
            foreach (int _ in EachPage(window, viewModel))
            {
                foreach (NumericUpDown box in Live<NumericUpDown>(window))
                {
                    SweepOne(window, box);
                    swept++;
                }
            }

            // 没有这道下限,哪天页面结构一变扫不到控件,这条就悄悄变成一个永远通过的空壳。
            // 17 = 常规 7 + 外观 1 + 终端 4 + 传输 4 + 代理 1。
            Assert.IsGreaterThanOrEqualTo(17, swept,
                                          $"设置窗口里只扫到 {swept} 个数字框,远低于预期 —— 扫描八成失效了。");
        });
    }

    /// <summary>
    /// 设置窗口里剩下的可编辑字段(纯文本框)清空之后也不许冒出转换异常 ——
    /// 那意味着又有人把数值属性绑到了 <see cref="TextBox" /> 上(隧道面板的端口原本就是这么写的)。
    /// </summary>
    [TestMethod]
    public void Emptying_any_text_field_in_the_settings_window_never_surfaces_an_exception()
    {
        OnSettingsWindow((window, viewModel) =>
        {
            int swept = 0;
            foreach (int _ in EachPage(window, viewModel))
            {
                // 数字框模板里也有一个 TextBox,那条路上面那条用例已经管了,这里跳过。
                foreach (TextBox field in Live<TextBox>(window)
                             .Where(field => !field.GetVisualAncestors().OfType<NumericUpDown>().Any()))
                {
                    string? original = field.Text;
                    field.Text = "";
                    Dispatcher.UIThread.RunJobs();

                    foreach (object? error in DataValidationErrors.GetErrors(field) ?? [])
                    {
                        Assert.DoesNotContain("Exception", error?.ToString() ?? "", StringComparison.Ordinal,
                                              $"清空文本框「{original}」后冒出了异常原文 —— 它八成绑在一个数值属性上,改用 NumericUpDown。");
                    }

                    field.Text = original;
                    Dispatcher.UIThread.RunJobs();
                    swept++;
                }
            }

            Assert.IsGreaterThanOrEqualTo(20, swept,
                                          $"设置窗口里只扫到 {swept} 个文本框,远低于预期 —— 扫描八成失效了。");
        });
    }

    /// <summary>把设置窗口连同视图模型架起来交给用例;窗口在用例跑完后关掉。</summary>
    private static void OnSettingsWindow(Action<SettingsView, SettingsViewModel> body) =>
        _session.Dispatch(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();

            // 有几个字段跟在开关后面(自动重连的间隔与重试、限速的上下行、日志留存与目录、
            // 代理那一整组)。开关不打开它们根本不出现,扫描就会漏掉一半 —— 全部拨到"露出来"。
            viewModel.General.AutoReconnect = true;
            viewModel.General.SessionLogging = true;
            viewModel.Transfer.BandwidthLimitEnabled = true;
            viewModel.Transfer.TransferLogging = true;
            viewModel.ProxyTypeIndex = 2;

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            body(window, viewModel);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>逐页翻过去:各页默认收着,只有当页可见,里面的控件才有模板、才能拿焦点。</summary>
    private static IEnumerable<int> EachPage(SettingsView window, SettingsViewModel viewModel)
    {
        for (int section = 0; section <= 11; section++)
        {
            viewModel.SelectedSectionIndex = section;
            // 页面是**按需创建**的(见 SettingsPageSelector):第一次切到某一页时,
            // 控件树刚建出来,绑定还没把值推进去 —— 此刻读 NumericUpDown.Value 会读到 null。
            // 真实使用中这一瞬发生在同一帧内、用户看不见,但测试是在帧之间读的,
            // 所以要多跑一轮把绑定落定,否则"原值"取到的是 null。
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            yield return section;
        }
    }

    /// <summary>当页上真的能让用户动手的那些控件。</summary>
    private static List<T> Live<T>(Visual root) where T : Control =>
        [.. root.GetVisualDescendants().OfType<T>()
                .Where(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled)];

    /// <summary>清空一个数字框,核对提示,再把焦点挪走,核对它自己恢复了。</summary>
    private static void SweepOne(Visual root, NumericUpDown box)
    {
        decimal? before = box.Value;
        Assert.IsTrue(NumericInputGuard.GetEnabled(box), $"{Describe(box)} 没挂上空值兜底。");

        box.Text = "";
        Dispatcher.UIThread.RunJobs();

        foreach (string message in DataValidationErrors.GetErrors(box)?
                     .Select(error => error?.ToString() ?? "") ?? [])
        {
            Assert.DoesNotContain("Exception", message, StringComparison.Ordinal,
                                  $"{Describe(box)} 清空后仍在显示异常原文:{message}");
            Assert.AreEqual(NumericInputGuard.Hint(box), message, Describe(box));
        }

        // 走真实的焦点通道:LostFocus 带的是 FocusChangedEventArgs,自己 RaiseEvent 一个裸
        // RoutedEventArgs 会把别人挂的强类型处理器炸掉,测的也就不是真事了。
        Assert.IsTrue(box.GetVisualDescendants().OfType<TextBox>().First().Focus(), Describe(box));
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(Elsewhere(root, box).Focus(), $"{Describe(box)} 那一页找不到能接走焦点的控件。");
        Dispatcher.UIThread.RunJobs();

        Assert.AreEqual(before, box.Value, $"{Describe(box)} 失焦后没回到原值。");
        Assert.IsFalse(DataValidationErrors.GetHasErrors(box), $"{Describe(box)} 失焦后还红着。");
    }

    /// <summary>当页上任意一个能接走焦点的控件,但不能是被测框自己(它的编辑框就在它肚子里)。</summary>
    private static Control Elsewhere(Visual root, NumericUpDown box) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            .First(control => control is TextBox or Button or CheckBox or ToggleSwitch or ComboBox
                              && control.IsEffectivelyVisible
                              && control.Focusable
                              && !control.GetVisualAncestors().Contains(box));

    /// <summary>断言失败时能指认是哪一个框:设置页的框没有名字,用区间当身份。</summary>
    private static string Describe(NumericUpDown box) =>
        $"数字框[{box.Minimum}–{box.Maximum}]";

    private static void OnUi(Action action) =>
        _session.Dispatch(() =>
        {
            action();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>一个绑到 int 属性的数字框 + 一个用来抢焦点的输入框,照搬连接配置里"延迟(秒)"那一处。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(Window window, NumericUpDown box, TextBox elsewhere, DelayModel model)
        {
            Window = window;
            Box = box;
            Elsewhere = elsewhere;
            Model = model;
        }

        internal Window Window { get; }
        internal NumericUpDown Box { get; }
        internal TextBox Elsewhere { get; }
        internal DelayModel Model { get; }

        /// <summary>数字框模板里那个真正接收键盘与焦点的输入框 —— NumericUpDown 本身不可聚焦。</summary>
        internal TextBox Editor => Box.GetVisualDescendants().OfType<TextBox>().First();

        internal static Fixture Show()
        {
            var model = new DelayModel { DelaySeconds = 30 };
            var box = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 3600,
                Increment = 1,
                FormatString = "0",
                ShowButtonSpinner = false,
            };
            box.Bind(NumericUpDown.ValueProperty,
                     new Binding(nameof(DelayModel.DelaySeconds))
                     {
                         Source = model,
                         Mode = BindingMode.TwoWay,
                     });
            var elsewhere = new TextBox();
            var window = new Window { Content = new StackPanel { Children = { box, elsewhere } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return new(window, box, elsewhere, model);
        }

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class DelayModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int DelaySeconds
        {
            get;
            set
            {
                if (field == value)
                {
                    return;
                }
                field = value;
                PropertyChanged?.Invoke(this, new(nameof(DelaySeconds)));
            }
        }
    }
}
