using VelaShell.Core.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public class ThemeServiceSwitchTests
{
    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_Light_ChangesCurrentTheme()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("light");

        Assert.AreEqual("light", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_Light_FiresThemeChangedEvent()
    {
        var sut = new ThemeService("dark");
        string? received = null;
        sut.ThemeChanged += name => received = name;

        sut.SetTheme("light");

        Assert.AreEqual("light", received);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_Dark_SwitchesBackFromLight()
    {
        var sut = new ThemeService("light");

        sut.SetTheme("dark");

        Assert.AreEqual("dark", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_SameTheme_DoesNotFireEvent()
    {
        var sut = new ThemeService("dark");
        bool fired = false;
        sut.ThemeChanged += _ => fired = true;

        sut.SetTheme("dark");

        Assert.IsFalse(fired);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_InvalidTheme_ThrowsArgumentException()
    {
        var sut = new ThemeService("dark");

        void act() => sut.SetTheme("ocean");

        Assert.ThrowsExactly<ArgumentException>(act);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_CaseInsensitive_AcceptsUpperCase()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("Light");

        Assert.AreEqual("light", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetTheme_System_ChangesCurrentTheme()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("system");

        Assert.AreEqual("system", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void Constructor_DefaultsToValid_Theme()
    {
        var darkSut = new ThemeService("dark");
        Assert.AreEqual("dark", darkSut.CurrentTheme);

        var lightSut = new ThemeService("light");
        Assert.AreEqual("light", lightSut.CurrentTheme);

        var systemSut = new ThemeService("system");
        Assert.AreEqual("system", systemSut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void Constructor_InvalidTheme_DefaultsToDark()
    {
        var sut = new ThemeService("neon");

        Assert.AreEqual("dark", sut.CurrentTheme);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void RoundTrip_DarkToLightToDark_AllEventsReceived()
    {
        var sut = new ThemeService("dark");
        var events = new List<string>();
        sut.ThemeChanged += events.Add;

        sut.SetTheme("light");
        sut.SetTheme("dark");
        sut.SetTheme("light");

        Assert.AreSequenceEqual(["light", "dark", "light"], events);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetAccent_ValidHex_UpdatesAndFires()
    {
        var sut = new ThemeService("dark");
        string? received = "unset";
        sut.AccentChanged += hex => received = hex;

        sut.SetAccent("#FF8800");

        Assert.AreEqual("#FF8800", sut.AccentColor);
        Assert.AreEqual("#FF8800", received);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetAccent_NormalizesMissingHashAndCase()
    {
        var sut = new ThemeService();

        sut.SetAccent("00d4aa");

        Assert.AreEqual("#00D4AA", sut.AccentColor);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetAccent_EmptyOrNull_ClearsToThemeDefault()
    {
        var sut = new ThemeService("dark", "#00D4AA");

        sut.SetAccent("");

        Assert.IsNull(sut.AccentColor);
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetAccent_Invalid_Throws()
    {
        var sut = new ThemeService();

        Assert.ThrowsExactly<ArgumentException>(() => sut.SetAccent("#12"));
        Assert.ThrowsExactly<ArgumentException>(() => sut.SetAccent("nothex"));
    }

    [TestMethod]
    [TestCategory("Theme")]
    public void SetAccent_SameValue_DoesNotFireAgain()
    {
        var sut = new ThemeService("dark", "#00D4AA");
        int count = 0;
        sut.AccentChanged += _ => count++;

        sut.SetAccent("#00D4AA");

        Assert.AreEqual(0, count);
    }
}
