using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Manifest;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 工作台连接类型的宿主侧接线:清单校验、注册表(声明 → 注册 → 惰性激活 → 注销)、
/// 能力面的 id 纪律,以及 <see cref="PluginWorkspaceLauncher" /> 的异常翻译。
/// <para>
/// 这一层守住的是"插件全权渲染的会话与 SSH/SFTP 同为一等公民"这条承诺:
/// 页签、凭据、惰性激活、会话生命周期全部复用宿主既有机制,插件只管交出一个控件。
/// </para>
/// </summary>
// 同 IsolatedPluginTests:涉及真实插件进程与按墙钟的等待,串行执行避免并行下的时序抖动。
[TestClass]
[DoNotParallelize]
public sealed class PluginWorkspaceTests
{
    private const string PluginId = "acme.cache";
    private const string WorkspaceId = "acme.cache";

    private static WorkspaceDescriptor Descriptor(params ProtocolSettingField[] fields) =>
        new()
        {
            Id = WorkspaceId,
            DisplayName = "Acme Cache",
            DefaultPort = 6379,
            Features = WorkspaceFeatures.AnonymousAccess,
            Fields = fields
        };

    private static SessionProfile Profile(string? workspaceId = WorkspaceId) =>
        new()
        {
            ConnectionType = ConnectionType.Plugin,
            Host = "cache.example.com",
            Port = 6379,
            PluginProtocolId = workspaceId
        };

    // ── 清单校验 ──────────────────────────────────────────────────

    /// <summary>
    /// 工作台 + 隔离进程要在**发现期**就被拒:宿主要向插件索取一个 Avalonia 控件挂进停靠区,
    /// 而原生控件无法跨进程嵌入。让这种清单装上去,表现会是"页签在、点了打不开"。
    /// </summary>
    [TestMethod]
    public void Manifest_WorkspacesWithIsolatedHostMode_IsRejected()
    {
        PluginManifestException error = Assert.ThrowsExactly<PluginManifestException>(() =>
            PluginManifestReader.Parse("""
            {
              "id": "acme.cache", "version": "1.0.0", "displayName": "Acme",
              "entry": "Acme.dll", "hostMode": "isolated",
              "contributes": { "workspaces": [ { "id": "acme.cache", "displayName": "Acme", "defaultPort": 6379 } ] }
            }
            """));

        Assert.Contains("inProcess", error.Message);
    }

    /// <summary>激活事件拼错(或忘了声明贡献)不许静默失效 —— 那会变成"插件永远不激活"。</summary>
    [TestMethod]
    public void Manifest_OnWorkspaceWithoutContribution_IsRejected()
    {
        PluginManifestException error = Assert.ThrowsExactly<PluginManifestException>(() =>
            PluginManifestReader.Parse("""
            {
              "id": "acme.cache", "version": "1.0.0", "displayName": "Acme", "entry": "Acme.dll",
              "activationEvents": [ "onWorkspace:acme.cache" ]
            }
            """));

        Assert.Contains("contributes.workspaces", error.Message);
    }

    /// <summary>
    /// 同一份清单里工作台 id 与协议 id 不得相撞:两者在连接页上是同一排页签,
    /// 也落进同一个 <c>PluginProtocolId</c> 字段,撞了就再也分不清是哪一种。
    /// </summary>
    [TestMethod]
    public void Manifest_WorkspaceIdCollidingWithProtocolId_IsRejected()
    {
        PluginManifestException error = Assert.ThrowsExactly<PluginManifestException>(() =>
            PluginManifestReader.Parse("""
            {
              "id": "acme.cache", "version": "1.0.0", "displayName": "Acme", "entry": "Acme.dll",
              "contributes": {
                "protocols":  [ { "id": "acme.cache", "displayName": "P", "defaultPort": 443 } ],
                "workspaces": [ { "id": "acme.cache", "displayName": "W", "defaultPort": 6379 } ]
              }
            }
            """));

        Assert.Contains("Duplicate", error.Message);
    }

