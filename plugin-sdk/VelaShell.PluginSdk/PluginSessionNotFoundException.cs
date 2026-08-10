namespace VelaShell.PluginSdk;

/// <summary>能力调用引用的会话不存在或已断开。</summary>
public sealed class PluginSessionNotFoundException : InvalidOperationException
{
    /// <summary>用会话 id 构造(标准消息)。</summary>
    public PluginSessionNotFoundException(string sessionId)
        : base($"Session '{sessionId}' does not exist or is not connected.")
        => SessionId = sessionId;

    /// <summary>用现成消息构造(跨进程错误还原路径,原始会话 id 可能不可得)。</summary>
    public PluginSessionNotFoundException(string sessionId, string message)
        : base(message)
        => SessionId = sessionId;

    /// <summary>引发异常的会话 id;跨进程还原时可能为空串。</summary>
    public string SessionId { get; }
}
