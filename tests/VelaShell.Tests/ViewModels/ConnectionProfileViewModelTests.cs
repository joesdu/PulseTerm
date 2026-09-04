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

    /// <summary>
    /// 「认证后执行命令」是每条配置各一份的:打开一条已存的配置要回显它自己的那条,
    /// 保存要原样带回去。回显丢字段的表现是"改个端口顺手把命令清空了",而且不报错。
    /// </summary>
    [TestMethod]
    public async Task PostAuthCommand_RoundTripsThroughTheEditDialog()
    {
        var existing = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
        };

        var vm = new ConnectionProfileViewModel(existing);

        Assert.IsTrue(vm.SupportsPostAuthCommand, "SSH 才有 shell 通道,这一栏也只对它出现。");
        Assert.AreEqual("sudo su -", vm.PostAuthCommand);
        Assert.AreEqual(3, vm.PostAuthCommandDelaySeconds);
        Assert.IsTrue(vm.IsAdvancedVisible, "填过的命令不能藏在折叠区里,否则用户会当成配置丢了。");

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.AreEqual("sudo su -", saved.PostAuthCommand);
        Assert.AreEqual(3, saved.PostAuthCommandDelaySeconds);
    }

    /// <summary>只有空格的命令等于没配:存下去会让 <c>SendSilentCommand</c> 每次连接多敲一个回车。</summary>
    [TestMethod]
    public async Task PostAuthCommand_WhenBlank_IsSavedAsNull()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", PostAuthCommand = "   " };

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.IsNull(saved.PostAuthCommand);
    }

    [TestMethod]
    public void PostAuthCommandDelay_IsClampedToTheSupportedRange()
    {
        var vm = new ConnectionProfileViewModel { PostAuthCommandDelaySeconds = 9999 };
        Assert.AreEqual(SessionProfile.MaxPostAuthCommandDelaySeconds, vm.PostAuthCommandDelaySeconds);

        vm.PostAuthCommandDelaySeconds = -1;
        Assert.AreEqual(0, vm.PostAuthCommandDelaySeconds, "0 是合法值:不等,握手完立刻发。");
    }

    /// <summary>
    /// 换到没有终端的协议(SFTP / FTP / 对象存储)时这一栏收起,存下去的也必须是 null。
    /// 留着的话它就是一条永远不执行的命令,而且切回 SSH 时会诈尸执行一次。
    /// </summary>
    [TestMethod]
    public async Task PostAuthCommand_OnProtocolsWithoutAShell_IsHiddenAndNotSaved()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", PostAuthCommand = "tmux attach" };

        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).FirstAsync();

        Assert.IsFalse(vm.SupportsPostAuthCommand);
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.IsNull(saved.PostAuthCommand);
    }

    /// <summary>
    /// FTP / FTPS 的「默认打开路径」:保存时经 <c>FtpSettings</c> 的 setter 归一化,
    /// 重新打开配置要回显,且不能藏在折叠的「高级选项」里(否则用户会当成配置丢了)。
    /// </summary>
    [TestMethod]
    public async Task FtpInitialRemotePath_IsNormalizedOnSave_AndRestoredOnEdit()
    {
        var vm = new ConnectionProfileViewModel { Host = "ftp.example.com", Username = "deploy" };
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).FirstAsync();
        vm.FtpInitialRemotePath = @"  \var\www\html\  ";

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual("/var/www/html", saved.Ftp?.InitialRemotePath);

        var reopened = new ConnectionProfileViewModel(saved);
        Assert.AreEqual("/var/www/html", reopened.FtpInitialRemotePath);
        Assert.IsTrue(reopened.IsAdvancedVisible, "填过的路径不能藏在折叠区里。");
    }

    /// <summary>这块设置只属于 FTP;换成别的协议时整个 <c>Ftp</c> 块不落盘,路径自然一起走。</summary>
    [TestMethod]
    public async Task FtpInitialRemotePath_IsNotSavedOnNonFtpProtocols()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u" };
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).FirstAsync();
        vm.FtpInitialRemotePath = "/pub";
        await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.IsNull(saved.Ftp);
    }

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
    private sealed class NoCredentialTerminal : PluginSdk.Protocols.IProtocolTerminal
    {
        public Task<PluginSdk.Protocols.IProtocolTerminalSession> ConnectAsync(
            PluginSdk.Protocols.ProtocolConnectRequest request,
            PluginSdk.Protocols.ProtocolTerminalOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task PluginProtocol_WithoutCredentials_HidesUsernameAndPassword_AndStillSaves()
    {
        // Telnet 的登录发生在带内(对端打印 login:)。摆着两个填了也发不出去的框会误导用户,
        // 而"用户名不能为空"这条更会把保存/连接按钮永久灰死 —— 无凭据协议必须两条都免掉。
        var registry = new Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
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

    /// <summary>没有网络端点的终端协议(串口)替身,兼动态候选项来源。</summary>
    private sealed class FakeSerialTerminal(params string[] ports)
        : PluginSdk.Protocols.IProtocolTerminal, PluginSdk.Protocols.IProtocolChoiceSource
    {
        /// <summary>被问过几次候选项(用来验证刷新真的重取,而不是拿缓存糊弄)。</summary>
        public int Queries { get; private set; }

        /// <summary>下一次要给出的端口(模拟热插拔)。</summary>
        public string[] Ports { get; set; } = ports;

        public Task<PluginSdk.Protocols.IProtocolTerminalSession> ConnectAsync(
            PluginSdk.Protocols.ProtocolConnectRequest request,
            PluginSdk.Protocols.ProtocolTerminalOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginSdk.Protocols.ProtocolSettingChoice>> GetChoicesAsync(
            string fieldKey, CancellationToken cancellationToken = default)
        {
            Queries++;
            return Task.FromResult<IReadOnlyList<PluginSdk.Protocols.ProtocolSettingChoice>>(
                fieldKey == PluginSdk.Protocols.ProtocolDescriptor.HostFieldKey
                    ? [.. Ports.Select(port => new PluginSdk.Protocols.ProtocolSettingChoice(port, $"Fake ({port})"))]
                    : []);
        }
    }

    private static Infrastructure.Plugins.Protocols.PluginProtocolRegistry RegisterSerial(
        FakeSerialTerminal terminal, out IDisposable handle)
    {
        var registry = new Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        handle = registry.Register("test.serial", new()
        {
            Id = "test.serial",
            DisplayName = "串口",
            DefaultPort = 22,
            HostLabel = "串口设备",
            HostKind = PluginSdk.Protocols.ProtocolSettingKind.DynamicChoice,
            HostAllowsCustomValue = true,
            Features = PluginSdk.Protocols.ProtocolFeatures.NoEndpoint
                       | PluginSdk.Protocols.ProtocolFeatures.NoCredentials
        }, terminal);
        return registry;
    }

    [TestMethod]
    public async Task PluginProtocol_WithoutAnEndpoint_HidesThePortColumnButKeepsTheButtonsUsable()
    {
        // 串口的目标不是 host:port:端口那一栏填什么都不会被用上,摆着只会让用户以为它有意义,
        // 而且还留着上一个协议的残值。但它的**取值**必须照旧参与按钮判定 ——
        // 收起一栏不该顺手把保存/连接按钮堵死。
        var terminal = new FakeSerialTerminal("COM3");
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };

            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            Assert.IsFalse(vm.ShowPortField);
            Assert.IsTrue(vm.Port is >= 1 and <= 65535, "端口仍是个合法值,只是不显示。");
            SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
            Assert.IsNotNull(profile, "没有端口栏,也必须存得下去。");
            Assert.AreEqual("COM3", profile.Host, "设备名走的正是「主机」那一栏。");
        }
    }

    [TestMethod]
    public async Task PluginProtocol_WithADynamicHostColumn_FetchesTheChoicesWhenTheFormOpens()
    {
        var terminal = new FakeSerialTerminal("COM3", "COM7");
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };

            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            Assert.IsTrue(vm.HostIsEditableChoice, "可手输:枚举不到的设备也必须填得进去。");
            Assert.IsTrue(vm.HostIsDynamicChoice, "动态:USB 转串口是热插拔的,得给刷新按钮。");
            Assert.IsFalse(vm.HostIsText);
            Assert.AreSequenceEqual(["COM3", "COM7"], [.. vm.HostChoices.Select(choice => choice.Value)]);
            Assert.AreEqual("串口设备", vm.HostLabel);
        }
    }

    [TestMethod]
    public async Task RefreshHostChoices_PicksUpADeviceThatWasPluggedInAfterTheDialogOpened()
    {
        // 这个按钮存在的全部理由:用户很可能是先打开连接对话框、才想起去插线。
        var terminal = new FakeSerialTerminal("COM3");
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };
            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();
            terminal.Ports = ["COM3", "COM9"];

            await vm.RefreshHostChoicesCommand.Execute().FirstAsync();

            Assert.AreSequenceEqual(["COM3", "COM9"], [.. vm.HostChoices.Select(choice => choice.Value)]);
            Assert.AreEqual(2, terminal.Queries, "刷新必须是真的重取,而不是拿缓存糊弄。");
        }
    }

    [TestMethod]
    public async Task DynamicHostColumn_KeepsAStoredDeviceThatIsNotPluggedInRightNow()
    {
        // 一条存着 COM7 的配置在适配器没插的时候打开,值被悄悄改写成 COM3 再保存下去,
        // 是一次**静默的数据损坏** —— 用户下次插上线才发现配置指到别处去了。
        var terminal = new FakeSerialTerminal("COM3");
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM7" };

            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            Assert.AreEqual("COM7", vm.Host);
            Assert.IsNull(vm.SelectedHostChoice, "对不上候选项就是没有选中项,当前值由文本框自己显示。");
            SessionProfile? profile = await vm.SaveCommand.Execute().FirstAsync();
            Assert.AreEqual("COM7", profile!.Host);
        }
    }

    [TestMethod]
    public async Task SwitchingBackToSsh_RestoresThePlainHostTextBoxAndThePortColumn()
    {
        var terminal = new FakeSerialTerminal("COM3");
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry registry = RegisterSerial(terminal, out IDisposable handle);
        using (handle)
        {
            var vm = new ConnectionProfileViewModel(protocolRegistry: registry) { Host = "COM3" };
            await vm.SelectPluginProtocolCommand.Execute("test.serial").FirstAsync();

            await vm.SelectConnectionTypeCommand.Execute(ConnectionType.SSH).FirstAsync();

            Assert.IsTrue(vm.ShowPortField);
            Assert.IsTrue(vm.HostIsText, "切回 SSH 后主机那格必须是普通文本框,不能还挂着串口下拉。");
            Assert.IsEmpty(vm.HostChoices);
        }
    }

    [TestMethod]
    public void EditableChoiceField_DoesNotRewriteAValueThatIsNotInTheList()
    {
        // 波特率:表里给九个常用值,但 250000(Marlin 固件)得填得进去 ——
        // 把表当白名单等于告诉这些用户"本工具不支持你的设备"。
        var field = new PluginProtocolFieldViewModel(new()
        {
            Key = "baudRate",
            Label = "波特率",
            Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
            AllowsCustomValue = true,
            DefaultValue = "115200",
            Choices = [new("9600", "9600"), new("115200", "115200")]
        }, "250000");

        Assert.AreEqual("250000", field.Text);
        Assert.IsTrue(field.IsEditableChoice);
        Assert.IsFalse(field.IsChoice);
        Assert.IsNull(field.SelectedChoice);
    }

    [TestMethod]
    public void ReadOnlyChoiceField_StillNormalisesAValueThatIsNotInTheList()
    {
        // 封闭枚举的老行为不变:值对不上就归一到第一项,免得下拉空着没法解释。
        var field = new PluginProtocolFieldViewModel(new()
        {
            Key = "parity",
            Label = "校验位",
            Kind = PluginSdk.Protocols.ProtocolSettingKind.Choice,
            Choices = [new("none", "无"), new("even", "偶校验")]
        }, "whatever");

        Assert.AreEqual("none", field.Text);
    }

    [TestMethod]
    public void ChoiceItem_StringifiesToTheStoredValue() =>
        // 可编辑下拉在用户选中一项时,会拿 item.ToString() 去填那个文本框 ——
        // 而那就是接下来落盘的值。返回展示文案的话,存进配置的会是
        // "USB-SERIAL CH340 (COM3)" 这种打不开的东西。
        Assert.AreEqual("COM3", new PluginChoiceItem("COM3", "USB-SERIAL CH340 (COM3)").ToString());

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
