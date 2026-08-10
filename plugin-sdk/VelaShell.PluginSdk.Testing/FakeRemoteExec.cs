using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IRemoteExecApi" /> 的测试替身:优先走 <see cref="Handler" />,
/// 否则按顺序吐出 <see cref="Responses" /> 队列;都没有时返回空输出。
/// 全部调用记录在 <see cref="Executed" />。
/// </summary>
public sealed class FakeRemoteExec : IRemoteExecApi
{
    /// <summary>脚本化应答:(sessionId, command) → 输出。</summary>
    public Func<string, string, string>? Handler { get; set; }

    /// <summary>顺序应答队列(无 <see cref="Handler" /> 时使用)。</summary>
    public Queue<string> Responses { get; } = new();

    /// <summary>全部已执行的 (sessionId, command) 记录。</summary>
    public List<(string SessionId, string Command)> Executed { get; } = [];

    /// <inheritdoc />
    public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Executed.Add((sessionId, command));
        string output = Handler?.Invoke(sessionId, command)
                        ?? (Responses.Count > 0 ? Responses.Dequeue() : "");
        return Task.FromResult(new ExecResult(output));
    }
}
