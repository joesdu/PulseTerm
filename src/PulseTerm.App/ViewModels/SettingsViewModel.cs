using System;
using System.Reactive;
using System.Threading.Tasks;
using PulseTerm.Core.Data;
using PulseTerm.Core.Services;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class SettingsViewModel : ReactiveObject
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;

    private string _language = "en";
    private string _theme = "dark";
    private string _terminalFont = "JetBrains Mono";
    private int _terminalFontSize = 14;
    private int _scrollbackLines = 10000;
    private int _defaultPort = 22;

    public SettingsViewModel(ISettingsService settingsService, IThemeService themeService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));

        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        CancelCommand = ReactiveCommand.Create(() => { });
    }

    public string Language
    {
        get => _language;
        set => this.RaiseAndSetIfChanged(ref _language, value);
    }

    public string Theme
    {
        get => _theme;
        set => this.RaiseAndSetIfChanged(ref _theme, value);
    }

    public string TerminalFont
    {
        get => _terminalFont;
        set => this.RaiseAndSetIfChanged(ref _terminalFont, value);
    }

    public int TerminalFontSize
    {
        get => _terminalFontSize;
        set => this.RaiseAndSetIfChanged(ref _terminalFontSize, value);
    }

    public int ScrollbackLines
    {
        get => _scrollbackLines;
        set => this.RaiseAndSetIfChanged(ref _scrollbackLines, value);
    }

    public int DefaultPort
    {
        get => _defaultPort;
        set => this.RaiseAndSetIfChanged(ref _defaultPort, value);
    }

    public string[] AvailableLanguages { get; } = new[] { "en", "zh-CN" };

    public string[] AvailableThemes { get; } = new[] { "dark", "light" };

    public ReactiveCommand<Unit, Unit> LoadCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    private async Task LoadAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        Language = settings.Language;
        Theme = settings.Theme;
        TerminalFont = settings.TerminalFont;
        TerminalFontSize = settings.TerminalFontSize;
        ScrollbackLines = settings.ScrollbackLines;
        DefaultPort = settings.DefaultPort;
    }

    private async Task SaveAsync()
    {
        var settings = new Core.Models.AppSettings
        {
            Language = Language,
            Theme = Theme,
            TerminalFont = TerminalFont,
            TerminalFontSize = TerminalFontSize,
            ScrollbackLines = ScrollbackLines,
            DefaultPort = DefaultPort
        };

        await _settingsService.SaveSettingsAsync(settings);
        _themeService.SetTheme(Theme);
    }
}
