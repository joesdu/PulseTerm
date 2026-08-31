using VelaShell.Core.Localization;
using VelaShell.Core.Services;
using VelaShell.PluginSdk;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary><see cref="IHostInfo" /> 实现:语言与主题为实时取值。</summary>
internal sealed class HostInfoCapability(string appVersion, IThemeService? theme, ILocalizationService? localization)
    : IHostInfo
{
    public string AppVersion { get; } = appVersion;

    public int ApiLevel => VelaPluginApi.Level;

    public string Locale => localization?.CurrentLanguage ?? "en";

    // 具名主题不外泄:插件只认 dark / light / system(见 PluginEventHub 的同一处处理)。
    public string Theme => VelaShell.Core.Models.UiThemeCatalog.VariantName(theme?.CurrentTheme);
}
