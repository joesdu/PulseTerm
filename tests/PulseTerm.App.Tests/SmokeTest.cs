using Avalonia.Headless.XUnit;

namespace PulseTerm.App.Tests;

public class SmokeTest
{
    [AvaloniaFact]
    public void SmokeTest_AppInitializes()
    {
        var app = new App();
        Assert.NotNull(app);
    }
}
