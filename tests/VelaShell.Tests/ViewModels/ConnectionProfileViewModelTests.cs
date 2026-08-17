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
