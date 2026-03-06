using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using PulseTerm.App.ViewModels;
using PulseTerm.App.Views;
using PulseTerm.Core.Services;

namespace PulseTerm.App;

public partial class App : Application
{
    public static IThemeService ThemeService { get; } = new ThemeService("dark");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        ThemeService.ThemeChanged += OnThemeChanged;
        ApplyThemeVariant(ThemeService.CurrentTheme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnThemeChanged(string themeName)
    {
        ApplyThemeVariant(themeName);
    }

    private void ApplyThemeVariant(string themeName)
    {
        RequestedThemeVariant = themeName.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Dark,
        };
    }
}