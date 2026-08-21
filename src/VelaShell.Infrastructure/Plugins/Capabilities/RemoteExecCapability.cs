using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="IRemoteExecApi" /> 的桥接实现:复用宿主既有连接,经独立 exec 通道执行
/// (<see cref="ISshClientWrapper.RunCommandDetailedAsync" />),不进用户终端、不碰输入串行化队列。
/// <para>
/// 实例是**按插件**创建的(见 <c>PluginContext</c>),因此
/// <see cref="IRemoteExecApi.MaxConcurrentStreams" /> 这个并发上限天然就是按插件计的。
/// </para>
/// </summary>
internal sealed class RemoteExecCapability(ISshConnectionService connections) : IRemoteExecApi
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>当前在飞的流式执行条数(每插件)。</summary>
    private int _activeStreams;

    public async Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ISshClientWrapper client = Resolve(sessionId);
        TimeSpan timeout = options?.Timeout is { } t && t > TimeSpan.Zero && t <= MaxTimeout ? t : TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            RemoteCommandResult result = await client.RunCommandDetailedAsync(command, cts.Token).ConfigureAwait(false);
            return new(result.StandardOutput)
            {
                Error = result.StandardError,
                ExitCode = result.ExitCode
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Remote command timed out after {timeout.TotalSeconds:0}s: {command}");
        }
    }

    public async Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
        IProgress<ExecOutput> output, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(output);
        ISshClientWrapper client = Resolve(sessionId);
        // 先占坑再干活:流是不限时的,每条占一个 SSH 通道。忘了取消的插件不该有能力
        // 把对端的 MaxSessions 耗光 —— 那时坏掉的是用户的连接,不只是这个插件。
        if (Interlocked.Increment(ref _activeStreams) > IRemoteExecApi.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _activeStreams);
            throw new InvalidOperationException(
                $"This plugin already has {IRemoteExecApi.MaxConcurrentStreams} streaming commands in flight; cancel one before starting another.");
        }
        bool includeStandardError = options?.IncludeStandardError ?? true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options?.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }
        try
        {
            RemoteCommandStreamResult result = await client.StreamCommandAsync(
                command,
                includeStandardError,
                (isError, line) => output.Report(new(isError ? ExecStream.StandardError : ExecStream.StandardOutput, line)),
                cts.Token).ConfigureAwait(false);
            return new(result.ExitCode, result.Lines);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Streaming remote command timed out: {command}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeStreams);
        }
    }

    private ISshClientWrapper Resolve(string sessionId) =>
        Guid.TryParse(sessionId, out Guid id) && connections.GetClient(id) is { IsConnected: true } client
            ? client
            : throw new PluginSessionNotFoundException(sessionId);
}
