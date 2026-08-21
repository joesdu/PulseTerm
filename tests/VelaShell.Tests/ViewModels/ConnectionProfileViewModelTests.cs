using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class ConnectionProfileViewModelTests
{
    // 密码 ASCII 过滤已从 ViewModel 下沉到 SecurePasswordBox 输入行为。
    [TestMethod]
    [DataRow("pä中文ss123", "pss123")]
    [DataRow("secret!", "secret!")]
    [DataRow("密码", "")]
    public void FilterAscii_StripsNonAsciiCharacters(string input, string expected) => Assert.AreEqual(expected, SecurePasswordBox.FilterAscii(input));

    [TestMethod]
    public void PluginFields_MarkedAdvanced_StayCollapsedUntilAdvancedIsExpanded()
    {
        // S3 这类协议一口气声明十来个字段,全铺开会把连接对话框顶出屏幕。
        // 标了 IsAdvanced 的调优项默认收进「高级选项」,页脚报出被收走的数量。
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new() { Key = "region", Label = "区域" }, null));
        vm.PluginFields.Add(new(new() { Key = "partSize", Label = "分片大小", IsAdvanced = true }, null));
        vm.PluginFields.Add(new(new() { Key = "concurrency", Label = "并发分片数", IsAdvanced = true }, null));

        Assert.IsTrue(vm.PluginFields[0].IsRowVisible, "连得上连不上取决于它的字段必须一直可见。");
        Assert.IsFalse(vm.PluginFields[1].IsRowVisible);
        Assert.IsFalse(vm.PluginFields[2].IsRowVisible);
        Assert.IsTrue(vm.HasAdvancedBadge);
        Assert.AreEqual("+2", vm.AdvancedBadge);

        vm.ToggleAdvancedCommand.Execute().Subscribe();
        Assert.IsTrue(vm.PluginFields.All(field => field.IsRowVisible));
        // 展开后徽标要消失:字段都在眼前了,再报"+2"就是误导。
        Assert.IsFalse(vm.HasAdvancedBadge);
        Assert.AreEqual(string.Empty, vm.AdvancedBadge);

        vm.ToggleAdvancedCommand.Execute().Subscribe();
        Assert.HasCount(2, vm.PluginFields.Where(field => !field.IsRowVisible));
    }

    /// <summary>没有协议级凭据的终端协议(Telnet)替身:只用来把描述符送进视图模型。</summary>
    private sealed class NoCredentialTerminal : VelaShell.PluginSdk.Protocols.IProtocolTerminal
    {
        public Task<VelaShell.PluginSdk.Protocols.IProtocolTerminalSession> ConnectAsync(
            VelaShell.PluginSdk.Protocols.ProtocolConnectRequest request,
            VelaShell.PluginSdk.Protocols.ProtocolTerminalOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class VariantWorkspace : VelaShell.PluginSdk.Workspaces.IWorkspaceProvider
    {
        public Task<VelaShell.PluginSdk.Workspaces.IWorkspaceDocument> OpenAsync(
            VelaShell.PluginSdk.Workspaces.WorkspaceConnectRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task PluginProtocol_WithoutCredentials_HidesUsernameAndPassword_AndStillSaves()
    {
        // Telnet 的登录发生在带内(对端打印 login:)。摆着两个填了也发不出去的框会误导用户,
        // 而"用户名不能为空"这条更会把保存/连接按钮永久灰死 —— 无凭据协议必须两条都免掉。
        var registry = new VelaShell.Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        using IDisposable handle = registry.Register("test.telnet", new()
        {
            Id = "test.telnet",
            DisplayName = "Telnet",
            DefaultPort = 23,
            Features = VelaShell.PluginSdk.Protocols.ProtocolFeatures.NoCredentials
        }, new NoCredentialTerminal());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute("test.telnet").FirstAsync();

        Assert.IsFalse(vm.ShowCredentialFields, "无凭据协议不该显示用户名一行。");
        Assert.IsFalse(vm.ShowPasswordField, "同上,口令与「记住密码」也一并收起。");
        Assert.IsTrue(vm.AllowsAnonymous, "NoCredentials 蕴含匿名,否则按钮永远灰着。");
        Assert.AreEqual(23, vm.Port, "端口应跟随协议默认值。");

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile, "用户名为空也必须存得下去。");
        Assert.AreEqual(ConnectionType.Plugin, profile.ConnectionType);
        Assert.AreEqual("test.telnet", profile.PluginProtocolId);
    }

    /// <summary>
    /// <b>连接框本身跟着"类型"那一栏变形</b> —— 端口、"主机"那一栏的含义、要不要凭据。
    /// <para>
    /// 它要解决的是"一个插件想用一个页签承载一族相近连接类型"时的那个尴尬:
    /// 数据库插件原先为五个方言开了五个页签,而那五个页签除了默认端口几乎一模一样。
    /// 收成一个之后,这三样**是随方言变的**东西没有地方安放 ——
    /// 于是有了变体:用户选了 PostgreSQL,端口就该是 5432 而不是 MySQL 的 3306。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_Variant_ReshapesTheConnectionBox()
    {
        var registry = new VelaShell.Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        using IDisposable handle = registry.RegisterWorkspace("test.db", new()
        {
            Id = "test.db",
            DisplayName = "数据库",
            DefaultPort = 3306,
            Features = VelaShell.PluginSdk.Workspaces.WorkspaceFeatures.CertificateTrust,
            Fields =
            [
                new()
                {
                    Key = "dialect",
                    Label = "数据库类型",
                    Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL"), new("sqlite", "SQLite")]
                }
            ],
            VariantKey = "dialect",
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 },
                new()
                {
                    Value = "sqlite",
                    DefaultPort = 1,
                    HostLabel = "数据库文件",
                    Features = VelaShell.PluginSdk.Workspaces.WorkspaceFeatures.NoCredentials
                }
            ]
        }, new VariantWorkspace());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute("test.db").FirstAsync();

        Assert.AreEqual(3306, vm.Port, "默认方言那一档的端口。");
        Assert.IsTrue(vm.ShowCredentialFields, "MySQL 是要凭据的。");

        PluginProtocolFieldViewModel dialect = vm.PluginFields.Single(f => f.Key == "dialect");

        dialect.Text = "postgresql";
        Assert.AreEqual(5432, vm.Port, "换方言之后端口要跟着走 —— 否则选了 PG 却连去 MySQL 的端口。");

        dialect.Text = "sqlite";
        Assert.AreEqual("数据库文件", vm.HostLabel, "SQLite 的\"主机\"那一栏装的是文件路径。");
        Assert.IsFalse(vm.ShowCredentialFields, "SQLite 就是个文件,填了用户名也没地方会用到。");
        Assert.IsTrue(vm.AllowsAnonymous, "NoCredentials 蕴含匿名,否则按钮永远灰着。");

        // 切回去要能**完全**恢复:变体每次都从原始描述重新套,而不是层层叠加。
        dialect.Text = "mysql";
        Assert.AreEqual(3306, vm.Port);
        Assert.IsTrue(vm.ShowCredentialFields);
        Assert.AreNotEqual("数据库文件", vm.HostLabel, "上一个变体的标签粘住了 —— 变体是覆盖,不是累加。");
    }

    /// <summary>
    /// <b>用户自己填过的端口不会被变体盖掉。</b>
    /// <para>
    /// 与切换页签用的是同一条判定。反过来做的话,用户把端口改成 13306(容器映射)、
    /// 顺手把方言从 MySQL 换成 MySQL 之外再换回来,就会发现自己填的端口没了。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_Variant_DoesNotOverwriteAHandTypedPort()
    {
        var registry = new VelaShell.Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        using IDisposable handle = registry.RegisterWorkspace("test.db2", new()
        {
            Id = "test.db2",
            DisplayName = "数据库",
            DefaultPort = 3306,
            Fields =
            [
                new()
                {
                    Key = "dialect",
                    Label = "类型",
                    Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL")]
                }
            ],
            VariantKey = "dialect",
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 }
            ]
        }, new VariantWorkspace());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute("test.db2").FirstAsync();

        vm.Port = 13306;
        vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";

        Assert.AreEqual(13306, vm.Port, "用户手填的端口被变体盖掉了。");
    }

    /// <summary>
    /// <b>重复点当前插件页签,不能把已经跟着变体走的端口悄悄拉回页签默认值。</b>
    /// <para>
    /// 端口跟随发生在"重复点当前页签"那条早退**之前**,而
    /// <c>IsProtocolDefaultPort</c> 现在把**变体端口**也算作"用户没手填过" ——
    /// 于是选了 PostgreSQL(5432)之后再点一次同一个页签,5432 会被当成默认值
    /// 改写成页签的 DefaultPort(3306),然后从早退处返回、不再套用变体。
    /// 结果是下拉仍写着 PostgreSQL,端口框已经是 3306 —— 两处自相矛盾,而且没有任何提示。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_ReselectingTheSameTab_KeepsTheVariantPort()
    {
        var registry = new VelaShell.Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        using IDisposable handle = registry.RegisterWorkspace("test.db3", new()
        {
            Id = "test.db3",
            DisplayName = "数据库",
            DefaultPort = 3306,
            Fields =
            [
                new()
                {
                    Key = "dialect",
                    Label = "类型",
                    Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL")]
                }
            ],
            VariantKey = "dialect",
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 }
            ]
        }, new VariantWorkspace());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute("test.db3").FirstAsync();
        vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";
        Assert.AreEqual(5432, vm.Port, "前置条件:变体端口已经跟上来了。");

        // 再点一次**同一个**页签。
        await vm.SelectPluginProtocolCommand.Execute("test.db3").FirstAsync();

        Assert.AreEqual(5432, vm.Port, "重复点当前页签把变体端口拉回了页签默认值。");
        Assert.AreEqual(
            "postgresql",
            vm.PluginFields.Single(f => f.Key == "dialect").Text,
            "方言下拉不该被动过 —— 它与端口必须一直是一致的。");
    }

    /// <summary>
    /// <b>从插件变体切回内建协议时,变体端口要跟着还原。</b>
    /// <para>
    /// 判定用的 <c>IsProtocolDefaultPort</c> 要靠 <c>_pluginBaseForm</c> 才认得出变体端口,
    /// 而切回内建协议那一支会先把它置空 —— 等到方法末尾再判,5432 已经取不到变体信息、
    /// 会被当成"用户手填的"留下来。于是新建的 SSH 配置停在 5432 上,连不上而且看不出为什么。
    /// </para>
    /// <para>这正是 <c>IsProtocolDefaultPort</c> 上那段注释("从 S3 切回 SSH 时端口停在 443")
    /// 要消灭的情形,被变体端口重新引了回来。</para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_SwitchingBackToSsh_RestoresTheDefaultPort()
    {
        var registry = new VelaShell.Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        using IDisposable handle = registry.RegisterWorkspace("test.db4", new()
        {
            Id = "test.db4",
            DisplayName = "数据库",
            DefaultPort = 3306,
            Fields =
            [
                new()
                {
                    Key = "dialect",
                    Label = "类型",
                    Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("postgresql", "PostgreSQL")]
                }
            ],
            VariantKey = "dialect",
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new() { Value = "postgresql", DefaultPort = 5432 }
            ]
        }, new VariantWorkspace());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "10.0.0.9" };
        await vm.SelectPluginProtocolCommand.Execute("test.db4").FirstAsync();
        vm.PluginFields.Single(f => f.Key == "dialect").Text = "postgresql";
        Assert.AreEqual(5432, vm.Port, "前置条件:变体端口已经跟上来了。");

        await vm.SelectConnectionTypeCommand.Execute(Core.Models.ConnectionType.SSH).FirstAsync();

        Assert.AreEqual(22, vm.Port, "切回 SSH 之后端口停在了 5432 上。");
    }

    /// <summary>
    /// 声明了显示条件的字段只在条件成立时出现,并且**改一下就跟着变** ——
    /// 这正是它要解决的问题:Redis 的「主节点名」只有哨兵模式有意义,
    /// 原先它在独立形态下照样显示,靠字段下面一行小字"仅哨兵模式"解释。
    /// </summary>
    [TestMethod]
    public void PluginFields_VisibleWhen_FollowsTheFieldItDependsOn()
    {
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new()
        {
            Key = "mode",
            Label = "部署形态",
            Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
            DefaultValue = "standalone",
            Choices = [new("standalone", "独立"), new("sentinel", "哨兵"), new("cluster", "集群")]
        }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "masterName",
            Label = "主节点名",
            VisibleWhen = new("mode", "sentinel")
        }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "database",
            Label = "默认数据库",
            VisibleWhen = new("mode", ["standalone", "sentinel"])
        }, null));

        PluginProtocolFieldViewModel master = vm.PluginFields[1];
        PluginProtocolFieldViewModel database = vm.PluginFields[2];

        Assert.IsFalse(master.IsRowVisible, "独立形态下不该出现哨兵专用的主节点名。");
        Assert.IsTrue(database.IsRowVisible, "独立形态有多数据库。");

        vm.PluginFields[0].Text = "sentinel";
        Assert.IsTrue(master.IsRowVisible, "切到哨兵后主节点名要出现。");
        Assert.IsTrue(database.IsRowVisible);

        vm.PluginFields[0].Text = "cluster";
        Assert.IsFalse(master.IsRowVisible);
        Assert.IsFalse(database.IsRowVisible, "集群只有 db0,数据库那格不该出现。");
    }

    /// <summary>
    /// 条件不成立时字段只是**看不见**,值照常保留并落盘 —— 与 IsHidden 同一套存取语义。
    /// 反过来做的话,用户在哨兵下填了主节点名、切去独立瞄一眼再切回来,
    /// 会发现自己填的东西被界面顺手清掉了。
    /// </summary>
    [TestMethod]
    public async Task PluginFields_HiddenByCondition_KeepTheirValues()
    {
        var vm = new ConnectionProfileViewModel { Host = "127.0.0.1", Username = "default" };
        await vm.SelectPluginProtocolCommand.Execute("velashell.s3").FirstAsync();
        vm.PluginFields.Add(new(new()
        {
            Key = "mode",
            Label = "部署形态",
            DefaultValue = "sentinel"
        }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "masterName",
            Label = "主节点名",
            VisibleWhen = new("mode", "sentinel")
        }, "mymaster"));

        Assert.IsTrue(vm.PluginFields[^1].IsRowVisible);
        vm.PluginFields[^2].Text = "standalone";
        Assert.IsFalse(vm.PluginFields[^1].IsRowVisible, "条件不再成立,字段应从表单上消失。");

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.IsNotNull(profile.PluginSettings);
        Assert.AreEqual("mymaster", profile.PluginSettings["masterName"],
            "看不见 ≠ 被清掉:值必须原样带回。");
    }

    /// <summary>「高级选项」展开也不该把当前不适用的字段翻出来 —— 两个条件是与关系。</summary>
    [TestMethod]
    public void PluginFields_VisibleWhen_BeatsAdvancedExpansion()
    {
        var vm = new ConnectionProfileViewModel();
        vm.PluginFields.Add(new(new() { Key = "mode", Label = "形态", DefaultValue = "standalone" }, null));
        vm.PluginFields.Add(new(new()
        {
            Key = "sentinelTuning",
            Label = "哨兵调优",
            IsAdvanced = true,
            VisibleWhen = new("mode", "sentinel")
        }, null));

        vm.ToggleAdvancedCommand.Execute().Subscribe();
        Assert.IsTrue(vm.IsAdvancedVisible);
        Assert.IsFalse(vm.PluginFields[1].IsRowVisible, "不适用的字段,展开高级选项也不该出现。");

        vm.PluginFields[0].Text = "sentinel";
        Assert.IsTrue(vm.PluginFields[1].IsRowVisible);
    }

    [TestMethod]
    public async Task PluginFields_CollapsedAdvancedValues_StillGetSaved()
    {
        // 折叠只是显示层面的事:落盘必须把高级字段一并带上,
        // 否则用户填过一次分片大小、收起「高级选项」再保存,值就静默丢了。
        var vm = new ConnectionProfileViewModel
        {
            Host = "s3.example.com",
            Username = "AKIA",
        };
        await vm.SelectPluginProtocolCommand.Execute("velashell.s3").FirstAsync();
        vm.PluginFields.Add(new(new() { Key = "partSize", Label = "分片大小", IsAdvanced = true }, "16777216"));
        Assert.IsFalse(vm.PluginFields[0].IsRowVisible, "默认折叠。");

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.AreEqual(ConnectionType.Plugin, profile.ConnectionType);
        Assert.IsNotNull(profile.PluginSettings);
        Assert.AreEqual("16777216", profile.PluginSettings["partSize"]);
    }

    [TestMethod]
    public async Task SaveCommand_MaterializesSecurePasswordIntoProfile()
    {
        // 无 workflow 时 SaveCommand 直接返回 BuildProfile 的结果,便于校验 SecureString → 明文交接。
        var vm = new ConnectionProfileViewModel
        {
            Name = "prod",
            Host = "h",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = SecureStringConvert.FromPlaintext("s3cret")
        };
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.AreEqual("s3cret", profile.Password);
    }

    [TestMethod]
    public async Task NewProfile_DefaultsToSsh_AndCanSaveSftp()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            Password = SecureStringConvert.FromPlaintext("secret"),
        };

        Assert.AreEqual(ConnectionType.SSH, vm.ConnectionType);
        vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();

        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(profile);
        Assert.AreEqual(ConnectionType.SFTP, profile.ConnectionType);
        Assert.IsTrue(vm.IsSftpSelected);
        Assert.IsFalse(vm.IsSshSelected);
    }

    [TestMethod]
    public void EditProfile_ReopensSelectedProtocol()
    {
        var existing = new SessionProfile
        {
            ConnectionType = ConnectionType.SFTP,
            Host = "files.example.com",
            Username = "root",
        };

        var vm = new ConnectionProfileViewModel(existing);

        Assert.AreEqual(ConnectionType.SFTP, vm.ConnectionType);
        Assert.IsTrue(vm.IsSftpSelected);
        Assert.IsFalse(vm.IsSshSelected);
    }

    [TestMethod]
    public async Task SaveCommand_UsesWorkflowServiceAndReturnsSavedProfile()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        var expected = new SessionProfile
        {
            Name = "prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        workflow.SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(expected);
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        SessionProfile? result = await vm.SaveCommand.Execute().FirstAsync();
        Assert.AreSame(expected, result);
        await workflow.Received(1).SaveProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task TestConnectionCommand_StoresSuccessState()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(true));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.LastTestSucceeded);
        Assert.IsNull(vm.ErrorMessage);
    }

    [TestMethod]
    public async Task CopyErrorCommand_CopiesFailureText_AndAcknowledgesWithCopiedState()
    {
        // 连接失败的原文(认证链、主机密钥指纹、栈顶异常)常常一长串,
        // 用户要拿去搜索或贴给同事 —— 照着屏幕抄不现实,复制按钮就是为它准备的。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(false, "Permission denied (publickey,password)."));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        string? copied = null;
        vm.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.HasError, "失败之后必须有可复制的错误信息,按钮才出得来。");
        Assert.IsFalse(vm.ErrorCopied);

        await vm.CopyErrorCommand.Execute().FirstAsync();
        Assert.AreEqual("Permission denied (publickey,password).", copied);
        // 没有这句回执就分不清"复制成功了"和"按钮没反应",用户只会再点两下。
        Assert.IsTrue(vm.ErrorCopied);
    }

    [TestMethod]
    public async Task CopyErrorCommand_IsUnavailable_WhenThereIsNoError()
    {
        // 成功那一条没有可复制的内容:命令必须自己灰掉,而不是复制一个空串。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(true));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);

        Assert.IsFalse(await vm.CopyErrorCommand.CanExecute.FirstAsync());
        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.HasError);
        Assert.IsFalse(await vm.CopyErrorCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task NewError_ClearsPreviousCopiedAcknowledgement()
    {
        // 「已复制」是对某一段具体文本说的:换了一条错误还顶着旧回执,
        // 用户会以为新的这条也已经在剪贴板里了。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns(new ConnectionTestResult(false, "第一条"), new ConnectionTestResult(false, "第二条"));
        ConnectionProfileViewModel vm = CreateValidViewModel(workflow);
        vm.CopyToClipboard = _ => Task.CompletedTask;

        await vm.TestConnectionCommand.Execute().FirstAsync();
        await vm.CopyErrorCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.ErrorCopied);

        await vm.TestConnectionCommand.Execute().FirstAsync();
        Assert.AreEqual("第二条", vm.ErrorMessage);
        Assert.IsFalse(vm.ErrorCopied);
    }

    [TestMethod]
    public async Task SaveCommand_CreatesNewGroup_WhenGroupTextIsUnknown()
    {
        // 分组框输入了不存在的分组名:保存时应新建分组落库,并把配置归入该组。
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "生产环境";
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        await repository.Received(1).SaveGroupAsync(Arg.Is<ServerGroup>(g => g.Name == "生产环境"));
        Assert.IsNotNull(profile.GroupId);
        Assert.Contains(option => option.Name == "生产环境" && option.Id == profile.GroupId, vm.Groups);
    }

    [TestMethod]
    public async Task SaveCommand_ReusesExistingGroup_WhenGroupTextMatches()
    {
        var existing = new ServerGroup { Name = "生产环境" };
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup> { existing }));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "生产环境";
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.AreEqual(existing.Id, profile.GroupId);
        await repository.DidNotReceive().SaveGroupAsync(Arg.Any<ServerGroup>());
    }

    [TestMethod]
    public async Task SaveCommand_EmptyOrUngroupedText_SavesAsUngrouped()
    {
        ISessionRepository? repository = Substitute.For<ISessionRepository>();
        repository.GetAllGroupsAsync().Returns(Task.FromResult(new List<ServerGroup>()));
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var vm = new ConnectionProfileViewModel(sessionRepository: repository)
        {
            Host = "h",
            Port = 22,
            Username = "root"
        };
        await vm.LoadGroupsAsync();
        vm.GroupText = "  ";
        SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(profile);
        Assert.IsNull(profile.GroupId);
        await repository.DidNotReceive().SaveGroupAsync(Arg.Any<ServerGroup>());
    }

    /// <summary>
    /// <b>没有端点的那一档,端口那一栏要收起来 —— 而且切回去还得出现。</b>
    /// <para>
    /// 实拍过的现象:选 SQLite 之后主机那一栏已经改标成"数据库文件"、凭据两栏也收了,
    /// 唯独端口框还摆着上一个方言留下的 55432。SQLite 是磁盘上的一个文件,
    /// 那一栏填什么都不会被拼进连接串,留着只会让用户以为它有意义。
    /// </para>
    /// <para>
    /// 顺带守住两条边界:主机那一栏<b>不能</b>跟着收(文件路径正是填在它里面),
    /// 以及收起端口之后保存/连接按钮不能因此灰掉 —— 端口的**取值**照旧在 1–65535 内。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task PluginWorkspace_VariantWithoutEndpoint_HidesThePortBox()
    {
        var registry = new VelaShell.Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        using IDisposable handle = registry.RegisterWorkspace("test.db3", new()
        {
            Id = "test.db3",
            DisplayName = "数据库",
            DefaultPort = 3306,
            Fields =
            [
                new()
                {
                    Key = "dialect",
                    Label = "数据库类型",
                    Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
                    DefaultValue = "mysql",
                    Choices = [new("mysql", "MySQL"), new("sqlite", "SQLite")]
                }
            ],
            VariantKey = "dialect",
            Variants =
            [
                new() { Value = "mysql", DefaultPort = 3306 },
                new()
                {
                    Value = "sqlite",
                    DefaultPort = 1,
                    HostLabel = "数据库文件",
                    Features = VelaShell.PluginSdk.Workspaces.WorkspaceFeatures.NoCredentials
                        | VelaShell.PluginSdk.Workspaces.WorkspaceFeatures.NoEndpoint
                }
            ]
        }, new VariantWorkspace());

        var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = @"D:\data\app.db" };
        await vm.SelectPluginProtocolCommand.Execute("test.db3").FirstAsync();

        Assert.IsTrue(vm.ShowPortField, "MySQL 是有端点的,端口那一栏必须在。");

        PluginProtocolFieldViewModel dialect = vm.PluginFields.Single(f => f.Key == "dialect");

        // ShowPortField 是纯计算属性:不补发通知的话,值变了而界面上那一栏纹丝不动 ——
        // 换方言看不出问题,得关掉对话框重开才刷新。所以这里连通知一起验。
        List<string?> changed = [];
        ((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dialect.Text = "sqlite";
        Assert.IsFalse(vm.ShowPortField, "SQLite 没有端点,端口那一栏还留着上一个方言的残值。");
        Assert.IsTrue(changed.Contains(nameof(vm.ShowPortField)),
            "换变体时没给 ShowPortField 补发通知,绑在它上面的那一栏不会跟着刷新。");
        Assert.AreEqual("数据库文件", vm.HostLabel, "主机那一栏不能跟着收 —— 文件路径就填在它里面。");
        Assert.IsTrue(await vm.SaveCommand.CanExecute.FirstAsync(),
            "收起一栏不该顺手把保存按钮堵死:端口的取值仍在合法区间内。");

        // 切回去要完全恢复:变体的能力位是整体替换,不该有"收起来就再也不出现"。
        dialect.Text = "mysql";
        Assert.IsTrue(vm.ShowPortField, "换回有端点的方言,端口那一栏没回来。");
    }

    private static ConnectionProfileViewModel CreateValidViewModel(IConnectionWorkflowService workflow)
    {
        return new(connectionWorkflowService: workflow)
        {
            Name = "prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = SecureStringConvert.FromPlaintext("secret")
        };
    }
}
