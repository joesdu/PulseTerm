namespace PulseTerm.Core.Localization;

/// <summary>
/// Service for accessing localized strings at runtime
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Gets a localized string by key
    /// </summary>
    /// <param name="key">The resource key</param>
    /// <returns>Localized string, or key name if not found</returns>
    string GetString(string key);
    
    /// <summary>
    /// Gets the current UI language code (e.g., "en", "zh-CN")
    /// </summary>
    string CurrentLanguage { get; }
    
    /// <summary>
    /// Sets the UI language for the application
    /// </summary>
    /// <param name="language">Language code (e.g., "en", "zh-CN")</param>
    void SetLanguage(string language);
}
