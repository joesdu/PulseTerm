using Avalonia.Controls;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Terminal;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 停靠工作区是标签集合与活动标签的唯一事实来源(Q-02)。
/// </summary>
/// <remarks>
/// <para>
/// 在此之前 <c>TabBarViewModel</c> 另存了一份平行的标签列表与活动指针,于是每一次
/// 新增 / 关闭 / 激活都要在两个模型之间手工同步,还得防着回环。漏一处的表现是
/// "标签关掉了但树上状态还亮着"或者"快捷键切了标签画面不动" —— 两处症状毫不相干,
/// 极难联想到同一个根因(§24 / §39 修过两次的就是这类同形 bug)。
/// </para>
/// <para>
/// 这组用例钉住的就是"只有一份":从工作区加/删/激活之后,派生出来的那几个视图
/// (<c>TerminalTabs</c>、<c>ActiveTerminalTab</c>)必须立刻一致,不需要任何额外的同步调用。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Docking")]
public sealed class WorkspaceIsTheTabSourceOfTruthTests
{
    private static TerminalTabViewModel Tab(string name) =>
        new(Substitute.For<ITerminalEmulator>())
        {
            Title = name,
            Profile = new() { Name = name, Host = $"{name}.example" },
            ConnectionStatus = SessionStatus.Connected,
        };

    [TestMethod]
    public void OpeningADocumentMakesItTheActiveTab()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel tab = Tab("one");

        vm.Open(tab);

