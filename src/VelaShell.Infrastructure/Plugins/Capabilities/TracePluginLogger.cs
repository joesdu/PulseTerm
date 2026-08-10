using System.Diagnostics;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>插件日志的默认落点:宿主 Trace 管道,带插件 id 与级别前缀。零分配热路径不适用日志,无需更重的机制。</summary>
internal sealed class TracePluginLogger(string pluginId) : IPluginLogger
{
    public void Write(PluginLogLevel level, string message, Exception? exception = null)
    {
        Trace.WriteLine(exception is null
            ? $"[Plugin:{pluginId}] [{level}] {message}"
            : $"[Plugin:{pluginId}] [{level}] {message} — {exception}");
    }
}
