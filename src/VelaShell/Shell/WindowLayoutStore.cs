using Avalonia.Controls;
using VelaShell.Core.Data;

namespace VelaShell.Shell;

/// <summary>一个辅助窗口记住的几何信息(存进 SonnetDB 的 windowLayout 集合)。</summary>
public sealed class WindowLayout
{
    /// <summary>上次的普通态宽度(逻辑像素)。最大化时不写入,否则还原后会变成整屏尺寸。</summary>
    public double Width { get; set; }

    /// <summary>上次的普通态高度(逻辑像素)。</summary>
    public double Height { get; set; }

    /// <summary>关闭时是否处于最大化。</summary>
    public bool Maximized { get; set; }
}

/// <summary>
/// 辅助窗口(任务管理器等)的尺寸持久化。主窗口的尺寸走设置里的
/// <c>Appearance.LastWindowWidth</c>,那是用户可见的一项设置;辅助窗口纯属使用习惯,
/// 不该塞进设置页,因此落在文档存储里。
/// </summary>
/// <param name="store">文档存储;为 null(无头测试)时所有操作退化为空操作。</param>
public sealed class WindowLayoutStore(IAppDataStore? store)
{
    private const string Collection = "windowLayout";

    /// <summary>读取某个窗口上次的几何信息;没存过或读取失败时返回 null。</summary>
    /// <param name="key">窗口标识,如 "processManager"。</param>
    public async Task<WindowLayout?> LoadAsync(string key)
    {
        if (store is null)
        {
            return null;
        }
        try
        {
            return await store.GetAsync<WindowLayout>(Collection, key).ConfigureAwait(false);
        }
        catch
        {
            // 记住尺寸是锦上添花,读不出来就用默认尺寸开,不能因此打不开窗口。
            return null;
        }
    }

    /// <summary>把窗口当前的几何信息写回存储;失败静默忽略。</summary>
    /// <param name="key">窗口标识。</param>
    /// <param name="window">要记录的窗口。</param>
    public async Task SaveAsync(string key, Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (store is null)
        {
            return;
        }
        WindowLayout layout = new() { Maximized = window.WindowState == WindowState.Maximized };
        if (window.WindowState == WindowState.Normal)
        {
            layout.Width = window.Width;
            layout.Height = window.Height;
        }
        else
        {
            // 最大化/最小化时 Width/Height 已不是用户调过的那个尺寸,沿用上次记录的值。
            WindowLayout? previous = await LoadAsync(key).ConfigureAwait(false);
            layout.Width = previous?.Width ?? 0;
            layout.Height = previous?.Height ?? 0;
        }
        try
        {
            await store.UpsertAsync(Collection, key, layout).ConfigureAwait(false);
        }
        catch
        {
            // 同上:记不住尺寸不值得打扰用户。
        }
    }

    /// <summary>
    /// 把记录的几何信息套用到窗口,必须在 Show 之前调用。尺寸会夹取到窗口自身的最小值与
    /// 当前屏幕工作区之间 —— 换了小显示器之后,上次在大屏上存的尺寸会让窗口大半在屏幕外。
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="layout">读到的几何信息;为 null 时不做任何改动。</param>
    public static void Apply(Window window, WindowLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (layout is null)
        {
            return;
        }
        if (layout is { Width: > 0, Height: > 0 })
        {
            double maxWidth = double.PositiveInfinity;
            double maxHeight = double.PositiveInfinity;
            try
            {
                if (window.Screens.Primary is { Scaling: > 0 } screen)
                {
                    maxWidth = screen.WorkingArea.Width / screen.Scaling;
                    maxHeight = screen.WorkingArea.Height / screen.Scaling;
                }
            }
            catch
            {
                // 屏幕信息在窗口显示前不一定可用(无头/远程桌面):拿不到就不夹取上界。
            }
            window.Width = Math.Clamp(layout.Width, window.MinWidth, maxWidth);
            window.Height = Math.Clamp(layout.Height, window.MinHeight, maxHeight);
        }
        if (layout.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}
