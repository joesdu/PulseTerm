using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using VelaShell.Core.Resources;

namespace VelaShell.Services;

/// <summary>
/// 系统托盘图标(设置 → 常规 → 关闭时最小化到托盘)。使用应用图标资源
/// (Assets/velashell.ico)。仅在设置开启时挂载,关闭时移除。
/// </summary>
public sealed class TrayIconService(Application app) : IDisposable
{
    private TrayIcon? _trayIcon;

    /// <summary>托盘图标当前是否已挂载。</summary>
    public bool IsActive => _trayIcon is not null;

    /// <summary>移除并释放托盘图标(若已挂载)。</summary>
    public void Dispose()
    {
        if (_trayIcon is null)
        {
            return;
        }
        TrayIcon.SetIcons(app, []);
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    /// <summary>点击托盘图标 / “显示主窗口”菜单。</summary>
    public event Action? ShowRequested;

    /// <summary>“退出”菜单:调用方负责绕过托盘拦截真正退出。</summary>
    public event Action? ExitRequested;

    /// <summary>根据设置挂载或移除系统托盘图标。</summary>
    /// <param name="enabled">true 挂载托盘图标并绑定右键菜单;false 移除。</param>
    public void SetEnabled(bool enabled)
    {
        if (enabled == IsActive)
        {
            return;
        }
        if (enabled)
        {
            _trayIcon = new()
            {
                Icon = CreateGeneratedIcon(),
                ToolTipText = "VelaShell",
                Menu = BuildMenu()
            };
            _trayIcon.Clicked += (_, _) => ShowRequested?.Invoke();
            TrayIcon.SetIcons(app, [_trayIcon]);
        }
        else
        {
            Dispose();
        }
    }

    private NativeMenu BuildMenu()
    {
        var show = new NativeMenuItem(Strings.Get("Tray_ShowMainWindow"));
        show.Click += (_, _) => ShowRequested?.Invoke();
        var exit = new NativeMenuItem(Strings.Get("Tray_ExitApp"));
        exit.Click += (_, _) => ExitRequested?.Invoke();
        return new()
        {
            Items = { show, new NativeMenuItemSeparator(), exit }
        };
    }

    /// <summary>托盘图标:加载应用图标资源(Assets/velashell.ico)。</summary>
    private static WindowIcon CreateGeneratedIcon()
    {
        using Stream stream = AssetLoader.Open(new("avares://VelaShell/Assets/velashell.ico"));
        return new(stream);
    }
}
