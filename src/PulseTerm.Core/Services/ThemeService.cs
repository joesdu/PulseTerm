namespace PulseTerm.Core.Services;

public class ThemeService : IThemeService
{
    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase) { "dark", "light" };

    private string _currentTheme;

    public ThemeService(string initialTheme = "dark")
    {
        _currentTheme = ValidThemes.Contains(initialTheme) ? initialTheme.ToLowerInvariant() : "dark";
    }

    public string CurrentTheme => _currentTheme;

    public event Action<string>? ThemeChanged;

    public void SetTheme(string themeName)
    {
        var normalized = themeName.ToLowerInvariant();

        if (!ValidThemes.Contains(normalized))
            throw new ArgumentException($"Invalid theme: '{themeName}'. Valid themes: dark, light.", nameof(themeName));

        if (_currentTheme == normalized)
            return;

        _currentTheme = normalized;
        ThemeChanged?.Invoke(_currentTheme);
    }
}
