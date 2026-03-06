using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(PulseTerm.App.Tests.TestAppBuilder))]

namespace PulseTerm.App.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<PulseTerm.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
