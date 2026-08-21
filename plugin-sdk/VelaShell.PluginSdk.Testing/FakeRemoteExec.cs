using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IRemoteExecApi" /> 的测试替身:优先走 <see cref="ResultHandler" />,
/// 其次 <see cref="Handler" />,否则按顺序吐出 <see cref="Responses" /> 队列;
/// 都没有时返回空输出与退出码 0。全部调用记录在 <see cref="Executed" />。
/// </summary>
public sealed class FakeRemoteExec : IRemoteExecApi
{
    /// <summary>脚本化应答:(sessionId, command) → 标准输出。退出码按 0、标准错误按空处理。</summary>
    public Func<string, string, string>? Handler { get; set; }

    /// <summary>
    /// 完整的脚本化应答:(sessionId, command) → 标准输出 / 标准错误 / 退出码。
    /// 要测"命令失败了界面怎么说"就用它 —— <see cref="Handler" /> 永远是成功的。
    /// </summary>
    public Func<string, string, ExecResult>? ResultHandler { get; set; }

    /// <summary>顺序应答队列(无 <see cref="Handler" /> / <see cref="ResultHandler" /> 时使用)。</summary>
    public Queue<string> Responses { get; } = new();

    /// <summary>
    /// 流式执行的脚本化应答:(sessionId, command) → 要逐行回调的输出。
    /// 未设置时,流式执行退回复用一次性应答并把它按行拆开回调。
    /// </summary>
    public Func<string, string, IEnumerable<ExecOutput>>? StreamHandler { get; set; }

    /// <summary>全部已执行的 (sessionId, command) 记录(含流式)。</summary>
    public List<(string SessionId, string Command)> Executed { get; } = [];

    /// <summary>已发起的流式执行 (sessionId, command) 记录。</summary>
    public List<(string SessionId, string Command)> Streamed { get; } = [];

    /// <inheritdoc />
    public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Executed.Add((sessionId, command));
        return Task.FromResult(Respond(sessionId, command));
    }

    /// <inheritdoc />
    public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
        IProgress<ExecOutput> output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        Executed.Add((sessionId, command));
        Streamed.Add((sessionId, command));
        long lines = 0;
        foreach (ExecOutput line in Lines(sessionId, command))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Stream is ExecStream.StandardError && options?.IncludeStandardError is false)
            {
                continue;
            }
            output.Report(line);
            lines++;
        }
        return Task.FromResult(new ExecStreamResult(0, lines));
    }

    private ExecResult Respond(string sessionId, string command)
    {
        if (ResultHandler is { } detailed)
        {
            return detailed(sessionId, command);
        }
        string output = Handler?.Invoke(sessionId, command) ?? (Responses.Count > 0 ? Responses.Dequeue() : "");
        return new(output);
    }

    private IEnumerable<ExecOutput> Lines(string sessionId, string command)
    {
        if (StreamHandler is { } handler)
        {
            return handler(sessionId, command);
        }
        // 没有专门脚本时复用一次性应答:大多数测试只关心"这些行有没有被逐条送到",
        // 让它们不必为流式再写一份数据。
        ExecResult result = Respond(sessionId, command);
        return
        [
            .. Split(result.Output, ExecStream.StandardOutput),
            .. Split(result.Error, ExecStream.StandardError)
        ];
    }

    private static IEnumerable<ExecOutput> Split(string text, ExecStream stream) =>
        text.Length == 0
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                  .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                  .Select(line => new ExecOutput(stream, line));
}
