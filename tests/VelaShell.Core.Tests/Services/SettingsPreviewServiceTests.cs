using VelaShell.Core.Models;
using VelaShell.Core.Services;

namespace VelaShell.Core.Tests.Services;

[TestClass]
public sealed class SettingsPreviewServiceTests
{
    private SettingsPreviewService _sut = null!;

    [TestInitialize]
    public void Setup() => _sut = new SettingsPreviewService();

    [TestMethod]
    public void PreviewWindowOpacity_InvokeOnce_EmitsExactlyOneEvent()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(50);

        Assert.HasCount(1, received);
        Assert.AreEqual(50, received[0]);
    }

    [TestMethod]
    public void PreviewWindowOpacity_InvokeMultiple_EmitsInOrder()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(30);
        _sut.PreviewWindowOpacity(60);
        _sut.PreviewWindowOpacity(90);

        Assert.AreSequenceEqual([30, 60, 90], received);
    }

    [TestMethod]
    public void PreviewWindowOpacity_BelowMin_ClampsTo10()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(0);

        Assert.HasCount(1, received);
        Assert.AreEqual(10, received[0]);
    }

    [TestMethod]
    public void PreviewWindowOpacity_AboveMax_ClampsTo100()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(150);

        Assert.HasCount(1, received);
        Assert.AreEqual(100, received[0]);
    }

    [TestMethod]
    public void PreviewWindowOpacity_Boundary10_PassesThrough()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(10);

        Assert.HasCount(1, received);
        Assert.AreEqual(10, received[0]);
    }

    [TestMethod]
    public void PreviewWindowOpacity_Boundary100_PassesThrough()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(100);

        Assert.HasCount(1, received);
        Assert.AreEqual(100, received[0]);
    }

    [TestMethod]
    public void OpacityChannel_And_SnapshotChannel_AreIndependent()
    {
        var opacityReceived = new List<int>();
        var snapshotReceived = new List<AppSettings>();

        _sut.WindowOpacityPreviewRequested += opacityReceived.Add;
        _sut.PreviewRequested += snapshotReceived.Add;

        var settings = new AppSettings();
        _sut.Preview(settings);
        _sut.PreviewWindowOpacity(42);

        Assert.HasCount(1, snapshotReceived);
        Assert.AreEqual(settings, snapshotReceived[0]);
        Assert.HasCount(1, opacityReceived);
        Assert.AreEqual(42, opacityReceived[0]);
    }

    [TestMethod]
    public void PreviewWindowOpacity_NegativeValue_ClampsTo10()
    {
        var received = new List<int>();
        _sut.WindowOpacityPreviewRequested += received.Add;

        _sut.PreviewWindowOpacity(-5);

        Assert.HasCount(1, received);
        Assert.AreEqual(10, received[0]);
    }

    [TestMethod]
    public void PreviewWindowOpacity_NoSubscribers_DoesNotThrow() =>
        // No subscribers attached — should not throw.
        _sut.PreviewWindowOpacity(50);

    [TestMethod]
    public void Preview_SnapshotStillWorks_Unchanged()
    {
        var snapshotReceived = new List<AppSettings>();
        _sut.PreviewRequested += snapshotReceived.Add;

        var settings = new AppSettings();
        _sut.Preview(settings);

        Assert.HasCount(1, snapshotReceived);
        Assert.AreEqual(settings, snapshotReceived[0]);
    }
}
