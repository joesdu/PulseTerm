namespace PulseTerm.Core.Services;

public interface IThemeService
{
    /// <summary>
    /// Gets the current theme name ("dark" or "light").
    /// </summary>
    string CurrentTheme { get; }

    /// <summary>
    /// Switches the active theme at runtime.
    /// </summary>
    /// <param name="themeName">"dark" or "light"</param>
    void SetTheme(string themeName);

    /// <summary>
    /// Raised when the theme changes. Argument is the new theme name.
    /// </summary>
    event Action<string>? ThemeChanged;
}
