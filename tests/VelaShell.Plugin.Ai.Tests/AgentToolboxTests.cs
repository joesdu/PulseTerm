using System.Text;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>Agent 工具箱:工具形状、审批闸门与各工具对插件能力的桥接语义。</summary>
[TestClass]
public sealed class AgentToolboxTests
{
    private static readonly string[] ExpectedToolNames =
        ["list_sessions", "read_terminal", "run_command", "read_remote_file", "write_terminal"];

    private static async Task<string> InvokeAsync(AgentToolbox toolbox, string name, Dictionary<string, object?>? args = null)
    {
        AIFunction function = toolbox.CreateTools().OfType<AIFunction>().Single(f => f.Name == name);
        object? result = await function.InvokeAsync(new AIFunctionArguments(args ?? []), CancellationToken.None);
        return result?.ToString() ?? "";
    }

    [TestMethod]
    public void CreateTools_ExposesExpectedToolSet()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context);

        string[] names = toolbox.CreateTools().OfType<AIFunction>().Select(f => f.Name).ToArray();

        Assert.AreSequenceEqual(ExpectedToolNames, names, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

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
            AutoApprove = true
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
