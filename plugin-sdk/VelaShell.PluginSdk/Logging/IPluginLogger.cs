namespace VelaShell.PluginSdk.Logging;

/// <summary>插件日志级别。</summary>
public enum PluginLogLevel
{
    /// <summary>调试细节,默认可能不落盘。</summary>
    Debug,

    /// <summary>常规信息。</summary>
    Information,

    /// <summary>可恢复的异常情况。</summary>
    Warning,

    /// <summary>失败,插件某项功能不可用。</summary>
    Error
}

/// <summary>
/// 插件日志通道:写入宿主日志管道,自动带 <c>[Plugin:&lt;id&gt;]</c> 前缀。
/// 实现是线程安全的,可在任意线程调用。
/// </summary>
public interface IPluginLogger
{
    /// <summary>写一条日志。</summary>
    void Write(PluginLogLevel level, string message, Exception? exception = null);

    /// <summary>写调试日志。</summary>
    void Debug(string message) => Write(PluginLogLevel.Debug, message);

    /// <summary>写信息日志。</summary>
    void Info(string message) => Write(PluginLogLevel.Information, message);

    /// <summary>写警告日志。</summary>
    void Warn(string message, Exception? exception = null) => Write(PluginLogLevel.Warning, message, exception);

    /// <summary>写错误日志。</summary>
    void Error(string message, Exception? exception = null) => Write(PluginLogLevel.Error, message, exception);
}
