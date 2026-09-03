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
using VelaShell.PluginSdk.Terminal;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>
/// Agent 模式的工具箱:把插件能力包装成 <see cref="AIFunction" />,
/// 由 FunctionInvokingChatClient 自动循环调用。
/// </summary>
/// <remarks>
/// <para>
/// <b>每个工具都接受可选的 <c>session_id</c></b>,不传就作用于用户当前选中的会话。
/// 这一条是"多主机"的前提:<c>list_sessions</c> 把 id 交给模型,却没有工具肯收,
/// 那模型就只能对着用户此刻选中的那一台干活 —— 而同时管着好几台服务器,
/// 恰恰是 SSH 客户端区别于普通聊天框的地方。
/// </para>
/// <para>
/// <b>只读工具不走审批,写操作一律走。</b>把高频只读动作做成<b>专用工具</b>(而不是让模型
/// 拼一条自由文本命令交给 <see cref="ReadOnlyCommand" /> 去猜)有两个好处:结构上不可能有副作用,
/// 所以不必打断用户;返回的东西也更小更准,不用把整份文件回传给模型。
/// </para>
/// <para>
/// 网络检索(web_search / web_fetch)由 <c>VelaShell.Plugin.Ai.Agent.Web</c> 下的实现接管。
/// </para>
/// <para>
/// 工具返回值一律是给模型看的文本;失败以文本描述而非异常 —— 异常会中断整轮对话,
/// 而"这条路走不通"本身就是模型需要知道、并据此换个方法的信息。
/// </para>
/// </remarks>
public sealed class AgentToolbox(IPluginContext context)
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxDirectoryEntries = 200;
    private const int MaxCommandOutput = 32 * 1024;

    /// <summary>本机文件上传的大小上限:比文本写入宽松得多,但也别让一次调用把内存吃光。</summary>
    private const long MaxUploadBytes = 32 * 1024 * 1024;

    /// <summary>一次并行执行最多铺多少台。再多就该写脚本了,而且输出也读不过来。</summary>
    private const int MaxParallelSessions = 16;

    /// <summary>当前选中的目标会话 id(由聊天面板提供;null = 未选)。</summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>最近一次 <c>open_session</c> 开出来的会话;界面上没有选中项时的兜底目标。</summary>
    /// <remarks>
    /// <b>只在 <see cref="SessionIdProvider" /> 给不出东西时才用。</b>这正是"没绑机器"的那条路:
    /// IM 桥接里聊天没绑会话、MCP 里外部 agent 还没 <c>use_session</c> —— 它们的 provider 返回 null,
    /// 自己开出来的那条于是自动成为默认目标,不必逼模型在后续每一次调用里都记得带 session_id。
    /// <para>
    /// 反过来,用户在面板上明明选着 A,模型开了 B,后续不带 session_id 的调用仍然落在 A 上:
    /// 用户选的那一台是他此刻正看着的,不该被一次工具调用悄悄改掉。
    /// <c>open_session</c> 的回执里因此把新会话 id 说得很清楚。
    /// </para>
    /// </remarks>
    private string? _openedSessionId;

    /// <summary>最近一次 <see cref="CreateTools" /> 的模式(决定"没会话"提示里提不提 open_session)。</summary>
    private ChatMode _mode = ChatMode.Agent;

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
        ("list_saved_sessions", "列出会话树里已保存的连接配置(含此刻没连着的)", true),
        ("read_terminal", "读取会话终端的尾部输出", true),
        ("search_terminal", "在终端滚回里搜索(子串或正则),只带回命中行", true),
        ("read_remote_file", "经 SFTP 读取远端小文本文件(≤256KB)", true),
        ("list_remote_directory", "经 SFTP 列出远端目录", true),
        ("stat_remote_path", "查远端路径存不存在、多大、什么时候改的", true),
        ("get_working_directory", "取会话的当前工作目录(相对路径的基准)", true),
        ("system_overview", "一次取回系统概览(内核、发行版、负载、内存、磁盘)", true),
        ("web_search", "检索网络,返回标题/链接/摘要清单(不含正文)", true),
        ("web_fetch", "取一个网页并转成文本", true),
        ("open_session", "按已保存的配置连一台机器(宿主会再向用户确认一次)", false),
        ("close_session", "关掉本插件自己开的那条会话", false),
        ("run_command", "在会话上执行一次性命令(独立通道,不进用户终端)", false),
        ("run_on_sessions", "在多台会话上并行执行同一条命令", false),
        ("write_remote_file", "经 SFTP 覆盖写入远端文本文件", false),
        ("patch_remote_file", "只替换远端文件里的一段文本(不必回传整份)", false),
        ("make_remote_directory", "创建远端目录(幂等)", false),
        ("rename_remote_path", "重命名 / 移动远端文件或目录(改配置前备份用)", false),
        ("upload_local_file", "把本机文件(如 MCP 工具刚生成的)经 SFTP 传到服务器", false),
        ("download_remote_file", "把远端文件拉到本机(交给本地 MCP 工具处理)", false),
        ("write_terminal", "把文本敲进用户可见的终端", false)
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
        // 记下这一轮的模式:"没有会话可用"那句提示要据此决定该不该提 open_session ——
        // 计划模式里它压根没注册,提了只会让模型去调一个不存在的工具。
        _mode = mode;
        var all = new List<(string Name, AITool Tool, bool ReadOnly)>
        {
            ("list_sessions", AIFunctionFactory.Create(ListSessionsAsync, "list_sessions",
                "List the user's SSH sessions (id, host, port, username, state). "
                + "Every other tool accepts one of these ids, so use this first when the task spans more than one server."), true),

            ("list_saved_sessions", AIFunctionFactory.Create(ListSavedSessionsAsync, "list_saved_sessions",
                "List the SAVED connection configurations in the user's VelaShell session tree, including machines that are "
                + "NOT connected right now. Use it when the machine the user is asking about does not appear in list_sessions. "
                + "The ids it returns are saved_session_id values for open_session — they are NOT session ids and the other tools do not accept them."), true),

            ("read_terminal", AIFunctionFactory.Create(ReadTerminalAsync, "read_terminal",
                "Read the tail of the terminal output (scrollback + screen) of an SSH session. Use it to see what the user sees. "
                + "If you are looking for something specific in a long buffer, prefer search_terminal — it costs far fewer tokens."), true),

            ("search_terminal", AIFunctionFactory.Create(SearchTerminalAsync, "search_terminal",
                "Search the terminal scrollback of an SSH session and return only the matching lines with their line numbers. "
                + "Much cheaper than reading the whole buffer when hunting for an error message."), true),

            ("read_remote_file", AIFunctionFactory.Create(ReadRemoteFileAsync, "read_remote_file",
                "Read a small text file from an SSH session via SFTP (up to 256 KB). Returns the file content as text."), true),

            ("list_remote_directory", AIFunctionFactory.Create(ListRemoteDirectoryAsync, "list_remote_directory",
                "List a directory on an SSH session via SFTP (name, type, size, modified time). Use it to find files before reading or editing them."), true),

            ("stat_remote_path", AIFunctionFactory.Create(StatRemotePathAsync, "stat_remote_path",
                "Check whether a remote path exists and report its type, size, permissions and modified time. "
                + "Use this instead of listing a whole directory when you only care about one path."), true),

            ("get_working_directory", AIFunctionFactory.Create(GetWorkingDirectoryAsync, "get_working_directory",
                "Get the current working directory of an SSH session. Resolve relative paths against it instead of guessing."), true),

            ("system_overview", AIFunctionFactory.Create(SystemOverviewAsync, "system_overview",
                "Collect a read-only snapshot of the server in one call: kernel, distribution, uptime and load, CPU count, "
                + "memory and disk usage. Start troubleshooting here instead of issuing five separate commands."), true),

            ("open_session", AIFunctionFactory.Create(OpenSessionAsync, "open_session",
                "Connect to one of the SAVED configurations from list_saved_sessions and return the new session id. "
                + "Requires user approval, and the VelaShell host then asks the user to confirm as well, showing your `reason` verbatim — "
                + "so write a reason that says who wants what, not \"the plugin needs a connection\". "
                + "Use it when the machine in question is saved but not connected. You cannot connect to an arbitrary host:port — "
                + "only to configurations the user has already saved."), false),

            ("close_session", AIFunctionFactory.Create(CloseSessionAsync, "close_session",
                "Close a session that YOU opened with open_session. The host refuses to close sessions the user opened themselves. "
                + "Do this once you are done with a session you opened, so the user is not left with tabs they did not ask for."), false),

            ("run_command", AIFunctionFactory.Create(RunCommandAsync, "run_command",
                "Run a one-shot, non-interactive shell command on an SSH session over a separate exec channel (it does NOT type into the user's terminal). "
                + "Requires user approval. Prefer the dedicated read-only tools above where one fits — they need no approval."), false),

            ("run_on_sessions", AIFunctionFactory.Create(RunOnSessionsAsync, "run_on_sessions",
                "Run the SAME command on several SSH sessions in parallel and return the output per host. Requires user approval. "
                + "Use it for fleet-wide checks (\"how full is the disk on all of these?\") instead of repeating run_command."), false),

            ("write_remote_file", AIFunctionFactory.Create(WriteRemoteFileAsync, "write_remote_file",
                "Overwrite (or create) a text file on an SSH session via SFTP. Requires user approval. "
                + "This replaces the WHOLE file — to change a few lines in an existing file use patch_remote_file instead."), false),

            ("patch_remote_file", AIFunctionFactory.Create(PatchRemoteFileAsync, "patch_remote_file",
                "Replace one exact snippet inside a remote text file, leaving the rest untouched. Requires user approval. "
                + "old_text must appear EXACTLY ONCE in the file — include enough surrounding lines to make it unique. "
                + "Prefer this over write_remote_file for config files: you do not have to send the whole file back."), false),

            ("make_remote_directory", AIFunctionFactory.Create(MakeRemoteDirectoryAsync, "make_remote_directory",
                "Create a directory (and missing parents) on an SSH session. Idempotent. Requires user approval."), false),

            ("rename_remote_path", AIFunctionFactory.Create(RenameRemotePathAsync, "rename_remote_path",
                "Rename or move a remote file or directory. Requires user approval. "
                + "Use it to back a config file up (foo.conf → foo.conf.bak) before editing it."), false),

            ("upload_local_file", AIFunctionFactory.Create(UploadLocalFileAsync, "upload_local_file",
                "Upload a file from the USER'S OWN MACHINE to the SSH server via SFTP. Requires user approval. "
                + "Use this after a local MCP tool produced a file (MCP servers run locally, so their output is NOT on the SSH server)."), false),

            ("download_remote_file", AIFunctionFactory.Create(DownloadRemoteFileAsync, "download_remote_file",
                "Download a file from the SSH server to the USER'S OWN MACHINE via SFTP. Requires user approval. "
                + "Use it when a local MCP tool needs to work on a remote file. Returns the local path it was saved to."), false),

            ("write_terminal", AIFunctionFactory.Create(WriteTerminalAsync, "write_terminal",
                "Type text into the user's visible terminal of an SSH session, as if the user typed it. A trailing newline executes the command. "
                + "The host will additionally ask the user for permission. Use only when the user explicitly wants something typed into their terminal."), false)
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

    // ---- 只读 ----

    private async Task<string> ListSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return "No sessions. " + NoSession;
        }
        // 与 ResolveAsync 同一套优先级:界面选中的 > 自己开出来的。标错默认目标比不标更糟。
        string? selected = SessionIdProvider?.Invoke() ?? _openedSessionId;
        var sb = new StringBuilder();
        foreach (SessionInfo s in sessions)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                id = s.SessionId,
                host = s.Host,
                port = s.Port,
                username = s.Username,
                state = s.State.ToString(),
                // 标出默认的那一台:不传 session_id 时所有工具落在它身上
                selected = s.SessionId == selected
            }));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 已保存的连接配置(会话树里的那些,含此刻没连着的)。
    /// </summary>
    /// <remarks>
    /// 顺带标出"这条配置已经有连着的会话"并给出那条会话的 id:不标的话,模型对着一台其实
    /// 已经开着的机器还要再走一遍 <c>open_session</c>(那可是一次要惊动用户的确认框)。
    /// 配对只按主机 + 端口 + 用户名,是个提示而不是判决 —— 真正认不认这条复用由宿主定。
    /// </remarks>
    private async Task<string> ListSavedSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SavedSessionInfo> saved = await context.Sessions.ListSavedAsync(cancellationToken).ConfigureAwait(false);
        if (saved.Count == 0)
        {
            return "The user has no saved connection configurations in VelaShell.";
        }
        IReadOnlyList<SessionInfo> live = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (SavedSessionInfo s in saved)
        {
            SessionInfo? connected = live.FirstOrDefault(c =>
                c.State == SessionState.Connected
                && string.Equals(c.Host, s.Host, StringComparison.OrdinalIgnoreCase)
                && c.Port == s.Port
                && (s.Username.Length == 0 || string.Equals(c.Username, s.Username, StringComparison.Ordinal)));
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                saved_session_id = s.SavedSessionId,
                name = s.Name,
                host = s.Host,
                port = s.Port,
                username = s.Username,
                group = s.Group,
                // 已经连着的话直接给会话 id:不必再 open_session 惊动用户
                connected_session_id = connected?.SessionId
            }));
        }
        return sb.ToString();
    }

    private async Task<string> ReadTerminalAsync(
        [Description("Maximum number of lines to read from the end of the buffer (default 200, max 2000).")] int lines = 200,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        lines = Math.Clamp(lines, 1, 2000);
        string output = await context.Terminal.GetOutputAsync(id, lines, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output) ? "(terminal buffer is empty)" : output;
    }

    private async Task<string> SearchTerminalAsync(
        [Description("Text to look for. Case-insensitive substring by default.")] string pattern,
        [Description("Treat the pattern as a .NET regular expression instead of a plain substring.")] bool isRegex = false,
        [Description("Maximum number of matching lines to return (default 100, max 500).")] int maxMatches = 100,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        if (string.IsNullOrEmpty(pattern))
        {
            return "pattern must not be empty.";
        }
        try
        {
            IReadOnlyList<TerminalMatch> matches = await context.Terminal
                .SearchOutputAsync(id, pattern, isRegex, Math.Clamp(maxMatches, 1, 500), cancellationToken)
                .ConfigureAwait(false);
            if (matches.Count == 0)
            {
                return $"No line in the terminal buffer matches '{pattern}'.";
            }
            var sb = new StringBuilder();
            foreach (TerminalMatch match in matches)
            {
                sb.AppendLine($"{match.Line}: {match.Text}");
            }
            return sb.ToString();
        }
        catch (ArgumentException ex)
        {
            return $"Invalid regular expression: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Failed to search the terminal: {ex.Message}";
        }
    }

    private async Task<string> ReadRemoteFileAsync(
        [Description("Absolute path of the remote file to read.")] string path,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        try
        {
            byte[] bytes = await context.RemoteFs.ReadAllBytesAsync(id, path, MaxFileBytes, cancellationToken).ConfigureAwait(false);
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
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        try
        {
            IReadOnlyList<RemoteFileEntry> entries = await context.RemoteFs
                .ListDirectoryAsync(id, path, cancellationToken).ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return "(directory is empty)";
            }
            var sb = new StringBuilder();
            foreach (RemoteFileEntry entry in entries.Take(MaxDirectoryEntries))
            {
                sb.AppendLine(Describe(entry));
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

    private async Task<string> StatRemotePathAsync(
        [Description("Absolute remote path to inspect.")] string path,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        try
        {
            RemoteFileEntry? entry = await context.RemoteFs.StatAsync(id, path, cancellationToken).ConfigureAwait(false);
            return entry is null ? $"No such path: {path}" : Describe(entry);
        }
        catch (Exception ex)
        {
            return $"Failed to stat path: {ex.Message}";
        }
    }

    private async Task<string> GetWorkingDirectoryAsync(
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        try
        {
            return await context.RemoteFs.GetWorkingDirectoryAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Failed to get the working directory: {ex.Message}";
        }
    }

    /// <summary>
    /// 系统概览。命令写死在这儿,所以这条工具<b>结构上不可能有副作用</b> —— 不用审批。
    /// </summary>
    /// <remarks>
    /// 每段都带 <c>2>/dev/null</c> 并以标题分隔:各家发行版缺哪个命令都不影响其余几段,
    /// 模型拿到的仍是一份能读的概览,而不是一句 "command not found"。
    /// </remarks>
    private const string OverviewScript =
        "echo '## kernel'; uname -a 2>/dev/null; " +
        "echo '## os'; (cat /etc/os-release 2>/dev/null || sw_vers 2>/dev/null) | head -6; " +
        "echo '## uptime'; uptime 2>/dev/null; " +
        "echo '## cpu'; (nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null); " +
        "echo '## memory'; (free -h 2>/dev/null || vm_stat 2>/dev/null | head -6); " +
        "echo '## disk'; df -h 2>/dev/null | head -20";

    private async Task<string> SystemOverviewAsync(
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        return await ExecuteAsync(id, OverviewScript, 30, cancellationToken).ConfigureAwait(false);
    }

    // ---- 执行 ----

    private async Task<string> RunCommandAsync(
        [Description("The shell command to execute (non-interactive, one-shot).")] string command,
        [Description("Timeout in seconds (default 30, max 600).")] int timeoutSeconds = 30,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        // 记忆键取命令名(第一个词):同一次排查里 ls/cat/systemctl 会被反复调用,
        // 一条条点太折磨人;但键只到命令名为止,换个命令仍要重新点头。
        if (!await ApproveAsync(new ApprovalRequest("run_command", command, $"run_command:{FirstWord(command)}"),
                cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED execution of this command. Do not retry it; ask the user how to proceed.";
        }
        return await ExecuteAsync(id, command, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 同一条命令铺到多台上并行跑。
    /// </summary>
    /// <remarks>
    /// "这几台的磁盘都满了没" 是运维里最常见的一类问题,而逐台 <c>run_command</c> 意味着
    /// 逐台审批、逐轮等待。这里一次审批覆盖整批(摘要里列清了会打到哪几台),然后并行发出去。
    /// </remarks>
    private async Task<string> RunOnSessionsAsync(
        [Description("Session ids from list_sessions to run the command on.")] string[] sessionIds,
        [Description("The shell command to execute on every listed session (non-interactive, one-shot).")] string command,
        [Description("Timeout in seconds per session (default 30, max 600).")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (sessionIds is not { Length: > 0 })
        {
            return "session_ids must list at least one session id; call list_sessions first.";
        }
        if (sessionIds.Length > MaxParallelSessions)
        {
            return $"Refusing to fan out to more than {MaxParallelSessions} sessions in one call; split the batch.";
        }
        var targets = new List<SessionInfo>();
        foreach (string raw in sessionIds.Distinct(StringComparer.Ordinal))
        {
            SessionInfo? info = await context.Sessions.GetAsync(raw.Trim(), cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return $"No such session: {raw}. Call list_sessions to see the available ids.";
            }
            targets.Add(info);
        }
        // 审批摘要里把目标主机全列出来:批量执行最该让用户看清的就是"打到哪几台"
        string hosts = string.Join(", ", targets.Select(t => $"{t.Username}@{t.Host}"));
        if (!await ApproveAsync(new ApprovalRequest("run_on_sessions", $"{command}\n→ {hosts}"), cancellationToken)
                .ConfigureAwait(false))
        {
            return "The user DENIED this batch execution. Do not retry; ask the user how to proceed.";
        }
        // 并行发出去,但结果按传入顺序拼 —— 让模型(和用户)看到的次序是稳定的
        string[] outputs = await Task.WhenAll(targets.Select(t =>
            ExecuteAsync(t.SessionId, command, timeoutSeconds, cancellationToken))).ConfigureAwait(false);
        var sb = new StringBuilder();
        for (int i = 0; i < targets.Count; i++)
        {
            sb.AppendLine($"===== {targets[i].Username}@{targets[i].Host} ({targets[i].SessionId}) =====");
            sb.AppendLine(outputs[i]);
        }
        return sb.ToString();
    }

    /// <summary>跑一条命令并把输出整理成给模型看的文本(异常一律转文本,别中断整轮对话)。</summary>
    private async Task<string> ExecuteAsync(string sessionId, string command, int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            ExecResult result = await context.RemoteExec.RunAsync(sessionId, command,
                new ExecOptions { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)) },
                cancellationToken).ConfigureAwait(false);
            string output = result.Output;
            if (output.Length > MaxCommandOutput)
            {
                output = output[..MaxCommandOutput] + "\n…(output truncated)";
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

    // ---- 写 ----

    /// <summary>
    /// 按已保存的配置连一台机器。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是工具箱里唯一一个<b>要过两道人</b>的工具:这里的审批闸(面板的审批卡 / 群里的
    /// <c>y</c>/<c>n</c>),以及宿主自己的确认框。看着重复,其实问的不是同一件事 ——
    /// 前者是"这轮对话里要不要让 agent 这么干",后者是"要不要让这个<b>插件</b>替我连机器",
    /// 后者的答案由用户在宿主里一次性给定(可以选"始终允许"),此后无人值守的路才真正走得通。
    /// </para>
    /// <para>
    /// <c>reason</c> 会被宿主<b>原样</b>显示在确认框上,所以这里空理由直接退回给模型重写,
    /// 而不是拿一句占位符去糊弄用户 —— 一个没有理由的确认框只是一个让人盲点的按钮。
    /// </para>
    /// </remarks>
    private async Task<string> OpenSessionAsync(
        [Description("A saved_session_id from list_saved_sessions (NOT a session id).")] string savedSessionId,
        [Description("Why the connection is needed. Shown to the user VERBATIM in the host's confirmation dialog — "
                     + "say who is asking and what for, e.g. 'Feishu group Ops: Zhang San asked whether /var is full'.")] string reason,
        [Description("Reuse an already-connected session for the same configuration instead of opening a second one (default true).")] bool reuseConnected = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(savedSessionId))
        {
            return "saved_session_id must not be empty; call list_saved_sessions first.";
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "reason must not be empty: the user sees it verbatim in the confirmation dialog. "
                   + "State who is asking and what for, then call open_session again.";
        }
        string wanted = savedSessionId.Trim();
        IReadOnlyList<SavedSessionInfo> saved = await context.Sessions.ListSavedAsync(cancellationToken).ConfigureAwait(false);
        if (saved.FirstOrDefault(s => string.Equals(s.SavedSessionId, wanted, StringComparison.Ordinal)) is not { } target)
        {
            return $"No such saved session: {wanted}. Call list_saved_sessions to see the available ids.";
        }
        // 审批卡上要看得见连的是哪一台,以及模型给宿主的那句理由 ——
        // 用户在这里点头之后,同一句话还会出现在宿主的确认框上,两处对得上才不显得可疑。
        string label = $"{target.Name} ({target.Username}@{target.Host}:{target.Port})";
        if (!await ApproveAsync(new ApprovalRequest("open_session", $"{label}\n→ {reason.Trim()}"), cancellationToken)
                .ConfigureAwait(false))
        {
            return "The user DENIED opening this connection. Do not retry; ask the user how to proceed.";
        }
        try
        {
            SessionInfo opened = await context.Sessions
                .OpenAsync(wanted, new SessionOpenOptions(reason.Trim(), reuseConnected), cancellationToken)
                .ConfigureAwait(false);
            bool isDefault = SessionIdProvider?.Invoke() is null;
            _openedSessionId = opened.SessionId;
            return $"Connected to {opened.Username}@{opened.Host}:{opened.Port}. session_id = {opened.SessionId}. "
                   + (isDefault
                       ? "The other tools now act on it by default; close_session it when you are done."
                       : "Pass that session_id explicitly to the other tools — without it they keep acting on the session the user selected. "
                         + "Close it with close_session when you are done.");
        }
        catch (PluginPermissionDeniedException ex)
        {
            // "不让你连"与"没连上"分开处置:用户说了不,重试没有意义。
            return $"The host DENIED this connection ({ex.Message}). Do not retry; tell the user what you wanted to do and why.";
        }
        catch (PluginSessionOpenException ex)
        {
            return $"Could not connect to {label}: {ex.Message}. The user allowed it, so a later retry may work — "
                   + "but report the failure instead of hammering it.";
        }
        catch (PluginSessionNotFoundException)
        {
            return $"No such saved session: {wanted}. Call list_saved_sessions to see the available ids.";
        }
        catch (Exception ex)
        {
            return $"Failed to open the session: {ex.Message}";
        }
    }

    /// <summary>关掉本插件自己开的那条会话(宿主拒绝关别人的)。</summary>
    /// <remarks>
    /// 不走审批闸。关的对象已经被宿主限死在"本插件开的那些"里,用户自己的标签页一根汗毛都动不了;
    /// 而收拾自己开的东西再要一次点头,只会让 agent 干脆不收拾 —— 尤其在没有审批界面的
    /// MCP 那条路上(<c>Ask</c> 等于一律拒绝),那样必然攒下一堆没人认领的标签页。
    /// </remarks>
    private async Task<string> CloseSessionAsync(
        [Description("The session id returned by open_session.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "session_id must not be empty; pass the id open_session returned.";
        }
        string wanted = sessionId.Trim();
        try
        {
            await context.Sessions.CloseAsync(wanted, cancellationToken).ConfigureAwait(false);
            if (string.Equals(_openedSessionId, wanted, StringComparison.Ordinal))
            {
                _openedSessionId = null;
            }
            return $"Session {wanted} is closed.";
        }
        catch (PluginPermissionDeniedException)
        {
            return $"Session {wanted} was not opened by this assistant, so it cannot be closed here. "
                   + "Only the user can close their own sessions.";
        }
        catch (Exception ex)
        {
            return $"Failed to close the session: {ex.Message}";
        }
    }

    private async Task<string> WriteRemoteFileAsync(
        [Description("Absolute path of the remote file to overwrite (parent directory must exist).")] string path,
        [Description("The complete new content of the file (UTF-8). This replaces the whole file.")] string content,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
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
            await context.RemoteFs.WriteAllBytesAsync(id, path, Encoding.UTF8.GetBytes(content), cancellationToken)
                         .ConfigureAwait(false);
            return $"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {path}.";
        }
        catch (Exception ex)
        {
            return $"Failed to write file: {ex.Message}";
        }
    }

    /// <summary>
    /// 只换文件里的一段,其余原样不动。
    /// </summary>
    /// <remarks>
    /// <b>要求 <c>old_text</c> 在文件里恰好出现一次</b>,出现零次或多次都拒绝并说明。
    /// 这不是洁癖:改配置时"多处匹配"意味着模型并不确定自己在改哪一处,
    /// 挑第一处替换是运维场景里最容易造成事故的那种"聪明"。
    /// <para>
    /// 与 <c>write_remote_file</c> 的差别是实打实的:改一行 nginx.conf 不必把几百行原样回传一遍,
    /// 既省 token,也避开了模型复述长文本时丢内容的风险。
    /// </para>
    /// </remarks>
    private async Task<string> PatchRemoteFileAsync(
        [Description("Absolute path of the remote text file to patch.")] string path,
        [Description("The exact text to replace. It must occur EXACTLY ONCE in the file — include surrounding lines to make it unique.")] string oldText,
        [Description("The replacement text.")] string newText,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        if (string.IsNullOrEmpty(oldText))
        {
            return "old_text must not be empty; use write_remote_file to create a file from scratch.";
        }
        newText ??= "";
        string original;
        try
        {
            byte[] bytes = await context.RemoteFs.ReadAllBytesAsync(id, path, MaxFileBytes, cancellationToken).ConfigureAwait(false);
            original = Encoding.UTF8.GetString(bytes);
        }
        catch (InvalidOperationException)
        {
            return $"File is larger than {MaxFileBytes / 1024} KB; patch it with run_command (sed/awk) instead.";
        }
        catch (Exception ex)
        {
            return $"Failed to read file: {ex.Message}";
        }

        int occurrences = CountOccurrences(original, oldText);
        if (occurrences == 0)
        {
            return $"old_text was not found in {path}. Read the file first and copy the snippet exactly (whitespace included).";
        }
        if (occurrences > 1)
        {
            return $"old_text occurs {occurrences} times in {path}; it must be unique. "
                   + "Include more surrounding lines so it matches exactly one place.";
        }

        string patched = original.Replace(oldText, newText, StringComparison.Ordinal);
        // 审批摘要给的是"改哪一处",不是整份文件 —— 用户要判断的正是这一处
        string summary = $"{path}\n- {Clip(oldText)}\n+ {Clip(newText)}";
        if (!await ApproveAsync(new ApprovalRequest("patch_remote_file", summary), cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED this edit. Do not retry; ask the user how to proceed.";
        }
        try
        {
            await context.RemoteFs.WriteAllBytesAsync(id, path, Encoding.UTF8.GetBytes(patched), cancellationToken)
                         .ConfigureAwait(false);
            return $"Patched {path} ({original.Length} → {patched.Length} chars).";
        }
        catch (Exception ex)
        {
            return $"Failed to write the patched file: {ex.Message}";
        }
    }

    private async Task<string> MakeRemoteDirectoryAsync(
        [Description("Absolute path of the remote directory to create (missing parents are created too).")] string path,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        if (!await ApproveAsync(new ApprovalRequest("make_remote_directory", path), cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED creating this directory. Do not retry; ask the user how to proceed.";
        }
        try
        {
            // 幂等版:已存在不报错,省得模型先 stat 再建
            await context.RemoteFs.EnsureDirectoryAsync(id, path, cancellationToken).ConfigureAwait(false);
            return $"Directory ready: {path}";
        }
        catch (Exception ex)
        {
            return $"Failed to create directory: {ex.Message}";
        }
    }

    private async Task<string> RenameRemotePathAsync(
        [Description("Current absolute remote path.")] string oldPath,
        [Description("New absolute remote path (this is also how you move something).")] string newPath,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        // 不给记忆键:每次的来源与去向都不同,"总是允许"在这里等于放弃把关
        if (!await ApproveAsync(new ApprovalRequest("rename_remote_path", $"{oldPath}\n→ {newPath}"), cancellationToken)
                .ConfigureAwait(false))
        {
            return "The user DENIED this rename. Do not retry; ask the user how to proceed.";
        }
        try
        {
            await context.RemoteFs.RenameAsync(id, oldPath, newPath, cancellationToken).ConfigureAwait(false);
            return $"Renamed {oldPath} → {newPath}.";
        }
        catch (Exception ex)
        {
            return $"Failed to rename: {ex.Message}";
        }
    }

    // ---- 传输 ----

    /// <summary>
    /// 把<b>本机</b>的一个文件传到服务器。
    /// </summary>
    /// <remarks>
    /// 这条是为 MCP 工具准备的。MCP 服务器跑在用户自己的机器上,它产出的文件落在本机,
    /// 既不在 SSH 服务器上,也不在终端的当前目录里 —— 用户找不到、模型还常常当成远端路径去汇报。
    /// </remarks>
    private async Task<string> UploadLocalFileAsync(
        [Description("Absolute path of a file on the user's own machine (for example one an MCP tool just produced in its working directory).")] string localPath,
        [Description("Absolute destination path on the SSH server (parent directory must exist).")] string remotePath,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
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
        if (!await ApproveAsync(new ApprovalRequest("upload_local_file", $"{localPath}\n→ {remotePath}  ({info.Length} bytes)"),
                cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED this upload. Do not retry; ask the user how to proceed.";
        }
        try
        {
            // 走 SFTP 的流式上传,别先整份读进内存 —— xmind/压缩包这类产物可以很大
            await context.RemoteFs.UploadFileAsync(id, localPath, remotePath, cancellationToken: cancellationToken)
                         .ConfigureAwait(false);
            return $"Uploaded {info.Length} bytes: {localPath} → {remotePath}.";
        }
        catch (Exception ex)
        {
            return $"Failed to upload: {ex.Message}";
        }
    }

    /// <summary>
    /// 把服务器上的文件拉到<b>本机</b> —— <c>upload_local_file</c> 的反向。
    /// </summary>
    /// <remarks>
    /// 没有它,本地 MCP 工具就够不着远端的任何东西(日志、配置、导出的数据),
    /// 整条"远端取数 → 本地加工"的链路是断的。
    /// <para>
    /// 不指定本机路径时落到插件私有数据目录下的 <c>downloads/</c>:随插件卸载一并清除,
    /// 也不必让模型去猜用户机器上哪个目录可写。
    /// </para>
    /// </remarks>
    private async Task<string> DownloadRemoteFileAsync(
        [Description("Absolute path of the file on the SSH server.")] string remotePath,
        [Description("Optional absolute destination on the user's machine. Omit it to save into the plugin's downloads folder.")] string? localPath = null,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        remotePath = (remotePath ?? "").Trim();
        if (remotePath.Length == 0)
        {
            return "remote_path must not be empty.";
        }
        string target;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            string folder = Path.Combine(context.DataDirectory, "downloads");
            Directory.CreateDirectory(folder);
            target = Path.Combine(folder, Path.GetFileName(remotePath.TrimEnd('/')));
        }
        else if (!Path.IsPathRooted(localPath.Trim()))
        {
            return "local_path must be an absolute path on the user's machine (or omit it).";
        }
        else
        {
            target = localPath.Trim();
        }
        try
        {
            RemoteFileEntry? entry = await context.RemoteFs.StatAsync(id, remotePath, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return $"No such remote file: {remotePath}";
            }
            if (entry.IsDirectory)
            {
                return $"{remotePath} is a directory; archive it first (tar) and download the archive.";
            }
            if (entry.Size > MaxUploadBytes)
            {
                return $"Refusing to download more than {MaxUploadBytes / (1024 * 1024)} MB in one call ({entry.Size / (1024 * 1024)} MB).";
            }
            // 往用户自己的磁盘上写东西,一律问一次
            if (!await ApproveAsync(new ApprovalRequest("download_remote_file", $"{remotePath}\n→ {target}  ({entry.Size} bytes)"),
                    cancellationToken).ConfigureAwait(false))
            {
                return "The user DENIED this download. Do not retry; ask the user how to proceed.";
            }
            await context.RemoteFs.DownloadFileAsync(id, remotePath, target, cancellationToken: cancellationToken)
                         .ConfigureAwait(false);
            return $"Downloaded to {target} ({entry.Size} bytes). Local tools can read it from there.";
        }
        catch (Exception ex)
        {
            return $"Failed to download: {ex.Message}";
        }
    }

    private async Task<string> WriteTerminalAsync(
        [Description("Text to type into the terminal. Include a trailing \\n to execute it as a command.")] string text,
        [Description("Optional session id from list_sessions; omit to use the session selected in the panel.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        (string? id, string? error) = await ResolveAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return error!;
        }
        // 同样不给记忆键:往用户眼前的终端里敲字,每次都该问
        if (!await ApproveAsync(new ApprovalRequest("write_terminal", text), cancellationToken).ConfigureAwait(false))
        {
            return "The user DENIED typing into the terminal. Do not retry; ask the user how to proceed.";
        }
        try
        {
            await context.Terminal.WriteAsync(id, text, cancellationToken).ConfigureAwait(false);
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

    // ---- 公共零件 ----

    private const string NoSessionMessage =
        "No SSH session is selected/connected. Ask the user to connect and select a session in the AI panel first, "
        + "or pass an explicit session_id from list_sessions.";

    /// <summary>
    /// "一台都没有"时给模型的话。
    /// </summary>
    /// <remarks>
    /// 能开会话的时候要把那条路指出来 —— 这正是从前"值班的人昨晚关了那个标签页,
    /// 机器人只能回一句你先去连一台"的出口。但计划模式下 <c>open_session</c> 压根没注册,
    /// 那时提它只会让模型去调一个不存在的工具,然后把这次失败当成自己的问题。
    /// </remarks>
    private string NoSession =>
        _mode != ChatMode.Plan && !DisabledTools.Contains("open_session")
            ? NoSessionMessage
              + " If the machine is saved in VelaShell but not connected, call list_saved_sessions and then open_session "
              + "(the user will be asked to confirm) instead of telling the user to go and connect it themselves."
            : NoSessionMessage;

    /// <summary>
    /// 解出这次调用该落在哪个会话上:显式传了就用那个(先核实存在),否则用界面上选中的那个,
    /// 再没有就用本轮 <c>open_session</c> 自己开出来的那条。
    /// </summary>
    /// <remarks>
    /// 认不出来的 id <b>当场给出可操作的回答</b>而不是让 SDK 抛一个
    /// <c>PluginSessionNotFoundException</c> —— 模型看到"去 list_sessions 拿 id"会自己纠正,
    /// 看到一个异常堆栈则往往就地放弃。
    /// <para>
    /// 兜底那一档的位置是刻意的:排在选中项<b>之后</b>。理由见 <see cref="_openedSessionId" />。
    /// </para>
    /// </remarks>
    private async Task<(string? Id, string? Error)> ResolveAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            string wanted = sessionId.Trim();
            SessionInfo? info = await context.Sessions.GetAsync(wanted, cancellationToken).ConfigureAwait(false);
            return info is null
                ? (null, $"No such session: {wanted}. Call list_sessions to see the available ids.")
                : (info.SessionId, null);
        }
        if (SessionIdProvider?.Invoke() is { } current)
        {
            return (current, null);
        }
        // 自己开的那条:用户手动关掉之后就不该再往它上面发东西,所以每次都核实一下还在不在。
        if (_openedSessionId is { } opened)
        {
            if (await context.Sessions.GetAsync(opened, cancellationToken).ConfigureAwait(false) is { } live)
            {
                return (live.SessionId, null);
            }
            _openedSessionId = null;
        }
        return (null, NoSession);
    }

    private static string Describe(RemoteFileEntry entry) => JsonSerializer.Serialize(new
    {
        name = entry.Name,
        path = entry.FullPath,
        type = entry.IsDirectory ? "dir" : "file",
        size = entry.Size,
        modified = entry.LastModified.ToString("u"),
        permissions = entry.Permissions
    });

    /// <summary>数一段文本在另一段里出现几次(不重叠)。</summary>
    internal static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    /// <summary>审批摘要里的片段截断:够看清改了什么,又不至于把弹窗撑爆。</summary>
    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";

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
