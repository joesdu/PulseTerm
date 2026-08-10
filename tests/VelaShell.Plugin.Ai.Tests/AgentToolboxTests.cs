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

        CollectionAssert.AreEquivalent(
            new[] { "list_sessions", "read_terminal", "run_command", "read_remote_file", "write_terminal" },
            names);
    }

    [TestMethod]
    public async Task ListSessions_ReportsConnectedSessions()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        var toolbox = new AgentToolbox(context);

        string result = await InvokeAsync(toolbox, "list_sessions");

        StringAssert.Contains(result, "prod-1");
        StringAssert.Contains(result, "root");
    }

    [TestMethod]
    public async Task ReadTerminal_WithoutSelectedSession_ReturnsHintInsteadOfThrowing()
    {
        using var context = new TestPluginContext();
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => null };

        string result = await InvokeAsync(toolbox, "read_terminal");

        StringAssert.Contains(result, "No SSH session");
    }

    [TestMethod]
    public async Task ReadTerminal_ReturnsBufferTail()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        context.FakeTerminal.Output[session.SessionId] = ["$ systemctl status nginx", "active (running)"];
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "read_terminal");

        StringAssert.Contains(result, "active (running)");
    }

    [TestMethod]
    public async Task RunCommand_WithoutApprovalHandler_IsDeniedAndNotExecuted()
    {
        using var context = new TestPluginContext();
        SessionInfo session = context.FakeSessions.AddConnected();
        var toolbox = new AgentToolbox(context) { SessionIdProvider = () => session.SessionId };

        string result = await InvokeAsync(toolbox, "run_command", new() { ["command"] = "rm -rf /" });

        StringAssert.Contains(result, "DENIED");
        Assert.AreEqual(0, context.FakeRemoteExec.Executed.Count);
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
        Assert.AreEqual(1, context.FakeRemoteExec.Executed.Count);
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

        StringAssert.Contains(result, "web-01");
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

        StringAssert.Contains(result, "denied");
        Assert.AreEqual(0, context.FakeTerminal.Writes.Count);
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

        StringAssert.Contains(result, "typed");
        Assert.AreEqual(1, context.FakeTerminal.Writes.Count);
        Assert.AreEqual("echo hi\n", context.FakeTerminal.Writes[0].Input);
    }
}
