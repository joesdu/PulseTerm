using VelaShell.Terminal.Rendering;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Rendering")]
public class ScrollOffsetTests
{
    [TestMethod]
    public void AtLiveBottom_AlwaysStaysAtBottom() =>
        // Offset 0 means "following output"; new lines must keep it at the bottom.
        Assert.AreEqual(0, VelaTerminalControl.PinScrollOffset(0, 10, 25));

    [TestMethod]
    public void ScrolledUp_PinsViewByGrowingWithScrollback() =>
        // Viewing history 5 lines up; scrollback grew 10 -> 13, so the same content is now
        // 8 lines up. Without this the view would drift down toward the live output.
        Assert.AreEqual(8, VelaTerminalControl.PinScrollOffset(5, 10, 13));

    [TestMethod]
    public void ScrolledUp_NoGrowth_KeepsOffset() => Assert.AreEqual(5, VelaTerminalControl.PinScrollOffset(5, 10, 10));

    [TestMethod]
    public void ScrolledUp_ClampsToNewScrollback() =>
        // Can never scroll past the top of the available history.
        Assert.AreEqual(12, VelaTerminalControl.PinScrollOffset(100, 10, 12));

    [TestMethod]
    public void ScrollbackShrank_ClampsDown() =>
        // Alt-screen exit / clear can shrink scrollback; the pinned offset clamps to fit.
        Assert.AreEqual(4, VelaTerminalControl.PinScrollOffset(5, 10, 4));

    [TestMethod]
    public void EmptyScrollback_ReturnsZero() => Assert.AreEqual(0, VelaTerminalControl.PinScrollOffset(5, 10, 0));
}
