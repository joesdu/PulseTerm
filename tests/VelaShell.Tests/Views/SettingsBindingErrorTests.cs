using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 设置窗口每一页都要绑得干净。
/// </summary>
/// <remarks>
/// 绑定错误只写调试输出、不抛异常,所以它们会一直堆在那儿没人管;堆多了的真正代价是
/// <b>把真错误淹掉</b> —— 一次打开设置刷出几十条"Value is null",谁也不会再去读这段日志。
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class SettingsBindingErrorTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsBindingErrorTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void EverySettingsPage_BindsWithoutErrors()
    {
        OnUi(() =>
        {
            var sink = new BindingErrorSink();
            ILogSink? previous = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                // 先拿一条必错的绑定验明接收器确实在工作,否则下面的"没有错误"只是空跑。
                var canary = new TextBlock();
                canary.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Missing.Deeper"));
                var probe = new Window { Content = canary, DataContext = new object() };
                probe.Show();
                Dispatcher.UIThread.RunJobs();
                probe.Close();
                Assert.IsNotEmpty(sink.Errors, "绑定错误接收器没有生效,后面的断言会一直是空跑。");
                sink.Errors.Clear();

                ISettingsService settings = Substitute.For<ISettingsService>();
                IThemeService theme = Substitute.For<IThemeService>();
                settings.GetSettingsAsync().Returns(new AppSettings());
                // 带上快捷命令仓储:真实应用里它总是有的,不给就只是在测另一种配置。
                IQuickCommandRepository snippets = Substitute.For<IQuickCommandRepository>();
                snippets.LoadAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new QuickCommandLoadResult(new QuickCommandData())));
                var viewModel = new SettingsViewModel(settings, theme, quickCommandRepository: snippets);
                var window = new SettingsView { DataContext = viewModel };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                foreach (SettingsSectionKey key in Enum.GetValues<SettingsSectionKey>())
                {
                    viewModel.SelectSection(key);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                window.Close();
            }
            finally
            {
                Logger.Sink = previous;
            }

            Assert.IsEmpty(
                sink.Errors,
                $"出现了 {sink.Errors.Count} 条绑定错误,例如:{Environment.NewLine}"
                + string.Join(Environment.NewLine, sink.Errors.Distinct()));
        });
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>只收集绑定相关的告警/错误,供上面的回归测试断言。</summary>
    private sealed class BindingErrorSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate);
            }
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate + " | " + string.Join(", ", propertyValues));
            }
        }
    }
}
