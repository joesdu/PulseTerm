using PulseTerm.Core.Models;

namespace PulseTerm.Core.Data;

public interface ISettingsService
{
    Task<AppSettings> GetSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
    Task<AppState> GetStateAsync();
    Task SaveStateAsync(AppState state);
}
