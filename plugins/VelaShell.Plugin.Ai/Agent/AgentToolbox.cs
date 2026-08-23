using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent.Web;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// Agent 模式的工具箱:把插件能力(会话枚举/终端读取/远程执行/远程读文件/终端回写/网络检索)
/// 包装为 <see cref="AIFunction" />,由 FunctionInvokingChatClient 自动循环调用。
/// 危险操作(run_command / write_terminal)先过 <see cref="ApprovalHandler" /> 审批;
/// write_terminal 之上还有宿主自己的授权弹窗。
/// 工具返回值一律是给模型看的文本;失败以文本描述而非异常(异常会中断整轮对话)。
/// </summary>
public sealed class AgentToolbox(IPluginContext context)
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxDirectoryEntries = 200;

    /// <summary>当前选中的目标会话 id(由聊天面板提供;null = 未选)。</summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>危险操作审批(返回是否放行)。未设置视为拒绝。</summary>
    public Func<ApprovalRequest, Task<bool>>? ApprovalHandler { get; set; }

    /// <summary>审批方式(见 <see cref="ApprovalMode" />)。</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Ask;

    /// <summary>用户在"配置工具"里取消勾选的内置工具名(不暴露给模型)。</summary>
    public IReadOnlySet<string> DisabledTools { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>网络检索设置(SearXNG 地址、私网闸、截断上限)。由聊天面板每轮推过来。</summary>
    public WebSearchOptions WebSearch { get; set; } = new();

    /// <summary>全部内置工具的名称与一句说明,供"配置工具"窗口列出勾选项。</summary>
    public static IReadOnlyList<(string Name, string Description, bool ReadOnly)> Catalog { get; } =
    [
        ("list_sessions", "列出用户的 SSH 会话(主机、端口、用户名、状态)", true),
        ("read_terminal", "读取所选会话终端的尾部输出", true),
        ("read_remote_file", "经 SFTP 读取远端小文本文件(≤256KB)", true),
        ("list_remote_directory", "经 SFTP 列出远端目录", true),
        ("run_command", "在所选会话上执行一次性命令(独立通道,不进用户终端)", false),
        ("write_remote_file", "经 SFTP 覆盖写入远端文本文件", false),
        ("upload_local_file", "把本机文件(如 MCP 工具刚生成的)经 SFTP 传到服务器", false),
        ("write_terminal", "把文本敲进用户可见的终端", false),
        ("web_search", "检索网络,返回标题/链接/摘要清单(不含正文)", true),
        ("web_fetch", "取一个网页并转成文本", true)
    ];

    /// <summary>
    /// 构建暴露给模型的工具列表。<see cref="ChatMode.Plan" /> 下<b>只给只读工具</b> ——
    /// 计划模式的约定就是"先说怎么做",不该在这一步动任何东西。
    /// </summary>
    /// <param name="mode">当前对话模式。</param>
    /// <param name="nativeWebSearch">
    /// 这一轮的检索由供应商的服务端工具接管。此时<b>不再注册插件自带的 web_search</b> ——
    /// 两个名字不同、用途一样的检索工具摆在一起,模型会来回换着试,既慢又乱。
    /// web_fetch 照给:原生检索给的是它自己挑好的结果,用户点名要读某个 URL 时还得靠它。
    /// </param>
    public IList<AITool> CreateTools(ChatMode mode, bool nativeWebSearch = false)
    {
        var all = new List<(string Name, AITool Tool, bool ReadOnly)>
        {
            ("list_sessions", AIFunctionFactory.Create(ListSessionsAsync, "list_sessions",
                "List the user's SSH sessions (id, host, port, username, state). Use it to discover what servers are available."), true),
            ("read_terminal", AIFunctionFactory.Create(ReadTerminalAsync, "read_terminal",
                "Read the tail of the terminal output (scrollback + screen) of the selected SSH session. Use it to see what the user sees."), true),
            ("run_command", AIFunctionFactory.Create(RunCommandAsync, "run_command",
                "Run a one-shot, non-interactive shell command on the selected SSH session over a separate exec channel (it does NOT type into the user's terminal). Requires user approval. Prefer read-only commands."), false),
            ("read_remote_file", AIFunctionFactory.Create(ReadRemoteFileAsync, "read_remote_file",
                "Read a small text file from the selected SSH session via SFTP (up to 256 KB). Returns the file content as text."), true),
            ("list_remote_directory", AIFunctionFactory.Create(ListRemoteDirectoryAsync, "list_remote_directory",
                "List a directory on the selected SSH session via SFTP (name, type, size, modified time). Use it to find files before reading or editing them."), true),
            ("write_remote_file", AIFunctionFactory.Create(WriteRemoteFileAsync, "write_remote_file",
                "Overwrite (or create) a text file on the selected SSH session via SFTP. Requires user approval. Always read the file first and send back its full new content — this replaces the file, it does not patch it."), false),
            ("upload_local_file", AIFunctionFactory.Create(UploadLocalFileAsync, "upload_local_file",
                "Upload a file from the USER'S OWN MACHINE to the SSH server via SFTP. Requires user approval. "
                + "Use this after a local MCP tool produced a file (MCP servers run locally, so their output is NOT on the SSH server): "
                + "upload it to make it available there."), false),
            ("write_terminal", AIFunctionFactory.Create(WriteTerminalAsync, "write_terminal",
                "Type text into the user's visible terminal of the selected SSH session, as if the user typed it. A trailing newline executes the command. The host will additionally ask the user for permission. Use only when the user explicitly wants something typed into their terminal."), false)
        };
        // 两个网络工具受"网络检索"总闸控制;开着才注册。都是只读的,所以计划模式也给 ——
        // "先查清楚再说怎么做"本来就是计划该干的事。
        if (WebSearch.Enabled && !nativeWebSearch)
        {
            all.Add(("web_search", AIFunctionFactory.Create(WebSearchAsync, "web_search",
                "Search the web. Returns a numbered list of results (title, URL, snippet) — it does NOT return page contents. "
                + "Pick the results that look relevant and read them with web_fetch. "
                + "Use it whenever the answer may have moved since training: package versions, CVE details, changelogs, "
                + "vendor documentation, or an error message you do not recognise."), true));
        }
        if (WebSearch.Enabled)
        {
            all.Add(("web_fetch", AIFunctionFactory.Create(WebFetchAsync, "web_fetch",
                "Fetch one web page (or a raw text/JSON URL) and return it as text. "
                + "Same-host redirects are followed; a cross-host redirect is reported back instead of followed, "
                + "so call web_fetch again with the new URL if that is where you want to go."), true));
        }
        return
        [
            .. all.Where(t => (mode != ChatMode.Plan || t.ReadOnly) && !DisabledTools.Contains(t.Name))
                  .Select(t => t.Tool)
        ];
    }

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
        // 记忆键取命令名(第一个词):同一次排查里 ls/cat/systemctl 会被反复调用,
        // 一条条点太折磨人;但键只到命令名为止,换个命令仍要重新点头。
        if (!await ApproveAsync(new ApprovalRequest("run_command", command, $"run_command:{FirstWord(command)}"),
                cancellationToken).ConfigureAwait(false))
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

    private async Task<string> ListRemoteDirectoryAsync(
        [Description("Absolute path of the remote directory to list.")] string path,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        try
        {
            IReadOnlyList<RemoteFileEntry> entries = await context.RemoteFs
                .ListDirectoryAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return "(directory is empty)";
            }
            var sb = new StringBuilder();
            foreach (RemoteFileEntry entry in entries.Take(MaxDirectoryEntries))
            {
                sb.AppendLine(JsonSerializer.Serialize(new
                {
                    name = entry.Name,
                    path = entry.FullPath,
                    type = entry.IsDirectory ? "dir" : "file",
                    size = entry.Size,
                    modified = entry.LastModified.ToString("u"),
                    permissions = entry.Permissions
                }));
            }
            if (entries.Count > MaxDirectoryEntries)
            {
                sb.AppendLine($"…({entries.Count - MaxDirectoryEntries} more entries omitted; narrow the path or use run_command with ls/find)");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to list directory: {ex.Message}";
        }
    }

    private async Task<string> WriteRemoteFileAsync(
        [Description("Absolute path of the remote file to overwrite (parent directory must exist).")] string path,
        [Description("The complete new content of the file (UTF-8). This replaces the whole file.")] string content,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        content ??= "";
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
        {
            return $"Refusing to write more than {MaxFileBytes / 1024} KB in one call; split the change or use run_command.";
        }
        // 审批摘要带上前几行内容:用户要看清到底写了什么,而不只是路径。
        string preview = content.Length <= 400 ? content : content[..400] + "…";
        // 不给记忆键:每次写的路径与内容都不同,"总是允许"在这里等于放弃把关
        if (!await ApproveAsync(new ApprovalRequest("write_remote_file", $"{path}\n{preview}"), cancellationToken)
                .ConfigureAwait(false))
        {
            return "The user DENIED writing this file. Do not retry; ask the user how to proceed.";
        }
        try
        {
            await context.RemoteFs.WriteAllBytesAsync(sessionId, path, Encoding.UTF8.GetBytes(content), cancellationToken)
                         .ConfigureAwait(false);
            return $"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {path}.";
        }
        catch (Exception ex)
        {
            return $"Failed to write file: {ex.Message}";
        }
    }

    /// <summary>本机文件上传的大小上限:比文本写入宽松得多,但也别让一次调用把内存吃光。</summary>
    private const long MaxUploadBytes = 32 * 1024 * 1024;

    /// <summary>
    /// 把<b>本机</b>的一个文件传到服务器。
    /// </summary>
    /// <remarks>
    /// 这条是为 MCP 工具准备的。MCP 服务器跑在用户自己的机器上,它产出的文件落在本机
    /// (通常是用户给那台服务器配的工作目录),既不在 SSH 服务器上,也不在终端的当前目录里 ——
    /// 用户找不到、模型还常常当成远端路径去汇报。有了这条,模型可以把刚生成的文件显式送上去。
    /// </remarks>
    private async Task<string> UploadLocalFileAsync(
        [Description("Absolute path of a file on the user's own machine (for example one an MCP tool just produced in its working directory).")] string localPath,
        [Description("Absolute destination path on the SSH server (parent directory must exist).")] string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (ResolveSessionId() is not { } sessionId)
        {
            return NoSessionMessage;
        }
        localPath = (localPath ?? "").Trim();
        if (!Path.IsPathRooted(localPath))
        {
            return "local_path must be an absolute path on the user's machine.";
        }
        if (!File.Exists(localPath))
        {
            return $"No such local file: {localPath}. List the MCP working directory first, or ask the tool where it wrote the file.";
        }
        var info = new FileInfo(localPath);
        if (info.Length > MaxUploadBytes)
        {
            return $"Refusing to upload more than {MaxUploadBytes / (1024 * 1024)} MB in one call ({localPath} is {info.Length / (1024 * 1024)} MB).";
        }
        // 不给记忆键:每次上传的来源与去向都不同,"总是允许"在这里等于放弃把关
        if (!await ApproveAsync(new ApprovalRequest("upload_local_file", $"{localPath}\n→ {remotePath}  ({info.Length} bytes)"),
                cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED this upload. Do not retry; ask the user how to proceed.";
        }
        try
        {
            // 走 SFTP 的流式上传,别先整份读进内存 —— xmind/压缩包这类产物可以很大
            await context.RemoteFs.UploadFileAsync(sessionId, localPath, remotePath, cancellationToken: cancellationToken)
                         .ConfigureAwait(false);
            return $"Uploaded {info.Length} bytes: {localPath} → {remotePath}.";
        }
        catch (Exception ex)
        {
            return $"Failed to upload: {ex.Message}";
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
        // 同样不给记忆键:往用户眼前的终端里敲字,每次都该问
        if (!await ApproveAsync(new ApprovalRequest("write_terminal", text), cancellationToken).ConfigureAwait(false))
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

    // ---- 网络检索 ----

    /// <summary>按当前设置现造一套检索件。</summary>
    /// <remarks>
    /// 每次调用都新建:<see cref="WebAccess" /> 本身没有状态(HttpClient 与页面缓存都是静态的),
    /// 而设置随时可能在设置窗口里被改掉 —— 缓存一个实例只会让改完的设置这一轮不生效。
    /// </remarks>
    private (WebAccess Access, WebSearchOptions Options) Web()
    {
        WebSearchOptions options = WebSearch;
        options.Clamp();
        return (new WebAccess(options), options);
    }

    private async Task<string> WebSearchAsync(
        [Description("The search query. Plain keywords work best — write it the way you would type it into a search engine.")] string query,
        [Description("How many results to return. 0 (the default) uses the user's configured count; maximum 20.")] int count = 0,
        CancellationToken cancellationToken = default)
    {
        (WebAccess access, WebSearchOptions options) = Web();
        var engine = new WebSearchEngine(access, options);
        (bool ok, IReadOnlyList<SearchHit> hits, string note) = await engine
            .SearchAsync(query ?? "", count > 0 ? count : options.MaxResults, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return $"Search failed: {note}";
        }
        if (hits.Count == 0)
        {
            return note.Length > 0
                ? $"No results for '{query}'. {note}"
                : $"No results for '{query}'. Try different keywords.";
        }
        var sb = new StringBuilder();
        sb.Append("Results for '").Append(query).Append("':").Append('\n');
        int n = 0;
        foreach (SearchHit hit in hits)
        {
            sb.Append('\n').Append(++n).Append(". ").Append(hit.Title).Append('\n');
            sb.Append("   ").Append(hit.Url).Append('\n');
            if (hit.Snippet.Length > 0)
            {
                sb.Append("   ").Append(Clip(hit.Snippet, 400)).Append('\n');
            }
        }
        sb.Append('\n').Append("Snippets are not the page. Read the promising ones with web_fetch(url) before answering.");
        return sb.ToString();
    }

    private async Task<string> WebFetchAsync(
        [Description("Absolute http(s) URL of the page to read.")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out Uri? target))
        {
            return $"'{url}' is not an absolute URL — pass a full http(s) address (web_search results already are).";
        }
        (WebAccess access, _) = Web();
        FetchResult result = await access.FetchAsync(target, cancellationToken).ConfigureAwait(false);
        // 失败时 Body 里装的就是给模型看的说明(跳转去了哪、为什么被拦),照原样回
        if (!result.Ok || result.FinalUrl == target)
        {
            return result.Body;
        }
        return $"[followed a same-host redirect to {result.FinalUrl}]\n\n{result.Body}";
    }

    /// <summary>长文本截断,尾部补省略号。</summary>
    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private const string NoSessionMessage =
        "No SSH session is selected/connected. Ask the user to connect and select a session in the AI panel first.";

    private string? ResolveSessionId() => SessionIdProvider?.Invoke();

    private async Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (Approval == ApprovalMode.Bypass)
        {
            return true;
        }
        // 只读放行:仅对"确定无副作用"的命令生效,写文件/敲终端一律照问(见 ReadOnlyCommand)
        if (Approval == ApprovalMode.ReadOnlyAuto
            && request.Kind == "run_command"
            && ReadOnlyCommand.IsSafe(request.Detail))
        {
            return true;
        }
        if (ApprovalHandler is not { } handler)
        {
            return false;
        }
        // 用户点了停止时,不再傻等审批
        return await handler(request).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>命令的第一个词(去掉前导的 sudo,否则所有命令的记忆键都是 sudo)。</summary>
    private static string FirstWord(string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "";
        }
        return parts[0] == "sudo" && parts.Length > 1 ? $"sudo {parts[1]}" : parts[0];
    }
}
