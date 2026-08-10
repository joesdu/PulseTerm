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

    public string Theme => theme?.CurrentTheme ?? "system";
}