    /// <summary>id 必须以插件 id 为前缀(防插件间冒名),且全小写(防大小写歧义)。</summary>
    [TestMethod]
    public void Manifest_ForeignWorkspaceId_IsRejected()
    {
        Assert.ThrowsExactly<PluginManifestException>(() =>
            PluginManifestReader.Parse("""
            {
              "id": "acme.cache", "version": "1.0.0", "displayName": "Acme", "entry": "Acme.dll",
              "contributes": { "workspaces": [ { "id": "other.vendor", "displayName": "W", "defaultPort": 6379 } ] }
            }
            """));
    }

    /// <summary>合法的工作台清单要能过,且能被读出来。</summary>
    [TestMethod]
    public void Manifest_ValidWorkspaceContribution_IsAccepted()
    {
        PluginManifest manifest = PluginManifestReader.Parse("""
        {
          "id": "acme.cache", "version": "1.0.0", "displayName": "Acme", "entry": "Acme.dll",
          "contributes": { "workspaces": [ { "id": "acme.cache", "displayName": "Acme Cache", "defaultPort": 6379 } ] },
          "activationEvents": [ "onWorkspace:acme.cache" ]
        }
        """);

        WorkspaceContribution contribution = manifest.Contributes!.Workspaces.Single();
        Assert.AreEqual("acme.cache", contribution.Id);
        Assert.AreEqual(6379, contribution.DefaultPort);
        Assert.IsFalse(manifest.ActivatesOnStartup, "onWorkspace 是惰性激活,不该被当成启动激活。");
    }

    // ── 注册表 ────────────────────────────────────────────────────

    /// <summary>清单声明的页签在**不装载程序集**的前提下就要出现,并带上"工作台"这个形态。</summary>
    [TestMethod]
    public void DeclareWorkspaces_MakesTheTabVisibleWithWorkspaceKind()
    {
        var registry = new PluginProtocolRegistry();

        registry.DeclareWorkspaces(PluginId, [new() { Id = WorkspaceId, DisplayName = "Acme Cache", DefaultPort = 6379 }]);

        PluginProtocolTab tab = registry.Tabs.Single();
        Assert.AreEqual(PluginConnectionKind.Workspace, tab.Kind);
        Assert.IsFalse(tab.IsReady);
        // 形态查询是同步的、不装载任何程序集 —— 会话树画图标、双击决定开什么都要靠它。
        Assert.AreEqual(PluginConnectionKind.Workspace, registry.KindOf(WorkspaceId));
        Assert.IsFalse(registry.TryGetWorkspace(WorkspaceId, out _));
    }

    /// <summary>两种形态共处一张表时,各自的 <c>Kind</c> 不能串。</summary>
    [TestMethod]
    public void Registry_KeepsFileSystemAndWorkspaceKindsApart()
    {
        var registry = new PluginProtocolRegistry();

        registry.Declare("acme.files", [new() { Id = "acme.files", DisplayName = "Files", DefaultPort = 443 }]);
        registry.DeclareWorkspaces(PluginId, [new() { Id = WorkspaceId, DisplayName = "Cache", DefaultPort = 6379 }]);

        Assert.AreEqual(PluginConnectionKind.FileSystem, registry.KindOf("acme.files"));
        Assert.AreEqual(PluginConnectionKind.Workspace, registry.KindOf(WorkspaceId));
        Assert.IsNull(registry.KindOf("nobody.knows"));
    }

