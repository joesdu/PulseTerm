using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;

namespace PulseTerm.App.Services;

public class UpdateService : IUpdateService
{
    private readonly UpdateManager _updateManager;
    private UpdateInfo? _updateInfo;

    public UpdateService(string updateUrl, IVelopackLocator? locator = null)
    {
        if (updateUrl is null)
            throw new ArgumentNullException(nameof(updateUrl));

        _updateManager = new UpdateManager(updateUrl, locator: locator);
    }

    public string? CurrentVersion
    {
        get
        {
            if (_updateManager.IsInstalled)
                return _updateManager.CurrentVersion?.ToString();

            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly.GetName().Version?.ToString();
        }
    }

    public string? AvailableVersion => _updateInfo?.TargetFullRelease?.Version?.ToString();

    public async Task<bool> CheckForUpdateAsync()
    {
        if (!_updateManager.IsInstalled)
            return false;

        try
        {
            _updateInfo = await _updateManager.CheckForUpdatesAsync();
            return _updateInfo != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task DownloadUpdateAsync(IProgress<int>? progress = null)
    {
        if (_updateInfo == null)
            return;

        await _updateManager.DownloadUpdatesAsync(
            _updateInfo,
            progress != null ? p => progress.Report(p) : null);
    }

    public void ApplyUpdateAndRestart()
    {
        if (_updateInfo?.TargetFullRelease == null)
            throw new InvalidOperationException("No update has been downloaded. Call CheckForUpdateAsync and DownloadUpdateAsync first.");

        _updateManager.ApplyUpdatesAndRestart(_updateInfo.TargetFullRelease);
    }
}
