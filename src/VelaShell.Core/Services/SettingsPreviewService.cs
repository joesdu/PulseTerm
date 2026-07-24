using VelaShell.Core.Models;

namespace VelaShell.Core.Services;

/// <summary>
/// 设置「即时预览」通道:设置窗口在外观项(主题/强调色除外,它们直接走 IThemeService)
/// 变化时广播一份未持久化的设置快照,主窗口与主 VM 按 SettingsSaved 同样的方式应用;
/// 取消/未保存关闭时用打开设置前的基线快照再广播一次即恢复原状。
/// </summary>
public interface ISettingsPreviewService
{
    /// <summary>预览快照(未持久化)。在 UI 线程触发。</summary>
    event Action<AppSettings>? PreviewRequested;

    /// <summary>广播一份未持久化的设置快照以触发即时预览。</summary>
    void Preview(AppSettings settings);

    /// <summary>窗口不透明度即时预览事件(已钳位到 10..100)。绕过 AppSettings 快照路径,在 UI 线程触发。</summary>
    event Action<int>? WindowOpacityPreviewRequested;

    /// <summary>广播窗口不透明度百分比(钳位到 10..100),不产生 AppSettings/JSON 开销。</summary>
    void PreviewWindowOpacity(int percent);

    /// <summary>
    /// 背景图/内容背景不透明度即时预览事件(均为百分比)。绕过防抖的 JSON 快照路径,
    /// 使拖动滑杆时不透明度线性平滑跟随、且不重复解码图片。在 UI 线程触发。
    /// </summary>
    event Action<(int Image, int Content)>? BackgroundOpacityPreviewRequested;

    /// <summary>广播背景图/内容背景不透明度百分比,不产生 AppSettings/JSON 开销。</summary>
    void PreviewBackgroundOpacity(int imageOpacity, int contentOpacity);
}

/// <summary><see cref="ISettingsPreviewService" /> 的默认实现:将预览请求同步转发给订阅者。</summary>
public sealed class SettingsPreviewService : ISettingsPreviewService
{
    /// <inheritdoc />
    public event Action<AppSettings>? PreviewRequested;

    /// <inheritdoc />
    public void Preview(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PreviewRequested?.Invoke(settings);
    }

    /// <inheritdoc />
    public event Action<int>? WindowOpacityPreviewRequested;

    /// <inheritdoc />
    public void PreviewWindowOpacity(int percent)
    {
        WindowOpacityPreviewRequested?.Invoke(Math.Clamp(percent, 10, 100));
    }

    /// <inheritdoc />
    public event Action<(int Image, int Content)>? BackgroundOpacityPreviewRequested;

    /// <inheritdoc />
    public void PreviewBackgroundOpacity(int imageOpacity, int contentOpacity)
    {
        BackgroundOpacityPreviewRequested?.Invoke(
            (Math.Clamp(imageOpacity, 0, 100), Math.Clamp(contentOpacity, 0, 100)));
    }
}
