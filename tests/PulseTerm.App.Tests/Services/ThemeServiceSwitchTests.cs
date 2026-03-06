using FluentAssertions;
using PulseTerm.Core.Services;

namespace PulseTerm.App.Tests.Services;

public class ThemeServiceSwitchTests
{
    [Fact]
    [Trait("Category", "Theme")]
    public void SetTheme_Light_ChangesCurrentTheme()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("light");

        sut.CurrentTheme.Should().Be("light");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void SetTheme_Light_FiresThemeChangedEvent()
    {
        var sut = new ThemeService("dark");
        string? received = null;
        sut.ThemeChanged += name => received = name;

        sut.SetTheme("light");

        received.Should().Be("light");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void SetTheme_Dark_SwitchesBackFromLight()
    {
        var sut = new ThemeService("light");

        sut.SetTheme("dark");

        sut.CurrentTheme.Should().Be("dark");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void SetTheme_SameTheme_DoesNotFireEvent()
    {
        var sut = new ThemeService("dark");
        var fired = false;
        sut.ThemeChanged += _ => fired = true;

        sut.SetTheme("dark");

        fired.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void SetTheme_InvalidTheme_ThrowsArgumentException()
    {
        var sut = new ThemeService("dark");

        var act = () => sut.SetTheme("ocean");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Invalid theme*ocean*");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void SetTheme_CaseInsensitive_AcceptsUpperCase()
    {
        var sut = new ThemeService("dark");

        sut.SetTheme("Light");

        sut.CurrentTheme.Should().Be("light");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void Constructor_DefaultsToValid_Theme()
    {
        var darkSut = new ThemeService("dark");
        darkSut.CurrentTheme.Should().Be("dark");

        var lightSut = new ThemeService("light");
        lightSut.CurrentTheme.Should().Be("light");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void Constructor_InvalidTheme_DefaultsToDark()
    {
        var sut = new ThemeService("neon");

        sut.CurrentTheme.Should().Be("dark");
    }

    [Fact]
    [Trait("Category", "Theme")]
    public void RoundTrip_DarkToLightToDark_AllEventsReceived()
    {
        var sut = new ThemeService("dark");
        var events = new List<string>();
        sut.ThemeChanged += name => events.Add(name);

        sut.SetTheme("light");
        sut.SetTheme("dark");
        sut.SetTheme("light");

        events.Should().Equal("light", "dark", "light");
    }
}