    /// <summary>激活后注册的实现覆盖掉声明,页签转为就绪。</summary>
    [TestMethod]
    public void RegisterWorkspace_ReplacesTheDeclarationAndMarksItReady()
    {
        var registry = new PluginProtocolRegistry();
        registry.DeclareWorkspaces(PluginId, [new() { Id = WorkspaceId, DisplayName = "Cache", DefaultPort = 6379 }]);
        var provider = new FakeWorkspaceProvider();

        registry.RegisterWorkspace(PluginId, Descriptor(), provider);

        PluginProtocolTab tab = registry.Tabs.Single();
        Assert.IsTrue(tab.IsReady);
        Assert.AreEqual(PluginConnectionKind.Workspace, tab.Kind);
        Assert.IsTrue(registry.TryGetWorkspace(WorkspaceId, out PluginWorkspaceRegistration registration));
        Assert.AreSame(provider, registration.Provider);
    }

    /// <summary>只被声明过的工作台在解析时触发惰性激活 —— "用户点到页签才装载插件"。</summary>
    [TestMethod]
    public async Task ResolveWorkspaceAsync_TriggersLazyActivation()
    {
        var registry = new PluginProtocolRegistry();
        registry.DeclareWorkspaces(PluginId, [new() { Id = WorkspaceId, DisplayName = "Cache", DefaultPort = 6379 }]);
        var provider = new FakeWorkspaceProvider();
        int activations = 0;
        registry.ActivationRequested = _ =>
        {
            activations++;
            registry.RegisterWorkspace(PluginId, Descriptor(), provider);
            return Task.FromResult(true);
        };

        Assert.IsNotNull(await registry.ResolveWorkspaceAsync(WorkspaceId));
        Assert.AreEqual(1, activations);
        // 已就绪后不再重复激活。
        await registry.ResolveWorkspaceAsync(WorkspaceId);
        Assert.AreEqual(1, activations);
    }

    /// <summary>停用/卸载插件要连声明一起撤,并通知界面收掉它名下的文档。</summary>
    [TestMethod]
    public void RemovePlugin_DropsWorkspaceDeclarationsAndRegistrations()
    {
        var registry = new PluginProtocolRegistry();
        registry.DeclareWorkspaces(PluginId, [new() { Id = WorkspaceId, DisplayName = "Cache", DefaultPort = 6379 }]);
        registry.RegisterWorkspace(PluginId, Descriptor(), new FakeWorkspaceProvider());
        List<string> unregistered = [];
        registry.Unregistered += unregistered.Add;

        registry.RemovePlugin(PluginId);

        Assert.IsEmpty(registry.Tabs);
        Assert.IsFalse(registry.TryGetWorkspace(WorkspaceId, out _));
        Assert.Contains(WorkspaceId, unregistered);
    }

    /// <summary>
    /// 同 id 换成**同一个** provider 实例(插件为换语言而重注册)不得触发注销 ——
    /// 那会把用户正开着的标签页全部掐掉。
    /// </summary>
    [TestMethod]
    public void RegisterWorkspace_SameProviderAgain_DoesNotAbandonOpenDocuments()
    {
        var registry = new PluginProtocolRegistry();
        var provider = new FakeWorkspaceProvider();
        registry.RegisterWorkspace(PluginId, Descriptor(), provider);
        List<string> unregistered = [];
        registry.Unregistered += unregistered.Add;

        registry.RegisterWorkspace(PluginId, Descriptor() with { DisplayName = "缓存" }, provider);

        Assert.IsEmpty(unregistered);
        Assert.AreEqual("缓存", registry.Tabs.Single().DisplayName);
    }

    /// <summary>换成**另一个**实现则必须通知:旧实现名下的文档已无人应答。</summary>
    [TestMethod]
    public void RegisterWorkspace_DifferentProvider_AbandonsOldDocuments()
    {
        var registry = new PluginProtocolRegistry();
        registry.RegisterWorkspace(PluginId, Descriptor(), new FakeWorkspaceProvider());
        List<string> unregistered = [];
        registry.Unregistered += unregistered.Add;

        registry.RegisterWorkspace(PluginId, Descriptor(), new FakeWorkspaceProvider());

        Assert.Contains(WorkspaceId, unregistered);
    }

