using VelaShell.PluginSdk.Events;

namespace VelaShell.PluginSdk;

/// <summary>宿主环境信息。<see cref="Locale" /> 与 <see cref="Theme" /> 为实时值,变更事件见 <see cref="IHostEvents" />。</summary>
public interface IHostInfo
{
    /// <summary>宿主应用版本(如 <c>0.0.1-dev</c>)。</summary>
    string AppVersion { get; }

    /// <summary>宿主支持的最高插件 apiLevel。</summary>
    int ApiLevel { get; }

    /// <summary>当前 UI 语言代码(如 <c>zh-CN</c>、<c>en</c>)。</summary>
    string Locale { get; }

    /// <summary>当前主题名称(如 <c>dark</c>、<c>light</c>、<c>system</c>)。</summary>
    string Theme { get; }
}
