namespace VelaShell.PluginSdk.RemoteExec;

/// <summary>远程命令执行选项。</summary>
public sealed record ExecOptions
{
    /// <summary>执行超时,默认 30 秒,上限 10 分钟(宿主强制截断)。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>远程命令执行结果。</summary>
/// <param name="Output">命令的标准输出(UTF-8 解码)。</param>
public sealed record ExecResult(string Output);

/// <summary>
/// 远程执行能力:在既有会话上经独立 exec 通道执行一次性命令 ——
/// 不进用户终端、不污染用户 shell 历史与环境变量。
/// 适合探测类命令(<c>docker ps</c>、<c>systemctl status</c> 等);
/// 交互式或长驻命令不适用。会话无效时抛 <see cref="PluginSessionNotFoundException" />。
/// </summary>
public interface IRemoteExecApi
{
    /// <summary>执行命令并等待完成,返回标准输出。超时抛 <see cref="TimeoutException" />。</summary>
    Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null, CancellationToken cancellationToken = default);
}
