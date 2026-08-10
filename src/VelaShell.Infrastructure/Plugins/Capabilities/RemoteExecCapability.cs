using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="IRemoteExecApi" /> 的桥接实现:复用宿主既有连接,经独立 exec 通道执行
/// (<see cref="ISshClientWrapper.RunCommandAsync" />),不进用户终端、不碰输入串行化队列。
/// </summary>
internal sealed class RemoteExecCapability(ISshConnectionService connections) : IRemoteExecApi
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    public async Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!Guid.TryParse(sessionId, out Guid id) || connections.GetClient(id) is not { IsConnected: true } client)
        {
            throw new PluginSessionNotFoundException(sessionId);
        }
        TimeSpan timeout = options?.Timeout is { } t && t > TimeSpan.Zero && t <= MaxTimeout ? t : TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            string output = await client.RunCommandAsync(command, cts.Token).ConfigureAwait(false);
            return new(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Remote command timed out after {timeout.TotalSeconds:0}s: {command}");
        }
    }
}
