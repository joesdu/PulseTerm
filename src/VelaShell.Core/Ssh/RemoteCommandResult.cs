namespace VelaShell.Core.Ssh;

/// <summary>
/// 一次性远端命令的完整结果。
/// <para>
/// 库中立类型:<see cref="ISshClientWrapper" /> 的契约不暴露具体 SSH 库的进程类型,
/// 换底层库时只要能填出这三样就行。
/// </para>
/// </summary>
/// <param name="StandardOutput">标准输出(UTF-8 解码)。</param>
/// <param name="StandardError">标准错误(UTF-8 解码)。</param>
/// <param name="ExitCode">退出码。</param>
public sealed record RemoteCommandResult(string StandardOutput, string StandardError, int ExitCode)
{
    /// <summary>退出码为 0。</summary>
    public bool IsSuccess => ExitCode == 0;
}

/// <summary>流式远端命令的收尾结果。</summary>
/// <param name="ExitCode">退出码。</param>
/// <param name="Lines">总共回调了多少行。</param>
public sealed record RemoteCommandStreamResult(int ExitCode, long Lines);
