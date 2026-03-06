using System;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class TunnelItemViewModel : ReactiveObject
{
    private readonly TunnelInfo _tunnelInfo;
    private TunnelStatus _status;
    private long _bytesTransferred;

    public TunnelItemViewModel(TunnelInfo tunnelInfo)
    {
        _tunnelInfo = tunnelInfo ?? throw new ArgumentNullException(nameof(tunnelInfo));
        _status = tunnelInfo.Status;
        _bytesTransferred = tunnelInfo.BytesTransferred;
    }

    public Guid Id => _tunnelInfo.Id;

    public string Name => _tunnelInfo.Config.Name;

    public TunnelType TunnelType => _tunnelInfo.Config.Type;

    public string LocalHost => _tunnelInfo.Config.LocalHost;

    public uint LocalPort => _tunnelInfo.Config.LocalPort;

    public string RemoteHost => _tunnelInfo.Config.RemoteHost;

    public uint RemotePort => _tunnelInfo.Config.RemotePort;

    public string DisplayRoute => $"{LocalHost}:{LocalPort} → {RemoteHost}:{RemotePort}";

    public string TypeBadge => TunnelType == TunnelType.LocalForward ? "L" : "R";

    public TunnelStatus Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set => this.RaiseAndSetIfChanged(ref _bytesTransferred, value);
    }

    public string FormattedBytes => FormatBytes(BytesTransferred);

    public bool IsActive => Status == TunnelStatus.Active;

    public static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }
}
