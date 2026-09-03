using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Docking;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 工作台连接(Redis 等由插件全权渲染界面的类型)连不上时**必须有提示**。
/// <para>
/// 这条路径与 SSH 的关键差别:连不上就没有标签页,失败没有地方可画 —— 只写状态栏的话
/// 用户看到的是"点了连接,什么都没发生"。本机没起 Redis 时点开一条 Redis 会话正是这个样子,
/// 这组用例守的就是那扇提示框还在。
/// </para>
/// </summary>
[TestClass]
public sealed class WorkspaceConnectionFailureTests
{
    private const string WorkspaceId = "acme.cache";

    /// <summary>连接必失败的工作台:一次握手都不放过去。</summary>
    private sealed class FailingWorkspaceProvider(Exception failure) : IWorkspaceProvider
    {
        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IWorkspaceDocument>(failure);
    }

    private static (MainWindowViewModel Vm, SessionProfile Profile) Arrange(Exception failure)
    {
        var registry = new PluginProtocolRegistry();
        registry.RegisterWorkspace(
            WorkspaceId,
            new()
            {
                Id = WorkspaceId,
                DisplayName = "Acme Cache",
                DefaultPort = 6379,
                // 匿名可连:与 Redis 一样,不填口令不该弹登录框 —— 否则这组用例
                // 测到的是弹凭据,而不是失败提示。
                Features = WorkspaceFeatures.AnonymousAccess
            },
            new FailingWorkspaceProvider(failure));
        var vm = new MainWindowViewModel(
            protocolRegistry: registry,
            workspaceLauncher: new PluginWorkspaceLauncher(registry));
        return (vm, new()
        {
            Name = "本地 Redis",
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = WorkspaceId,
            Host = "127.0.0.1",
            Port = 6379
        });
    }

    /// <summary>
    /// 端点不可达(本机没起 Redis):不开标签页,但要把原因报到提示钩子上。
    /// </summary>
    [TestMethod]
    public async Task ConnectionRefused_ReportsTheFailure_AndOpensNoDocument()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolConnectionException("连不上 127.0.0.1:6379:Connection refused"));
        var reported = new List<string>();
        vm.ConnectionFailureReporter = (_, message) =>
        {
            reported.Add(message);
            return Task.CompletedTask;
        };

        PluginWorkspaceDocument? document = await vm.OpenWorkspaceDocumentForProfileAsync(profile);

        Assert.IsNull(document);
        Assert.HasCount(1, reported);
        Assert.Contains("Connection refused", reported[0]);
        // 匿名连接(Redis 常态)不该拼出 "@127.0.0.1:6379" 这种前面缺了一截的目标。
        Assert.Contains("127.0.0.1:6379", reported[0]);
        Assert.DoesNotContain("@127.0.0.1", reported[0]);
        // 状态栏与 LastConnectionError 仍是同一条消息:插件代开会话那条路径靠它区分
        // "没连上"与"人不同意"(见 HostSessionOpener)。
        Assert.AreEqual(reported[0], vm.LastConnectionError);
        Assert.IsEmpty(vm.Layout.AllDocuments());
    }

    /// <summary>
    /// 没挂提示钩子(headless 单测、插件代开会话)时连接流程照旧,只是不弹框。
    /// </summary>
    [TestMethod]
    public async Task WithoutAReporter_TheFailureStillLandsOnLastConnectionError()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolConnectionException("端点不可达"));

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.IsNotNull(vm.LastConnectionError);
        Assert.Contains("端点不可达", vm.LastConnectionError);
    }

    /// <summary>
    /// 三次凭据都没过之后,循环走完了也要报一次 —— 否则用户对着密码框输三遍,
    /// 得到的是一片安静。
    /// </summary>
    [TestMethod]
    public async Task ExhaustedAuthenticationRetries_ReportsTheLastFailure()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolAuthenticationException("WRONGPASS 口令不对"));
        profile.Username = "default";
        int prompts = 0;
        vm.InteractiveAuthenticator = candidate =>
        {
            prompts++;
            candidate.Password = "nope";
            return Task.FromResult<SessionProfile?>(candidate);
        };
        var reported = new List<string>();
        vm.ConnectionFailureReporter = (_, message) =>
        {
            reported.Add(message);
            return Task.CompletedTask;
        };

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.AreEqual(3, prompts);
        Assert.HasCount(1, reported);
        Assert.Contains("WRONGPASS", reported[0]);
    }

    /// <summary>
    /// 用户在凭据框上点取消是"不连了",不是失败:不弹提示,并且要把上一条错误清掉
    /// —— <c>HostSessionOpener</c> 正是靠 <c>LastConnectionError</c> 为空来区分这两者的。
    /// </summary>
    [TestMethod]
    public async Task CancelledCredentialPrompt_ReportsNothing()
    {
        (MainWindowViewModel vm, SessionProfile profile) =
            Arrange(new ProtocolConnectionException("不该走到这里"));
        profile.Username = "default";
        vm.InteractiveAuthenticator = _ => Task.FromResult<SessionProfile?>(null);
        int reports = 0;
        vm.ConnectionFailureReporter = (_, _) =>
        {
            reports++;
            return Task.CompletedTask;
        };

        Assert.IsNull(await vm.OpenWorkspaceDocumentForProfileAsync(profile));

        Assert.AreEqual(0, reports);
        Assert.IsNull(vm.LastConnectionError);
    }
}
