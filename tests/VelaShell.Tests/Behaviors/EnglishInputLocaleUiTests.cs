using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using VelaShell.Behaviors;
using VelaShell.Services;

namespace VelaShell.Tests.Behaviors;

[TestClass]
[TestCategory("EnglishInputLocaleUi")]
// 不并行的约束已提升到程序集级(见 ModuleInit.cs):共享 headless UI 线程的是全部 UI 测试,
// 不止这一个类。
public sealed class EnglishInputLocaleUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(EnglishInputLocaleUiTests).Assembly);

    [TestMethod]
    public void Focus_then_blur_restores_once_even_when_disable_and_detach_follow()
    {
        Func<IInputLocaleSwitcher> originalFactory = EnglishInputLocale.InputLocaleSwitcherFactory;
        var fake = new FakeInputLocaleSwitcher();
        EnglishInputLocale.InputLocaleSwitcherFactory = () => fake;

        try
        {
            _session.Dispatch(() =>
            {
                var focused = new TextBox();
                var other = new TextBox();

                // Given: IME is enabled before the English-input-locale behavior attaches
                InputMethod.SetIsInputMethodEnabled(focused, true);
                Assert.IsTrue(InputMethod.GetIsInputMethodEnabled(focused),
                    "IME should be true before behavior attaches");

                // When: EnglishInputLocale behavior is enabled
                EnglishInputLocale.SetEnabled(focused, true);

                // Then: IME is disabled before the control can accept composition.
                Assert.IsFalse(InputMethod.GetIsInputMethodEnabled(focused),
                    "EnglishInputLocale.SetEnabled(true) should disable Avalonia IME");
                var window = new Window
                {
                    Content = new StackPanel { Children = { focused, other } },
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.IsTrue(focused.Focus());
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(1, fake.SelectCalls);
                Assert.AreEqual(0, fake.RestoreCalls);

                Assert.IsTrue(other.Focus());
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(1, fake.RestoreCalls);
                Assert.AreEqual(0x0411, fake.RestoredLayout);

                EnglishInputLocale.SetEnabled(focused, false);

                // Then: IME should be re-enabled after behavior is disabled
                Assert.IsTrue(InputMethod.GetIsInputMethodEnabled(focused),
                    "EnglishInputLocale.SetEnabled(false) should re-enable Avalonia IME");
                window.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.AreEqual(1, fake.RestoreCalls);
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            EnglishInputLocale.InputLocaleSwitcherFactory = originalFactory;
        }
    }

    private sealed class FakeInputLocaleSwitcher : IInputLocaleSwitcher
    {
        public int SelectCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public nint RestoredLayout { get; private set; }

        public bool TrySelectEnglish(out nint priorLayout)
        {
            SelectCalls++;
            priorLayout = 0x0411;
            return true;
        }

        public void Restore(nint priorLayout)
        {
            RestoreCalls++;
            RestoredLayout = priorLayout;
        }
    }
}
