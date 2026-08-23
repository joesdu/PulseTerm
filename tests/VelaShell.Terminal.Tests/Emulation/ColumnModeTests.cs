using System.Text;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Tests.Emulation;

/// <summary>
/// DECCOLM (<c>CSI ? 3 h/l</c>) gating — issue #253.
/// <para>
/// The xterm-family terminfo init string (<c>is2=\E[!p\E[?3;4l\E[4l\E&gt;</c>) carries <c>\E[?3l</c>,
/// so <c>screen</c>, <c>tmux</c>, <c>tput init</c> and <c>reset</c> all emit it on startup. Honouring it
/// unconditionally squeezed the grid to 80 columns and wiped the screen while the control's layout and
/// the remote PTY still believed the old width — the user saw "the selectable region shrank after
/// opening screen", and only switching tabs (which forces an arrange pass) put it back.
/// </para>
/// <para>
/// Mainstream behaviour, which these tests pin: DECCOLM is a complete no-op unless the application first
/// opts in with DECSET <c>?40</c> (xterm's <c>c132</c> resource; Windows Terminal's
/// <c>EnableDECCOLMSupport</c>, default false; VTE never resizes at all). When it *is* allowed, the
/// geometry change must be announced so the host can keep the PTY in sync.
/// </para>
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public class ColumnModeTests
{
    /// <summary>The literal init string an xterm-256color terminfo hands to screen/tmux/tput init.</summary>
    private const string XtermInitString = "\x1b[!p\x1b[?3;4l\x1b[4l\x1b>";

    private static TerminalEmulator New(int cols = 140, int rows = 40) => new(cols, rows);

    private static void Feed(TerminalEmulator e, string s) => e.Feed(Encoding.UTF8.GetBytes(s));

    [TestMethod]
    public void XtermInitString_LeavesGridAndContentAlone()
    {
        TerminalEmulator e = New();
        Feed(e, "pi@NanoPi-R2S:~$ screen -R test");
        Feed(e, XtermInitString);

        // The whole point of #253: neither the width nor the visible screen may move.
        Assert.AreEqual(140, e.Columns);
        Assert.AreEqual(40, e.Rows);
        Assert.AreEqual("pi@NanoPi-R2S:~$ screen -R test", e.Screen.ActiveLine(0).GetText().TrimEnd());
        Assert.AreEqual(140, e.Screen.ActiveLine(0).Columns);
    }

    [TestMethod]
    public void Deccolm_IsIgnoredByDefault()
    {
        TerminalEmulator e = New();
        Feed(e, "keep me");

        Feed(e, "\x1b[?3h"); // 132 columns
        Assert.AreEqual(140, e.Columns);
        Feed(e, "\x1b[?3l"); // 80 columns
        Assert.AreEqual(140, e.Columns);

        // Ignored means *fully* ignored: no erase, no cursor home.
        Assert.AreEqual("keep me", e.Screen.ActiveLine(0).GetText().TrimEnd());
        Assert.AreEqual(7, e.CursorX);
    }

    [TestMethod]
    public void Deccolm_DoesNotFireHostGeometryChanged_WhenIgnored()
    {
        TerminalEmulator e = New();
        int fired = 0;
        e.HostGeometryChanged += (_, _) => fired++;

        Feed(e, XtermInitString);
        Feed(e, "\x1b[?3h");

        Assert.AreEqual(0, fired);
    }

    [TestMethod]
    public void Deccolm_ResizesAndAnnounces_OnceAllowedByDecset40()
    {
        TerminalEmulator e = New();
        (int Cols, int Rows)? announced = null;
        e.HostGeometryChanged += (c, r) => announced = (c, r);

        Feed(e, "\x1b[?40h"); // xterm c132: allow 80 <-> 132
        Feed(e, "\x1b[?3h");

        Assert.AreEqual(132, e.Columns);
        Assert.AreEqual(40, e.Rows);
        Assert.AreEqual((132, 40), announced);

        Feed(e, "\x1b[?3l");
        Assert.AreEqual(80, e.Columns);
        Assert.AreEqual((80, 40), announced);
    }

    [TestMethod]
    public void Deccolm_ClearsScreenAndHomesCursor_OnceAllowed()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[?40h");
        Feed(e, "wipe me");
        Feed(e, "\x1b[?3l");

        Assert.AreEqual("", e.Screen.ActiveLine(0).GetText().TrimEnd());
        Assert.AreEqual(0, e.CursorX);
        Assert.AreEqual(0, e.CursorY);
    }

    [TestMethod]
    public void Decset40_Reset_TurnsColumnModeBackOff()
    {
        TerminalEmulator e = New();
        Feed(e, "\x1b[?40h");
        Feed(e, "\x1b[?40l");
        Feed(e, "\x1b[?3h");

        Assert.AreEqual(140, e.Columns);
    }

    [TestMethod]
    public void Decset40_SurvivesSoftAndFullReset_LikeXterm()
    {
        // xterm models c132 as a resource: neither DECSTR (CSI ! p) nor RIS (ESC c) clears it.
        TerminalEmulator e = New();
        Feed(e, "\x1b[?40h");
        Feed(e, "\x1b[!p");
        Feed(e, "\x1bc");

        Assert.IsTrue(e.Modes.AllowColumnMode);
    }
}