        Assert.AreSame(tab, vm.ActiveTerminalTab);
        Assert.AreSame(tab, vm.TerminalTabs.Single());
    }

    [TestMethod]
    public void ActivatingADocumentMovesTheActiveTab()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel first = Tab("one");
        TerminalTabViewModel second = Tab("two");
        vm.Open(first);
        vm.Open(second);
        Assert.AreSame(second, vm.ActiveTerminalTab, "新开的文档就是活动文档。");

        vm.Activate(first);

        Assert.AreSame(first, vm.ActiveTerminalTab);
    }

    [TestMethod]
    public void RemovingTheActiveDocumentHandsOverToWhatIsLeft()
    {
        // 静默移除(连接失败路径)不触发 DocumentClosed,活动标签仍必须落到还活着的那个上,
        // 否则界面会停在一个已经不存在的标签上。
        var vm = new MainWindowViewModel();
        TerminalTabViewModel first = Tab("one");
        TerminalTabViewModel second = Tab("two");
        vm.Open(first);
        TerminalDocument secondDocument = vm.Open(second);

        vm.Layout.RemoveDocument(secondDocument);

        Assert.AreSame(first, vm.ActiveTerminalTab);
        Assert.AreSame(first, vm.TerminalTabs.Single());
    }

    [TestMethod]
    public void ClosingTheLastDocumentLeavesNoActiveTab()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel only = Tab("one");
        TerminalDocument document = vm.Open(only);

        vm.Layout.RemoveDocument(document);

        Assert.IsNull(vm.ActiveTerminalTab);
        Assert.IsEmpty(vm.TerminalTabs);
    }

    [TestMethod]
    public void CtrlTabCyclesThroughTheGroupAndWrapsAround()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel first = Tab("one");
        TerminalTabViewModel second = Tab("two");
        vm.Open(first);
        vm.Open(second);
        vm.Activate(first);

        ((System.Windows.Input.ICommand)vm.NextTabCommand).Execute(null);
        Assert.AreSame(second, vm.ActiveTerminalTab);

        // 到尾回绕,而不是停在最后一个。
        ((System.Windows.Input.ICommand)vm.NextTabCommand).Execute(null);
        Assert.AreSame(first, vm.ActiveTerminalTab);

        ((System.Windows.Input.ICommand)vm.PreviousTabCommand).Execute(null);
        Assert.AreSame(second, vm.ActiveTerminalTab);
    }

    /// <summary>
    /// Ctrl+Tab 现在也能走到非终端文档上(SFTP 面板等)。
    /// </summary>
    /// <remarks>
    /// 这是相对改动前的**一处行为变化**:原先循环的是 <c>TabBar.Tabs</c>(只有终端标签),
    /// 于是同时开着终端与 SFTP 面板时,Ctrl+Tab 永远到不了 SFTP 那一页。
    /// 那更像是双模型留下的疏漏,而不是有意设计 —— 停靠标签条上明明摆着那一页。
    /// </remarks>
    [TestMethod]
    public void CtrlTabAlsoReachesNonTerminalDocuments()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel terminal = Tab("one");
        TerminalDocument terminalDocument = vm.Open(terminal);
        DockDocument other = new PlainDocument();
        vm.Layout.AddDocument(other);
        vm.Layout.ActivateDocument(terminalDocument);

        ((System.Windows.Input.ICommand)vm.NextTabCommand).Execute(null);

        Assert.AreSame(other, vm.Layout.ActiveDocument, "焦点确实走到了那一页上。");
    }

    /// <summary>切到别的**会话**文档时,活动终端标签清空。</summary>
    /// <remarks>
    /// 与工具面板那条相对:换了会话就是换了工作对象,状态栏必须跟着换,
    /// 否则它会一直显示着一台你已经不在看的机器。
    /// </remarks>
    [TestMethod]
    public void ASessionDocumentClearsTheActiveTerminalTab()
    {
        var vm = new MainWindowViewModel();
        vm.Open(Tab("one"));

        vm.Layout.AddDocument(new SftpDocument(new SftpDocumentViewModel(
            new SessionProfile { Name = "s", Host = "h" },
            Guid.NewGuid(),
            (_, _) => Task.CompletedTask,
            Substitute.For<ISftpService>(),
            new TransferOptions())));

        Assert.IsNull(vm.ActiveTerminalTab);
    }

    /// <summary>切到工具面板(AI 聊天等)时,会话上下文原封不动。</summary>
    /// <remarks>
    /// <para>
    /// 工具面板是<b>在当前会话上做事的工具</b>,不改变"我在哪台机器上"。用户开着文件浏览器
    /// 就是要它一直在;状态栏也该继续显示那台机器 —— 让它们随焦点闪来闪去是彻头彻尾的干扰。
    /// </para>
    /// <para>
    /// 这条曾经被改坏过一次:那次把判据写成"不是终端文档就清空并收起",插件面板被顺手
    /// 卷了进去。现在判据是文档自己声明的 <c>IsSessionDocument</c>。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void AToolPanelLeavesTheSessionContextAlone()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel terminal = Tab("one");
        TerminalDocument terminalDocument = vm.Open(terminal);
        // 用户把它打开了 —— 这正是这条用例的前提。不置这一下,收起那一支会被
        // 「浏览器本来就是空占位」的守卫短路掉,用例就成了空壳(第一版正是如此)。
        vm.FileBrowser.IsVisible = true;
        FileBrowserViewModel browserWhileOnTerminal = vm.FileBrowser;

        DockDocument chat = new PluginDocument("ai.chat", "AI", "velashell.ai", new Border());
        vm.Layout.AddDocument(chat);

        Assert.AreSame(chat, vm.Layout.ActiveDocument);
        Assert.AreSame(terminal, vm.ActiveTerminalTab,
            "工具面板不改变「我在哪台机器上」—— 清空它会让状态栏整个空掉。");
        Assert.AreSame(browserWhileOnTerminal, vm.FileBrowser,
            "切到工具面板不该把文件浏览器换成占位符 —— 终端还开着,只是暂时不在最上层。");

        // 切回终端不该有任何"恢复"动作要做:它压根没被动过。
        vm.Layout.ActivateDocument(terminalDocument);
        Assert.AreSame(terminal, vm.ActiveTerminalTab);
        Assert.AreSame(browserWhileOnTerminal, vm.FileBrowser);
    }

    /// <summary>以后新增的插件文档类型默认按工具面板处理。</summary>
    /// <remarks>
    /// 判据做成文档自己声明的属性、而不是宿主里的一张类型白名单,就是为了这一条:
    /// 白名单在新增类型时必然被忘掉,而忘掉的后果是一个症状与原因毫不相干的交互 bug。
    /// 默认按面板处理 —— "面板被误判成会话"比反过来更烦人。
    /// </remarks>
    [TestMethod]
    public void AnUnknownDocumentTypeDefaultsToBeingAToolPanel()
    {
        var vm = new MainWindowViewModel();
        TerminalTabViewModel terminal = Tab("one");
        vm.Open(terminal);
        vm.FileBrowser.IsVisible = true;
        FileBrowserViewModel browserWhileOnTerminal = vm.FileBrowser;

        vm.Layout.AddDocument(new PlainDocument());

        Assert.AreSame(terminal, vm.ActiveTerminalTab);
        Assert.AreSame(browserWhileOnTerminal, vm.FileBrowser);
    }

    /// <summary>切到独立 SFTP / 工作台文档时,底部的文件浏览器收起来。</summary>
    /// <remarks>
    /// 那个文档<b>本身就是</b>一个文件面板,底下再挂一个属于别的会话的浏览器是重复的。
    /// 这一支与上一条是同一个判断的两侧,一起钉住才说明白"判据是它还有没有意义"。
    /// </remarks>
    [TestMethod]
    public void AStandaloneFileDocumentDoesCollapseTheFileBrowser()
    {
        var vm = new MainWindowViewModel();
        vm.Open(Tab("one"));
        FileBrowserViewModel browserWhileOnTerminal = vm.FileBrowser;
        browserWhileOnTerminal.IsVisible = true;

        vm.Layout.AddDocument(new SftpDocument(new SftpDocumentViewModel(
            new SessionProfile { Name = "s", Host = "h" },
            Guid.NewGuid(),
            (_, _) => Task.CompletedTask,
            Substitute.For<ISftpService>(),
            new TransferOptions())));

        Assert.AreNotSame(browserWhileOnTerminal, vm.FileBrowser,
            "独立 SFTP 文档自己就是文件面板,底下那个应当收起。");
    }

    /// <summary>关掉最后一个文档之后,文件浏览器收起来。</summary>
    /// <remarks>没有任何会话了,浏览器里躺着的是一份已经死掉的列表。</remarks>
    [TestMethod]
    public void ClosingEverythingCollapsesTheFileBrowser()
    {
        var vm = new MainWindowViewModel();
        TerminalDocument only = vm.Open(Tab("one"));
        vm.FileBrowser.IsVisible = true;
        FileBrowserViewModel browserWhileOnTerminal = vm.FileBrowser;

        vm.Layout.RemoveDocument(only);

        Assert.AreNotSame(browserWhileOnTerminal, vm.FileBrowser);
    }

    /// <summary>一个不承载终端的普通停靠文档。</summary>
    private sealed class PlainDocument : DockDocument
    {
        public PlainDocument() => Title = "面板";
    }
}
