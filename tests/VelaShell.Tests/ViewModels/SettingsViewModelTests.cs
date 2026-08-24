using System.Runtime.InteropServices;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class SettingsViewModelTests
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;

    public SettingsViewModelTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _themeService = Substitute.For<IThemeService>();
    }

    private SettingsViewModel CreateVm(ISettingsPreviewService? previewService = null) =>
        new(_settingsService, _themeService, previewService: previewService);

    [TestMethod]
    [TestCategory("Settings")]
    public async Task AppearanceOpacityChange_PreviewsEveryValueImmediately_WithoutSnapshot()
    {
        ISettingsPreviewService preview = new SettingsPreviewService();
        var opacityValues = new List<int>();
        var snapshots = new List<AppSettings>();
        preview.WindowOpacityPreviewRequested += value => opacityValues.Add(value);
        preview.PreviewRequested += snapshot => snapshots.Add(snapshot);
        _settingsService.GetSettingsAsync().Returns(new AppSettings());

        SettingsViewModel vm = CreateVm(preview);
        await vm.LoadCommand.Execute().FirstAsync();

        foreach (int value in new[] { 20, 30, 40, 50 })
        {
            vm.Appearance.WindowOpacityPercent = value;
        }

        Assert.AreSequenceEqual([20, 30, 40, 50], opacityValues);
        Assert.IsEmpty(snapshots);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task OpacityOnlyPreview_NotifyClosed_BroadcastsOriginalBaseline()
    {
        ISettingsPreviewService preview = new SettingsPreviewService();
        var snapshots = new List<AppSettings>();
        preview.PreviewRequested += snapshot => snapshots.Add(snapshot);
        var baseline = new AppSettings { Appearance = new() { WindowOpacityPercent = 80 } };
        _settingsService.GetSettingsAsync().Returns(baseline);

        SettingsViewModel vm = CreateVm(preview);
        await vm.LoadCommand.Execute().FirstAsync();
        vm.Appearance.WindowOpacityPercent = 40;
        vm.NotifyClosed();

        Assert.HasCount(1, snapshots);
        Assert.AreEqual(80, snapshots[0].Appearance.WindowOpacityPercent);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task LoadCommand_LoadsSettingsFromService()
    {
        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "light",
            TerminalFont = "Fira Code",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222,
            TerminalType = "vt220",
            TerminalEncoding = "GBK",
            Appearance = new() { ShowQuickCommandsPanel = true },
        };
        _settingsService.GetSettingsAsync().Returns(settings);

        SettingsViewModel vm = CreateVm();
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.AreEqual("zh-CN", vm.Language);
        Assert.AreEqual("light", vm.Theme);
        Assert.AreEqual("Fira Code", vm.TerminalFont);
        Assert.AreEqual(16, vm.TerminalFontSize);
        Assert.AreEqual(5000, vm.ScrollbackLines);
        Assert.AreEqual(2222, vm.DefaultPort);
        Assert.AreEqual("vt220", vm.TerminalType);
        Assert.AreEqual("GBK", vm.TerminalEncoding);
        Assert.IsTrue(vm.Appearance.ShowQuickCommandsPanel);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_PersistsToService()
    {
        SettingsViewModel vm = CreateVm();
        vm.Language = "zh-CN";
        vm.Theme = "light";
        vm.TerminalFont = "Cascadia Code";
        vm.TerminalFontSize = 18;
        vm.ScrollbackLines = 20000;
        vm.DefaultPort = 8022;
        vm.TerminalType = "xterm-256color";
        vm.TerminalEncoding = "UTF-8";
        vm.Appearance.ShowQuickCommandsPanel = true;

        await vm.SaveCommand.Execute().FirstAsync();

        await _settingsService
            .Received(1)
            .SaveSettingsAsync(
                Arg.Is<AppSettings>(s =>
                    s.Language == "zh-CN"
                    && s.Theme == "light"
                    && s.TerminalFont == "Cascadia Code"
                    && s.TerminalFontSize == 18
                    && s.ScrollbackLines == 20000
                    && s.DefaultPort == 8022
                    && s.TerminalType == "xterm-256color"
                    && s.TerminalEncoding == "UTF-8"
                    && s.Appearance.ShowQuickCommandsPanel
                )
            );
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_AppliesTheme()
    {
        SettingsViewModel vm = CreateVm();
        vm.Theme = "light";

        await vm.SaveCommand.Execute().FirstAsync();

        _themeService.Received(1).SetTheme("light");
    }

    /// <summary>代理设置载入:分组对象直绑回填,类型下拉索引与可编辑标志同步刷新。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public async Task LoadCommand_PopulatesProxyOptionsAndDerivedIndex()
    {
        var settings = new AppSettings
        {
            Proxy = new()
            {
                Type = "socks5",
                Host = "proxy.example",
                Port = 1080,
                Username = "u",
                Password = "p",
                ProxyDns = false,
            },
        };
        _settingsService.GetSettingsAsync().Returns(settings);

        SettingsViewModel vm = CreateVm();
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.AreSame(settings.Proxy, vm.Proxy);
        Assert.AreEqual(3, vm.ProxyTypeIndex);
        Assert.IsTrue(vm.IsProxyEditable);
        Assert.IsFalse(vm.Proxy.ProxyDns);
    }

    /// <summary>代理设置保存:类型索引写回字符串,主机/端口/凭据/DNS 开关一并落盘。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_PersistsProxyOptions()
    {
        SettingsViewModel vm = CreateVm();
        vm.ProxyTypeIndex = 2; // http
        vm.Proxy.Host = "proxy.example";
        vm.Proxy.Port = 3128;
        vm.Proxy.Username = "user";
        vm.Proxy.Password = "secret";
        vm.Proxy.ProxyDns = false;

        await vm.SaveCommand.Execute().FirstAsync();

        await _settingsService
            .Received(1)
            .SaveSettingsAsync(
                Arg.Is<AppSettings>(s =>
                    s.Proxy.Type == "http"
                    && s.Proxy.Host == "proxy.example"
                    && s.Proxy.Port == 3128
                    && s.Proxy.Username == "user"
                    && s.Proxy.Password == "secret"
                    && !s.Proxy.ProxyDns
                )
            );
    }

    /// <summary>代理类型索引与内部字符串的双向映射;仅 http/socks5 开放参数编辑。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ProxyTypeIndex_MapsTypeStringAndGatesEditability()
    {
        SettingsViewModel vm = CreateVm();

        Assert.AreEqual(0, vm.ProxyTypeIndex);
        Assert.IsFalse(vm.IsProxyEditable);

        vm.ProxyTypeIndex = 1;
        Assert.AreEqual("system", vm.Proxy.Type);
        Assert.IsFalse(vm.IsProxyEditable);

        vm.ProxyTypeIndex = 2;
        Assert.AreEqual("http", vm.Proxy.Type);
        Assert.IsTrue(vm.IsProxyEditable);

        vm.ProxyTypeIndex = 3;
        Assert.AreEqual("socks5", vm.Proxy.Type);
        Assert.IsTrue(vm.IsProxyEditable);

        vm.Proxy.Type = "none";
        Assert.AreEqual(0, vm.ProxyTypeIndex);
        Assert.IsFalse(vm.IsProxyEditable);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_ValidatesRequiredFields()
    {
        var vm = new ConnectionProfileViewModel
        {
            // Host and Username empty → SaveCommand not executable
            Host = "",
            Username = "",
        };
        bool canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);

        Assert.IsFalse(canExecute);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_AuthMethodToggle_SwitchesVisibility()
    {
        var vm = new ConnectionProfileViewModel();

        // Default is Password
        Assert.IsTrue(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);

        vm.AuthMethod = AuthMethod.PrivateKey;

        Assert.IsFalse(vm.IsPasswordAuth);
        Assert.IsTrue(vm.IsKeyAuth);

        vm.AuthMethod = AuthMethod.Password;

        Assert.IsTrue(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_TrustPermanentlyCommand_SetsResult()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-ed25519",
            "SHA256:abc123def456",
            HostKeyVerification.Unknown
        );

        Assert.IsNull(vm.Result);

        vm.TrustPermanentlyCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.TrustPermanently, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_TrustOnceCommand_SetsResult()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-ed25519",
            "SHA256:abc123def456",
            HostKeyVerification.Unknown
        );

        vm.TrustOnceCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.TrustOnce, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_CancelCommand_SetsReject()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-rsa",
            "SHA256:xyz789",
            HostKeyVerification.Unknown
        );

        vm.CancelCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.Reject, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_ChangedKey_ShowsWarning()
    {
        var vmChanged = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:changed123",
            HostKeyVerification.Changed
        );

        Assert.IsTrue(vmChanged.IsChanged);

        var vmUnknown = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:unknown456",
            HostKeyVerification.Unknown
        );

        Assert.IsFalse(vmUnknown.IsChanged);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_PortValidation_AcceptsValidRange()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "test.example.com",
            Username = "admin",

            // Valid port
            Port = 22,
        };
        bool canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsTrue(canExecute);

        // Port 0 — invalid
        vm.Port = 0;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsFalse(canExecute);

        // Port 65535 — valid max
        vm.Port = 65535;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsTrue(canExecute);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_SaveCommand_ReturnsProfile()
    {
        var vm = new ConnectionProfileViewModel
        {
            Name = "My Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/home/user/.ssh/id_rsa",
        };

        SessionProfile? result = null;
        vm.SaveCommand.Execute().Subscribe(profile => result = profile);

        Assert.IsNotNull(result);
        Assert.AreEqual("My Server", result!.Name);
        Assert.AreEqual("192.168.1.100", result.Host);
        Assert.AreEqual(2222, result.Port);
        Assert.AreEqual("deploy", result.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, result.AuthMethod);
        Assert.AreEqual("/home/user/.ssh/id_rsa", result.PrivateKeyPath);
    }

    /// <summary>
    /// 关于页的架构必须报真实架构名。旧实现是 <c>Is64BitOperatingSystem ? "x64" : "x86"</c>,
    /// 只答"是不是 64 位"—— arm64 被显示成 x64、arm 被显示成 x86。
    /// 这里用纯函数逐架构断言,不必真有 ARM 机器才能守住。
    /// </summary>
    [TestMethod]
    [DataRow(Architecture.X64, "x64")]
    [DataRow(Architecture.X86, "x86")]
    [DataRow(Architecture.Arm64, "arm64")]
    [DataRow(Architecture.Arm, "arm")]
    public void DescribeArchitecture_NativeRun_ShowsRealArchitectureName(
        Architecture architecture,
        string expected
    ) => Assert.AreEqual(expected, SettingsViewModel.DescribeArchitecture(architecture, architecture));

    /// <summary>
    /// 进程架构与系统架构不一致(x64 版跑在 arm64 的仿真层上)时两者都要显示。
    /// 自更新按**进程**架构选产物,装错架构的人会一直留在仿真轨上,关于页得看得出来。
    /// </summary>
    [TestMethod]
    public void DescribeArchitecture_Emulated_ShowsBothProcessAndOsArchitecture()
    {
        string text = SettingsViewModel.DescribeArchitecture(Architecture.X64, Architecture.Arm64);

        Assert.IsTrue(text.Contains("x64", StringComparison.Ordinal), $"缺进程架构:{text}");
        Assert.IsTrue(text.Contains("arm64", StringComparison.Ordinal), $"缺系统架构:{text}");
    }

    /// <summary>关于页整行以当前架构收尾(拼接本身没写错)。</summary>
    [TestMethod]
    public void AboutOs_EndsWithCurrentArchitecture()
    {
        string architecture = SettingsViewModel.DescribeArchitecture(
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture
        );

        Assert.EndsWith($"({architecture})", SettingsViewModel.AboutOs);
    }

    // ———— 快捷键参考页的搜索 ————
    // 全部断言只用键位(F5、Ctrl+Shift+L)而不用动作名:动作名随界面语言变,
    // 键位不变,测试才不会因为跑测机器的语言不同而红。

    /// <summary>搜索一个只属于某一组的键位:其余分组整组消失,计数跟着收窄。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_KeepsOnlyMatchingRows()
    {
        SettingsViewModel vm = CreateVm();
        int total = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);

        vm.ShortcutFilter = "F5";

        Assert.HasCount(1, vm.FilteredShortcutGroups, "F5 只在进程管理器一组里出现");
        Assert.HasCount(1, vm.FilteredShortcutGroups[0].FilteredItems);
        Assert.IsTrue(vm.HasShortcutMatches);
        Assert.IsGreaterThan(1, total, "全量表不该只有一条,否则下面的收窄断言没意义");
    }

    /// <summary>键帽是分开的("Ctrl" "Shift" "L"),但用户会照文档敲加号写法,两种都要搜得到。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_MatchesBothSpacedAndPlusJoinedKeys()
    {
        SettingsViewModel vm = CreateVm();

        vm.ShortcutFilter = "Ctrl+Shift+L";
        int withPlus = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);

        vm.ShortcutFilter = "Ctrl Shift L";
        int withSpaces = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);

        Assert.AreEqual(1, withPlus, "加号写法搜不到 Ctrl+Shift+L");
        Assert.AreEqual(1, withSpaces, "空格写法搜不到 Ctrl Shift L");
    }

    /// <summary>搜不到时页面要能显示空态提示,而不是留一片什么都没有的白。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_NoMatch_ReportsEmptyState()
    {
        SettingsViewModel vm = CreateVm();

        vm.ShortcutFilter = "这个键位不存在";

        Assert.IsEmpty(vm.FilteredShortcutGroups);
        Assert.IsFalse(vm.HasShortcutMatches);
    }

    /// <summary>清空搜索词(只剩空白也算清空)回到全量表。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_Cleared_RestoresFullCatalog()
    {
        SettingsViewModel vm = CreateVm();
        int total = vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count);
        vm.ShortcutFilter = "F5";

        vm.ShortcutFilter = "   ";

        Assert.AreEqual(vm.ShortcutGroups.Length, vm.FilteredShortcutGroups.Length, "只有空白的搜索词等于没搜");
        Assert.AreEqual(total, vm.FilteredShortcutGroups.Sum(group => group.FilteredItems.Count));
    }

    // ———— 快捷键参考页的分组折叠 ————

    /// <summary>分组默认展开:参考页的首要用途是通读,折叠是用户主动收纳。</summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutGroups_DefaultToExpanded()
    {
        SettingsViewModel vm = CreateVm();

        Assert.IsTrue(vm.ShortcutGroups.All(group => group.IsExpanded));
    }

    /// <summary>
    /// 搜索命中的分组必须临时展开 —— 搜到了却因为分组是收起的而看不见,等于没搜。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_ForcesMatchingGroupOpen()
    {
        SettingsViewModel vm = CreateVm();
        foreach (ShortcutGroup group in vm.ShortcutGroups)
        {
            group.IsExpanded = false;
        }

        vm.ShortcutFilter = "F5";

        Assert.IsTrue(vm.FilteredShortcutGroups[0].IsExpanded, "命中的分组应被临时展开");
    }

    /// <summary>
    /// 搜索清空后要把用户原本的折叠态还回去:搜一次不该把人手动收起来的分组全打开。
    /// </summary>
    [TestMethod]
    [TestCategory("Settings")]
    public void ShortcutFilter_Cleared_RestoresCollapsedState()
    {
        SettingsViewModel vm = CreateVm();
        ShortcutGroup first = vm.ShortcutGroups[0];
        first.IsExpanded = false;

        vm.ShortcutFilter = "Ctrl";
        vm.ShortcutFilter = string.Empty;

        Assert.IsFalse(first.IsExpanded, "搜索结束后应还原为用户收起的状态");
    }
}