    /// <summary>注销句柄只撤自己那一份:同 id 被后来者替换过时不能把别人的注册删掉。</summary>
    [TestMethod]
    public void RegisterWorkspace_StaleHandleDispose_IsANoOp()
    {
        var registry = new PluginProtocolRegistry();
        IDisposable first = registry.RegisterWorkspace(PluginId, Descriptor(), new FakeWorkspaceProvider());
        var second = new FakeWorkspaceProvider();
        registry.RegisterWorkspace(PluginId, Descriptor(), second);

        first.Dispose();

        Assert.IsTrue(registry.TryGetWorkspace(WorkspaceId, out PluginWorkspaceRegistration registration));
        Assert.AreSame(second, registration.Provider);
    }

    // ── 能力面 ────────────────────────────────────────────────────

    /// <summary>
    /// id 会落进用户的会话配置,必须以插件 id 为前缀、且全小写。
    /// **测的是真实的 <see cref="WorkspacesCapability" />**,与清单校验共用同一个判定。
    /// </summary>
    [TestMethod]
    public void Capability_ForeignId_IsRejected()
    {
        var registry = new PluginProtocolRegistry();
        using var capability = new WorkspacesCapability(PluginId, registry, new CollectingLogger());

        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { Id = "other.vendor" }, new FakeWorkspaceProvider()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { Id = "Acme.Cache" }, new FakeWorkspaceProvider()));
    }

    /// <summary>端口必须在 1–65535 内:声明 0 的话新建配置会预填一个连不上的端口。</summary>
    [TestMethod]
    public void Capability_OutOfRangePort_IsRejected()
    {
        var registry = new PluginProtocolRegistry();
        using var capability = new WorkspacesCapability(PluginId, registry, new CollectingLogger());

        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(Descriptor() with { DefaultPort = 0 }, new FakeWorkspaceProvider()));
    }

    /// <summary>
    /// 指纹要写回的字段必须真的存在,否则"信任该证书"点了等于没点 ——
    /// 这类失败只在真机上暴露,代价是用户对着同一个弹窗点三次都连不上。
    /// </summary>
    [TestMethod]
    public void Capability_ThumbprintKeyPointingAtNothing_IsRejected()
    {
        var registry = new PluginProtocolRegistry();
        using var capability = new WorkspacesCapability(PluginId, registry, new CollectingLogger());

        Assert.ThrowsExactly<ArgumentException>(() =>
            capability.Register(
                Descriptor() with { TrustedThumbprintSettingKey = "nope" },
                new FakeWorkspaceProvider()));

        // 字段确实存在时放行。
        capability.Register(
            Descriptor(new ProtocolSettingField { Key = "thumb", Label = "T", IsHidden = true })
                with
            { TrustedThumbprintSettingKey = "thumb" },
            new FakeWorkspaceProvider());
        Assert.IsTrue(registry.TryGetWorkspace(WorkspaceId, out _));
    }

    /// <summary>能力被释放(插件停用)时撤掉全部注册 —— 这是可收集 ALC 能真正回收的前提。</summary>
    [TestMethod]
    public void Capability_Dispose_UnregistersEverything()
    {
        var registry = new PluginProtocolRegistry();
        var capability = new WorkspacesCapability(PluginId, registry, new CollectingLogger());
        capability.Register(Descriptor(), new FakeWorkspaceProvider());

        capability.Dispose();

        Assert.IsFalse(registry.TryGetWorkspace(WorkspaceId, out _));
    }

    // ── 启动器 ────────────────────────────────────────────────────

    /// <summary>
    /// 插件没装/被禁用时,给"这条配置暂时无处可去"而不是"连接失败" ——
    /// 后者会让用户反复重试一个永远连不上的地址。
    /// </summary>
    [TestMethod]
    public async Task Launcher_UnknownWorkspace_ReportsUnavailable()
    {
        var launcher = new PluginWorkspaceLauncher(new PluginProtocolRegistry());

        PluginProtocolUnavailableException error =
            await Assert.ThrowsExactlyAsync<PluginProtocolUnavailableException>(
                () => launcher.OpenAsync(Profile()));

        Assert.AreEqual(WorkspaceId, error.ProtocolId);
    }

    /// <summary>配置里根本没记连接类型 id 时也走同一条路(而不是空引用)。</summary>
    [TestMethod]
    public async Task Launcher_ProfileWithoutWorkspaceId_ReportsUnavailable()
    {
        var launcher = new PluginWorkspaceLauncher(new PluginProtocolRegistry());

        await Assert.ThrowsExactlyAsync<PluginProtocolUnavailableException>(
            () => launcher.OpenAsync(Profile(null)));
    }

    /// <summary>
    /// 打开成功后:请求里带上宿主分配的会话 id、凭据,以及**补齐默认值的**设置。
    /// 补默认值这一条让插件后来新增的字段对老配置也成立。
    /// </summary>
    [TestMethod]
    public async Task Launcher_OpenAsync_PassesCredentialsAndDefaultedSettings()
    {
        var registry = new PluginProtocolRegistry();
        var provider = new FakeWorkspaceProvider();
        registry.RegisterWorkspace(PluginId, Descriptor(
            new ProtocolSettingField { Key = "mode", Label = "Mode", DefaultValue = "standalone" },
            new ProtocolSettingField { Key = "db", Label = "DB", DefaultValue = "0" }), provider);
        var launcher = new PluginWorkspaceLauncher(registry);
        SessionProfile profile = Profile();
        profile.Username = "acl-user";
        profile.Password = "secret";
        profile.PluginSettings = new Dictionary<string, string>(StringComparer.Ordinal) { ["db"] = "3" };

        PluginWorkspaceSession session = await launcher.OpenAsync(profile);

        Assert.AreEqual("Acme Cache", session.TypeName);
        Assert.AreNotEqual(Guid.Empty, session.SessionId);
        WorkspaceConnectRequest request = provider.LastRequest!;
        Assert.AreEqual("acl-user", request.Username);
        Assert.AreEqual("secret", request.Password);
        Assert.AreEqual("standalone", request.GetString("mode"), "没填过的字段要补上声明的默认值。");
        Assert.AreEqual("3", request.GetString("db"), "填过的字段要覆盖默认值。");
    }

    /// <summary>机密字段与普通设置合并进同一张表(插件不必区分它们从哪儿来)。</summary>
    [TestMethod]
    public async Task Launcher_MergesSecretsIntoSettings()
    {
        var registry = new PluginProtocolRegistry();
        var provider = new FakeWorkspaceProvider();
        registry.RegisterWorkspace(PluginId, Descriptor(), provider);
        var launcher = new PluginWorkspaceLauncher(registry);
        SessionProfile profile = Profile();
        profile.PluginSecrets = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "t0ken" };

        await launcher.OpenAsync(profile);

        Assert.AreEqual("t0ken", provider.LastRequest!.GetString("token"));
    }

    /// <summary>
    /// SDK 异常翻成 Core 中立异常族:界面只认后者。认证失败必须单独认出来 ——
    /// 宿主看到它才会重弹登录框,而"连不上"对"密码打错了"是最无用的反馈。
    /// </summary>
    [TestMethod]
    public async Task Launcher_TranslatesSdkExceptions()
    {
        var registry = new PluginProtocolRegistry();
        var provider = new FakeWorkspaceProvider();
        registry.RegisterWorkspace(PluginId, Descriptor(), provider);
        var launcher = new PluginWorkspaceLauncher(registry);

        provider.Failure = new ProtocolAuthenticationException("bad password");
        await Assert.ThrowsExactlyAsync<PluginProtocolAuthenticationException>(() => launcher.OpenAsync(Profile()));

        provider.Failure = new ProtocolConnectionException("no route");
        await Assert.ThrowsExactlyAsync<PluginProtocolConnectionException>(() => launcher.OpenAsync(Profile()));

        provider.Failure = new ProtocolUnsupportedException("CONFIG is disabled");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => launcher.OpenAsync(Profile()));
    }

    /// <summary>证书未信任时,指纹要写回的字段键由**描述**提供 —— 宿主并不知道插件管它叫什么。</summary>
    [TestMethod]
    public async Task Launcher_CertificateFailure_CarriesTheThumbprintSettingKey()
    {
        var registry = new PluginProtocolRegistry();
        var provider = new FakeWorkspaceProvider
        {
            Failure = new ProtocolCertificateTrustException(
                "untrusted", "CN=cache", "CN=self", DateTimeOffset.UnixEpoch, "AABB", "RemoteCertificateNameMismatch")
        };
        registry.RegisterWorkspace(PluginId, Descriptor(
            new ProtocolSettingField { Key = "thumb", Label = "T", IsHidden = true })
            with
        { TrustedThumbprintSettingKey = "thumb" }, provider);
        var launcher = new PluginWorkspaceLauncher(registry);

        PluginProtocolCertificateException error =
            await Assert.ThrowsExactlyAsync<PluginProtocolCertificateException>(() => launcher.OpenAsync(Profile()));

        Assert.AreEqual("thumb", error.SettingKey);
        Assert.AreEqual("AABB", error.Thumbprint);
    }

    /// <summary>连接类型被注销时,启动器要把它名下还开着的会话报给界面去关。</summary>
    [TestMethod]
    public async Task Launcher_OnUnregistered_AbandonsItsSessions()
    {
        var registry = new PluginProtocolRegistry();
        registry.RegisterWorkspace(PluginId, Descriptor(), new FakeWorkspaceProvider());
        var launcher = new PluginWorkspaceLauncher(registry);
        PluginWorkspaceSession session = await launcher.OpenAsync(Profile());
        List<Guid> abandoned = [];
        launcher.SessionAbandoned += abandoned.Add;

        launcher.OnUnregistered(WorkspaceId);

        Assert.AreSequenceEqual([session.SessionId], abandoned);
        // 已经报过的会话不会再报第二次(界面关闭标签页时也会 Forget)。
        abandoned.Clear();
        launcher.OnUnregistered(WorkspaceId);
        Assert.IsEmpty(abandoned);
    }

    /// <summary>界面关掉标签页后把会话摘掉,插件停用时不该再为它发一次"已失效"。</summary>
    [TestMethod]
    public async Task Launcher_Forget_RemovesTheSessionFromAbandonNotifications()
    {
        var registry = new PluginProtocolRegistry();
        registry.RegisterWorkspace(PluginId, Descriptor(), new FakeWorkspaceProvider());
        var launcher = new PluginWorkspaceLauncher(registry);
        PluginWorkspaceSession session = await launcher.OpenAsync(Profile());
        List<Guid> abandoned = [];
        launcher.SessionAbandoned += abandoned.Add;

        launcher.Forget(session.SessionId);
        launcher.OnUnregistered(WorkspaceId);

        Assert.IsEmpty(abandoned);
    }

    /// <summary>测试替身:记录请求、按需失败、交出一个什么都不做的文档。</summary>
    private sealed class FakeWorkspaceProvider : IWorkspaceProvider
    {
        public WorkspaceConnectRequest? LastRequest { get; private set; }

        public Exception? Failure { get; set; }

        public Task<IWorkspaceDocument> OpenAsync(
            WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Failure is { } failure
                ? Task.FromException<IWorkspaceDocument>(failure)
                : Task.FromResult<IWorkspaceDocument>(new FakeWorkspaceDocument());
        }
    }

    private sealed class FakeWorkspaceDocument : IWorkspaceDocument
    {
        public WorkspaceStatus Status { get; } = new(ProtocolSessionState.Connected);

        public event EventHandler<WorkspaceStatus>? StatusChanged;

        public object CreateView() => new();

        public Task ReconnectAsync(CancellationToken cancellationToken = default)
        {
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
