using System.Globalization;
using System.Resources;

namespace PulseTerm.Core.Localization;

public class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager;
    
    public LocalizationService()
    {
        _resourceManager = new ResourceManager("PulseTerm.Core.Resources.Strings", typeof(Resources.Strings).Assembly);
    }
    
    public string GetString(string key)
    {
        try
        {
            var value = _resourceManager.GetString(key, CultureInfo.CurrentUICulture);
            return value ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return key;
        }
    }
    
    public string CurrentLanguage => CultureInfo.CurrentUICulture.Name;
    
    public void SetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var culture = new CultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
    }
}
