using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// Agent 模式的工具箱:把插件能力(会话枚举/终端读取/远程执行/远程读文件/终端回写)
/// 包装为 <see cref="AIFunction" />,由 FunctionInvokingChatClient 自动循环调用。
/// 危险操作(run_command / write_terminal)先过 <see cref="ApprovalHandler" /> 审批;
/// write_terminal 之上还有宿主自己的授权弹窗。
/// 工具返回值一律是给模型看的文本;失败以文本描述而非异常(异常会中断整轮对话)。
/// </summary>
public sealed class AgentToolbox(IPluginContext context)
{
    private const int MaxFileBytes = 256 * 1024;

    /// <summary>当前选中的目标会话 id(由聊天面板提供;null = 未选)。</summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>危险操作审批(参数为将执行的命令/输入文本,返回是否放行)。未设置视为拒绝。</summary>
    public Func<string, Task<bool>>? ApprovalHandler { get; set; }

    /// <summary>免审批开关(用户显式打开才生效)。</summary>
    public bool AutoApprove { get; set; }

    /// <summary>构建暴露给模型的工具列表。</summary>
    public IList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(ListSessionsAsync, "list_sessions",
            "List the user's SSH sessions (id, host, port, username, state). Use it to discover what servers are available."),
        AIFunctionFactory.Create(ReadTerminalAsync, "read_terminal",
            "Read the tail of the terminal output (scrollback + screen) of the selected SSH session. Use it to see what the user sees."),
        AIFunctionFactory.Create(RunCommandAsync, "run_command",
            "Run a one-shot, non-interactive shell command on the selected SSH session over a separate exec channel (it does NOT type into the user's terminal). Requires user approval. Prefer read-only commands."),
        AIFunctionFactory.Create(ReadRemoteFileAsync, "read_remote_file",
            "Read a small text file from the selected SSH session via SFTP (up to 256 KB). Returns the file content as text."),
        AIFunctionFactory.Create(WriteTerminalAsync, "write_terminal",
            "Type text into the user's visible terminal of the selected SSH session, as if the user typed it. A trailing newline executes the command. The host will additionally ask the user for permission. Use only when the user explicitly wants something typed into their terminal.")
    ];

    private async Task<string> ListSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return "No sessions. Ask the user to connect to a server in VelaShell first.";
        }
        var sb = new StringBuilder();
        foreach (SessionInfo s in sessions)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                id = s.SessionId,
                host = s.Host,
                port = s.Port,
                username = s.Username,
                state = s.State.ToString()
            }));
        }
        return sb.ToString();
    }

    private async Task<string> ReadTerminalAsync(
        [Description("Maximum number of lines to read from the end of the buffer (default 200, max 2000).")] int lines = 200,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        lines = Math.Clamp(lines, 1, 2000);
        string output = await context.Terminal.GetOutputAsync(sessionId, lines, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output) ? "(terminal buffer is empty)" : output;
    }

    private async Task<string> RunCommandAsync(
        [Description("The shell command to execute (non-interactive, one-shot).")] string command,
        [Description("Timeout in seconds (default 30, max 600).")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        if (!await ApproveAsync($"run_command: {command}", cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED execution of this command. Do not retry it; ask the user how to proceed.";
        }
        try
        {
            ExecResult result = await context.RemoteExec.RunAsync(sessionId, command,
                new ExecOptions { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)) },
                cancellationToken).ConfigureAwait(false);
            string output = result.Output;
            if (output.Length > 32 * 1024)
            {
                output = output[..(32 * 1024)] + "\n…(output truncated)";
            }
            return output.Length == 0 ? "(command produced no output)" : output;
        }
        catch (TimeoutException)
        {
            return $"Command timed out after {timeoutSeconds}s.";
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }

    private async Task<string> ReadRemoteFileAsync(
        [Description("Absolute path of the remote file to read.")] string path,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        try
        {
            byte[] bytes = await context.RemoteFs.ReadAllBytesAsync(sessionId, path, MaxFileBytes, cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0 ? "(file is empty)" : Encoding.UTF8.GetString(bytes);
        }
        catch (InvalidOperationException)
        {
            return $"File is larger than {MaxFileBytes / 1024} KB; read a smaller file or use run_command with head/tail/grep instead.";
        }
        catch (Exception ex)
        {
            return $"Failed to read file: {ex.Message}";
        }
    }

    private async Task<string> WriteTerminalAsync(
        [Description("Text to type into the terminal. Include a trailing \\n to execute it as a command.")] string text,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        if (!await ApproveAsync($"write_terminal: {text}", cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED typing into the terminal. Do not retry; ask the user how to proceed.";
        }
        try
        {
            await context.Terminal.WriteAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
            return "Text was typed into the terminal.";
        }
        catch (PluginPermissionDeniedException)
        {
            return "The host denied terminal write permission. Do not retry.";
        }
        catch (Exception ex)
        {
            return $"Failed to write terminal: {ex.Message}";
        }
    }

    private const string NoSessionMessage =
        "No SSH session is selected/connected. Ask the user to connect and select a session in the AI panel first.";

    private string? ResolveSessionId() => SessionIdProvider?.Invoke();

    private async Task<bool> ApproveAsync(string summary, CancellationToken cancellationToken)
    {
        if (AutoApprove)
        {
            return true;
        }
        if (ApprovalHandler is not { } handler)
        {
            return false;
        }
        // 用户点了停止时,不再傻等审批
        return await handler(summary).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
