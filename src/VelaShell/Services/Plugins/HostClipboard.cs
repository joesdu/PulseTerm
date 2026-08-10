using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VelaShell.PluginSdk.Clipboard;

namespace VelaShell.Services.Plugins;

/// <summary>
/// 插件剪贴板能力(<see cref="IClipboardApi" />)的宿主实现:经主窗口的系统剪贴板,
/// 调用从任意线程封送到 UI 线程。隔离插件同样路由到这里(经 RPC),语义一致。
/// </summary>
internal sealed class HostClipboard : IClipboardApi
{
    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(async () =>
            Clipboard() is { } clipboard ? await clipboard.TryGetTextAsync() : null);
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Clipboard() is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        });
    }

    private static IClipboard? Clipboard() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow?.Clipboard;
}
