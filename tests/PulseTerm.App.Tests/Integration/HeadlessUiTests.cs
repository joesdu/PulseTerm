using System.Globalization;
using FluentAssertions;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Localization;
using PulseTerm.Core.Services;

namespace PulseTerm.App.Tests.Integration;

[Collection("IntegrationTests")]
public class HeadlessUiTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void MainWindowViewModel_Initializes_WithAllSubViewModels()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Sidebar.Should().NotBeNull();
        viewModel.TabBar.Should().NotBeNull();
        viewModel.StatusBar.Should().NotBeNull();
        viewModel.OpenSettingsCommand.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MainWindowViewModel_Sidebar_IsCorrectType()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Sidebar.Should().NotBeNull();
        viewModel.Sidebar.Should().BeOfType<SidebarViewModel>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MainWindowViewModel_TabBar_IsCorrectType()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.TabBar.Should().NotBeNull();
        viewModel.TabBar.Should().BeOfType<TabBarViewModel>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MainWindowViewModel_StatusBar_IsCorrectType()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.StatusBar.Should().NotBeNull();
        viewModel.StatusBar.Should().BeOfType<StatusBarViewModel>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ThemeService_SwitchToDark_AppliesCorrectly()
    {
        var themeService = new ThemeService("light");

        themeService.SetTheme("dark");

        themeService.CurrentTheme.Should().Be("dark");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ThemeService_SwitchToLight_AppliesCorrectly()
    {
        var themeService = new ThemeService("dark");

        themeService.SetTheme("light");

        themeService.CurrentTheme.Should().Be("light");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ThemeService_RoundTrip_MaintainsState()
    {
        var themeService = new ThemeService("dark");
        var events = new List<string>();
        themeService.ThemeChanged += name => events.Add(name);

        themeService.SetTheme("light");
        themeService.SetTheme("dark");
        themeService.SetTheme("light");

        themeService.CurrentTheme.Should().Be("light");
        events.Should().HaveCount(3);
        events.Should().Equal("light", "dark", "light");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LocalizationService_DefaultLanguage_ReturnsEnglishStrings()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");

            var service = new LocalizationService();

            service.CurrentLanguage.Should().Be("en");
            var appName = service.GetString("AppName");
            appName.Should().NotBeNullOrEmpty();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LocalizationService_SetLanguageChinese_ChangesCurrentLanguage()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            var service = new LocalizationService();

            service.SetLanguage("zh-CN");

            service.CurrentLanguage.Should().Be("zh-CN");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LocalizationService_SwitchLanguage_RoundTrip()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            var service = new LocalizationService();

            service.SetLanguage("zh-CN");
            service.CurrentLanguage.Should().Be("zh-CN");

            service.SetLanguage("en");
            service.CurrentLanguage.Should().Be("en");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LocalizationService_MissingKey_ReturnsKeyAsDefault()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            var service = new LocalizationService();

            var result = service.GetString("NonExistentKey_XYZ_12345");

            result.Should().Be("NonExistentKey_XYZ_12345");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
