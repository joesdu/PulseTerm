using System;
using System.Threading.Tasks;

namespace PulseTerm.App.Services;

public interface IUpdateService
{
    Task<bool> CheckForUpdateAsync();
    Task DownloadUpdateAsync(IProgress<int>? progress = null);
    void ApplyUpdateAndRestart();
    string? CurrentVersion { get; }
    string? AvailableVersion { get; }
}
