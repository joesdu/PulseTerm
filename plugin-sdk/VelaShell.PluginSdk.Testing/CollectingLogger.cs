using VelaShell.PluginSdk.Logging;

namespace VelaShell.PluginSdk.Testing;

/// <summary>一条被捕获的日志。</summary>
/// <param name="Level">级别。</param>
/// <param name="Message">消息。</param>
/// <param name="Exception">随附异常(可空)。</param>
public sealed record CapturedLogEntry(PluginLogLevel Level, string Message, Exception? Exception);

/// <summary><see cref="IPluginLogger" /> 的收集实现:日志进内存列表,断言用。线程安全。</summary>
public sealed class CollectingLogger : IPluginLogger
{
    private readonly List<CapturedLogEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>捕获日志的快照。</summary>
    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public void Write(PluginLogLevel level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            _entries.Add(new(level, message, exception));
        }
    }
}
