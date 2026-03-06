using PulseTerm.Core.Models;

namespace PulseTerm.Core.Data;

public class SettingsService : ISettingsService
{
    private readonly JsonDataStore _dataStore;
    private readonly string _settingsPath;
    private readonly string _statePath;

    public SettingsService(JsonDataStore dataStore, string? basePath = null)
    {
        _dataStore = dataStore;
        
        if (string.IsNullOrEmpty(basePath))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            basePath = Path.Combine(userProfile, ".pulseterm");
        }
        
        _settingsPath = Path.Combine(basePath, "settings.json");
        _statePath = Path.Combine(basePath, "state.json");
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        return await _dataStore.LoadAsync<AppSettings>(_settingsPath).ConfigureAwait(false) ?? new AppSettings();
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await _dataStore.SaveAsync(_settingsPath, settings).ConfigureAwait(false);
    }

    public async Task<AppState> GetStateAsync()
    {
        return await _dataStore.LoadAsync<AppState>(_statePath).ConfigureAwait(false) ?? new AppState();
    }

    public async Task SaveStateAsync(AppState state)
    {
        await _dataStore.SaveAsync(_statePath, state).ConfigureAwait(false);
    }
}
