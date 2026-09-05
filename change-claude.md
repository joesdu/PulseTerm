# VelaShell 改动方案(change-claude.md)

> 本文是 `plan-claude.md` 每一项的**代码级改法**:改哪些文件、加什么类型、关键代码长什么样、为什么要这么改、怎么验证、出了问题怎么退。
> **本文只写方案,不动代码。** 逐项确认后再按 §1 的批次顺序动手,每项一个 PR。
> 编号与 `plan-claude.md` 一一对应;两处复核后与原判断不同的地方在 §0.2 单独列出。

---

## 0. 使用说明

### 0.1 每项的固定结构

- **目标**:一句话说清做完后用户看到什么变化。
- **改动清单**:文件级,新增 / 修改 / 删除。
- **关键代码**:只写决定设计的那几段(签名、状态机、不变量),不写全量实现。
- **为什么这么改**:替代方案是什么、为什么不选。
- **验证**:新增/修改的测试用例名与断言口径,以及实机检查点。
- **风险与回滚**:可能影响的现有行为,以及退回方式。

### 0.2 复核修订(与 plan-claude.md 不同的结论)

| 编号 | 原判断 | 复核结果 | 处理 |
| --- | --- | --- | --- |
| P-09 | 关标签时 UI 线程同步等待读/写任务最多 3 秒 | **不成立**。`TerminalTabViewModel.Dispose`(:627)与 `DetachTransport`(:787)都已 `Task.Run(bridge.Dispose)`;退出路径 `App.CloseTerminalBridgesOnExit`(:466)也是并行 `Task.Run` + 2 秒总上限。UI 线程从不等待。 | **撤回**。`plan-claude.md` 的 P-09 行同步改为「撤回」。剩余可做的只是把 `Dispose` 内部的 `Wait` 改成 `DisposeAsync`,属于代码整洁,并入 Q-04。 |
| U-02 | 需要新增状态色、遮罩等令牌 | `VelaStatusConnected / Connecting / Disconnected`、`VelaScrim / VelaScrimStrong`、`VelaAccentForeground`、`VelaErrorForeground` **已经存在**(`ThemeTokenApplier.cs:127-150`),只是三处 C# 与若干 XAML 没有用 | 工作量下调:大部分是「改成引用现有令牌」,真正要新增的只有 强调色板 8 色、同步通道 4 色、危险悬停色 |
| U-09 | 连接对话框无校验 | 保存/连接/测试按钮已按 主机非空 / 用户名非空 / 端口范围 做 `CanExecute` 门槛(`ConnectionProfileViewModel.cs:190-206`) | 范围收窄为「按钮灰掉但不说原因」→ 字段下方内联提示 + 私钥文件存在性校验 |
| F-11 | 评估 Tmds.Ssh 的 keyboard-interactive 支持面 | 已查 `tmds.ssh/0.24.0` 的 XML 文档:凭据类型只有 `Password / PrivateKey / Certificate / Kerberos / SshAgent / No`,**没有** keyboard-interactive | 本仓库暂不能实现;方案改为「上游跟进 + 本地失败诊断文案」,见 F-11 |

### 0.3 全局约定(每一项都遵守,下文不再重复)

1. **文案**:新增键必须同时进五份 resx(`Strings / zh-Hans / zh-Hant / ja / ko`),命名沿用现有前缀(`SetTerm_`、`SetGen_`、`Cmd_`、`Sc_`、`Notify_`、`Msg_`、`Sidebar_`、`Editor_`)。C# 侧用 `Strings.Get/Format`,XAML 用 `{loc:Localize}`。
2. **快捷键**:新增或改动键位先改 `ShortcutCatalog.cs`,再改 `MainWindow.axaml` 的 `KeyBindings` / `KeyboardShortcutService`;`ShortcutCatalogTests` 会打印可粘贴的文档行,同步到 velashell-docs 的 `快捷键参考.md`。
3. **颜色**:一律 `DynamicResource`;新令牌加在 `ThemeTokenApplier.BuildTokens` 的派生表里,由 seed palette 派生,`ThemeTokenApplierTests` 会把 axaml 默认值与派生值钉在一起。
4. **设置项**:字段加在 `AppSettings.cs` 对应 `*Options` 类(带 `Set(ref field, value)` 通知),设置页 XAML 直接绑定 `Options.字段`(现有写法,见 `TerminalSettingsPage.axaml:175`),运行时消费点在 `MainWindowViewModel.ApplyLiveTerminalSettings` 或对应服务。
5. **测试**:MSTest,`[TestCategory]` 沿用现有分类;headless UI 用例用带返回值的 `Dispatch(async () => { …; return true; })`(AGENTS.md 明令)。
6. **提交**:一项一个 PR;涉及行为/配置/快捷键的,PR 正文引用 velashell-docs 的对应 PR。

### 0.4 关于 Avalonia 键绑定的一个事实(F-03 / F-04 / U-06 都依赖)

`Window.KeyBindings` 由 `KeyboardDevice` 在**分发 KeyDown 路由事件之前**从焦点元素向上逐级匹配,命中即 `Handled`。因此:
- 写在 `MainWindow.axaml` 里的手势**总是**先于终端控件的 `OnKeyDown` 生效(这正是终端有焦点时 `Ctrl+P` 仍能打开命令面板的原因);
- 反过来,任何加进 `KeyBindings` 的手势都会**无条件**从终端手里抢走。要「有条件拦截」(如只有分屏时 Alt+方向才移焦)必须用 `AddHandler(KeyDownEvent, …, RoutingStrategies.Tunnel)` 在 `MainWindow` 代码隐藏里判断后再吃掉。

---

## 1. 批次与顺序

| 批次 | 事项 | 说明 |
| --- | --- | --- |
| 第一批 | U-03 · U-08 · Q-03 · F-03 · P-02 · P-04 · P-06 · F-02 · F-04 · F-05 · U-02 · Q-05 · Q-07 | 低风险、独立、可并行;P-09 撤回 |
| 第二批 | P-01 · P-03(A) · P-05 · F-07 · F-08 · F-09 · F-10 · U-01 · U-04 · U-05 · U-06 · U-07 · U-09 · Q-04 · Q-06 | 需要设置项 / 新服务 / 跨层接线 |
| 第三批 | P-07 · P-08 · P-10 · P-03(B) · F-06 · F-11 · U-10 · Q-01 · Q-02 | 需要先量化或先拆结构 |

需要**先拍板**的决策集中在 §5,主要是几组手势的取舍。

---

## 2. 第一批

### U-03 侧栏底部用户名写死为 `root`

**目标**:底部那行显示活动会话的 `用户名@主机`;本地终端显示本机用户名;没有活动会话时整行隐藏。

**改动清单**
- 修改 `src/VelaShell.Presentation/ViewModels/SidebarViewModel.cs`:新增 `ActiveIdentity`(`string?`,`RaiseAndSetIfChanged`)与 `HasActiveIdentity => !string.IsNullOrEmpty(ActiveIdentity)`。
- 修改 `src/VelaShell/ViewModels/MainWindowViewModel.cs` `UpdateStatusBarForActiveTab`(:4631):同一处已经在按活动标签刷新状态栏,追加一行写 `Sidebar.ActiveIdentity`。
- 修改 `src/VelaShell/Views/SidebarView.axaml:243`:`Text="{Binding ActiveIdentity}"`,外层 `StackPanel` 加 `IsVisible="{Binding HasActiveIdentity}"`,`ToolTip.Tip` 绑同一值(长主机名被裁剪时能看全)。

**关键代码**
```csharp
// MainWindowViewModel.UpdateStatusBarForActiveTab 末尾
Sidebar.ActiveIdentity = ActiveTerminalTab switch
{
    { LocalShell: not null } => Environment.UserName,
    { Profile: { } p } => string.IsNullOrEmpty(p.Username) ? p.Host : $"{p.Username}@{p.Host}",
    _ => null
};
```

**为什么这么改**:放在 `UpdateStatusBarForActiveTab` 而不是新开一条 `WhenAnyValue` 管道,因为它已经订阅了 `ActiveTerminalTab` 与该标签的 `ConnectionStatus`,断线/切标签都会走到这里;不需要第二套触发源。

**验证**:`MainWindowViewModelTests` 加 `ActivatingTab_SetsSidebarIdentity_AndClearsWhenNoTab`;实机看本地终端标签显示本机用户名。

**风险与回滚**:无;纯展示。

---

### U-08 崩溃与错误只写 `Trace`,发布版无日志落盘

**目标**:任何构建都把 `Trace` 写进 `~/.velashell/logs/velashell-yyyyMMdd.log`(保留 7 天);未处理异常写 `crash-yyyyMMdd-HHmmss.txt`;下次启动在消息中心提示;关于页与消息中心有「打开日志目录」。

**改动清单**
- 新增 `src/VelaShell.Infrastructure/Diagnostics/DiagnosticLog.cs`:静态类,`Initialize(string logsDirectory, int retainDays = 7)`、`WriteCrash(string kind, Exception ex)`、`TryTakeUnseenCrash(out string path)`、`OpenLogsDirectory()`。
- 新增 `src/VelaShell.Infrastructure/Diagnostics/RollingFileTraceListener.cs`:`TraceListener` 子类,按天滚动,`Write/WriteLine` 加锁、逐行 `Flush`。
- 修改 `src/VelaShell/Program.cs`:`Main` 里紧接 `VelaShellStartupArguments.Parse` 之后调用 `DiagnosticLog.Initialize(new VelaShellStoragePaths().LogsDirectory)`(整个包在 try/catch,日志初始化失败不能挡启动);`InstallGlobalExceptionGuards`(:278)两个处理器里追加 `DiagnosticLog.WriteCrash(...)`;`Main` 最外层 `catch (Exception ex)` 也写一份。
- 修改 `src/VelaShell/ViewModels/MainWindowViewModel.cs`:`RegisterCommands` 新增命令 `app.logs.open`(标题 `Cmd_OpenLogs`,分类 `CmdCat_Actions`,图标 `Icon.folder`);`RefreshNotificationSourcesAsync`(:1586)新增 `BuildCrashNotification()`。
- 修改 `src/VelaShell.Core/Models/Notification.cs`:`NotificationKind` 增加 `System`。
- 修改 `src/VelaShell/Views/Settings/AboutPage.axaml:117` 附近:加一个 `dlg-outline` 按钮 `SetAbout_OpenLogs`,`Command="{Binding OpenLogsDirectoryCommand}"`;`SettingsViewModel` 加该命令(实现同样调 `DiagnosticLog.OpenLogsDirectory()`)。
- 修改 `src/VelaShell/App.axaml.cs`:所有 `Trace.WriteLine($"[VelaShell] ...")` 不动(它们自动进文件了)。
- 文案:`Cmd_OpenLogs`、`SetAbout_OpenLogs`、`Notify_CrashTitle`、`Notify_CrashBody`、`Notify_CrashAction` ×5 resx。

**关键代码**
```csharp
public static class DiagnosticLog
{
    private const string SeenMarker = "crash.seen";
    public static string? Directory { get; private set; }

    public static void Initialize(string logsDirectory, int retainDays = 7)
    {
        System.IO.Directory.CreateDirectory(logsDirectory);
        Directory = logsDirectory;
        Trace.Listeners.Add(new RollingFileTraceListener(logsDirectory, "velashell-", retainDays));
        Trace.AutoFlush = true;
        Trace.WriteLine($"[Startup] VelaShell {version} pid={Environment.ProcessId} os={RuntimeInformation.OSDescription}");
        _ = Task.Run(() => Prune(logsDirectory, retainDays)); // 清理不占启动路径
    }

    public static void WriteCrash(string kind, object exception)
    {
        if (Directory is null) return;
        string path = Path.Combine(Directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, $"{kind}\n{DateTime.Now:O}\n{exception}\n");
        Trace.WriteLine($"[Crash] {kind}: written to {path}");
    }

    /// 上次启动之后出现、且尚未提示过的最新崩溃文件;取走即标记为已看。
    public static bool TryTakeUnseenCrash(out string path) { … 比较 crash-*.txt 的最新时间与 SeenMarker 的时间戳 … }
}
```
```csharp
// MainWindowViewModel
private static NotificationItem? BuildCrashNotification()
    => DiagnosticLog.TryTakeUnseenCrash(out string path)
        ? new() { Id = "crash:" + Path.GetFileName(path), Kind = NotificationKind.System,
                  Severity = NotificationSeverity.Warning,
                  Title = Strings.Get("Notify_CrashTitle"),
                  Body = Strings.Format("Notify_CrashBody", Path.GetFileName(path)),
                  PublishedAt = DateTime.UtcNow,
                  Link = new() { Label = Strings.Get("Notify_CrashAction"), CommandId = "app.logs.open" } }
        : null;
```

**为什么这么改**
- 不引入 Serilog/NLog:全仓 65 处已经是 `Trace.WriteLine`,挂一个 `TraceListener` 零改动就全部落盘;插件宿主已经把 `paths.LogsDirectory` 当作 `DiagnosticsDirectory`(`PluginServiceCollectionExtensions.cs:60`),两边落在同一目录,排障时一处找齐。
- 崩溃提示走消息中心而不是启动弹窗:AGENTS/plan 里多次强调「启动时弹窗很烦」;消息中心已有「链接 → 命令」的机制(`Link.CommandId`),复用即可。
- `Prune` 放后台:目录枚举是 IO,与 P-05 的原则一致。

**验证**:`DiagnosticLogTests`(Infrastructure.Tests):① `Trace.WriteLine` 后当天文件含该行;② 8 天前的文件被清;③ `WriteCrash` 后 `TryTakeUnseenCrash` 一次为 true、再次为 false;④ `Initialize` 对只读目录不抛。实机:`VELASHELL_STARTUP_TRACE`(见 P-05)与日志文件内容一致。

**风险与回滚**:磁盘写入失败时 listener 内部吞异常(`TraceListener` 抛出会连带把 `Trace.WriteLine` 调用方炸掉)。回滚 = 不调用 `Initialize`。

---

### Q-03 CI 只有发布流水线

**目标**:push 到 `dev`/`main` 与所有 PR 都跑 构建 + 测试(Windows + Ubuntu),失败用例名可见。

**改动清单**
- 新增 `.github/workflows/ci.yml`。
- 修改 `README.md` / `README.en.md`:加 CI 徽章(可选)。

**关键代码**
```yaml
name: CI
on:
  push: { branches: [main, dev] }
  pull_request:
concurrency: { group: ci-${{ github.ref }}, cancel-in-progress: true }
jobs:
  test:
    strategy:
      fail-fast: false
      matrix: { os: [windows-latest, ubuntu-latest] }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with: { global-json-file: global.json }
      - run: dotnet build VelaShell.slnx -c Debug --nologo -warnaserror
      - run: >
          dotnet test VelaShell.slnx -c Debug --no-build --nologo
          --filter "TestCategory!=DockerIntegration&TestCategory!=CrossPlatform"
          --logger "trx;LogFilePrefix=ci" --results-directory artifacts/trx
      - uses: actions/upload-artifact@v7
        if: always()
        with: { name: trx-${{ matrix.os }}, path: artifacts/trx/*.trx }
```

**为什么这么改**
- 用 **Debug**:`SignAssembly` 只在 Release 打开(`src/Directory.Build.props`),Debug 不需要 `VelaShell.snk`,fork 与 Dependabot 的 PR 拿不到 secret 也能跑。
- 排除 `DockerIntegration` / `CrossPlatform`:前者要 Docker,后者要 `VELASHELL_PUBLISH_TESTS=1`;AGENTS.md 已说明它们按环境早退且 MSTest 记为通过,在 CI 上跑只是浪费。
- Ubuntu 一并跑:headless UI 测试不需要显示服务器;顺便把 `#if LINUX` 的 `UseWayland` 分支编译到。
- `-warnaserror`:当前基线就是 0 警告,守住它。
- 不加 `dotnet format --verify-no-changes`:CRLF 工作树与 `end_of_line = lf` 的 .editorconfig 会产生 18 万条 ENDOFLINE 噪音(见 `velashell-line-endings-and-format-noise`),先解决换行再加。

**验证**:开一个空 PR 看两条矩阵都绿;故意在测试里加一个失败看 trx 上传。

**风险与回滚**:Plugin.Ai 测试 2.5 分钟拉长总时长(Q-06 会缩);删文件即回滚。

---

### F-03 「键盘优先」缺核心键位

**目标**:分屏、跳标签、字号缩放、窗格移焦、清屏、关闭全部 都有键位,并进快捷键页与文档。

手势选择(§5 待拍板):

| 动作 | 命令 id | 手势(Win/Linux) | macOS | 备注 |
| --- | --- | --- | --- | --- |
| 跳到第 N 个标签 | `tab.goto.N`(1–8) | `Ctrl+Alt+N` | `Cmd+N` | `Ctrl+2…7` 是控制字符(`^@ ^[ ^\ ^] ^^ ^_`),不能用 `Ctrl+数字`;Windows Terminal 也是 `Ctrl+Alt+数字` |
| 最后一个标签 | `tab.goto.last` | `Ctrl+Alt+9` | `Cmd+9` | |
| 右侧分屏 | `split.horizontal`(已有) | `Ctrl+Shift+D` | 同 | |
| 下方分屏 | `split.vertical`(已有) | `Ctrl+Shift+S` | 同 | S = stack;`Ctrl+Shift+S` 终端里无用途 |
| 窗格移焦 | `pane.focus.left/right/up/down` | `Alt+←→↑↓` | 同 | **有条件拦截**:只有 >1 个窗格时才吃掉,否则照旧送 meta+方向 |
| 字号加/减/重置 | `view.zoom.in/out/reset` | `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | `Cmd+…` | `Ctrl+-` 会抢走 `^_`;`Ctrl+Shift+-` 仍经 `InputEncoder.OemMinus → 0x1F` 送到远端(emacs undo 可用) |
| 清屏 | `edit.clear`(已有) | `Ctrl+Shift+K` | 同 | 命令已存在,只补 `Shortcut` |
| 关闭全部标签 | `session.close.all` | `Ctrl+Shift+W` | 同 | 经 F-07 的确认闸 |

**改动清单**
- 修改 `src/VelaShell/Docking/Model/DockEnums.cs`:新增 `enum DockDirection { Left, Right, Up, Down }`(现有只有 `DockOrientation / DockPosition / DockTabsPosition`)。
- 修改 `src/VelaShell/Docking/Model/DockWorkspace.cs`:新增 `DockGroup? FindNeighborGroup(DockGroup from, DockDirection direction)` 与 `bool HasMultipleGroups => AllGroups().Skip(1).Any()`。
- 修改 `src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs`:把 `OnPointerWheelChanged` 里的字号步进抽成 `public void AdjustFontSize(int delta)` / `public void ResetFontSize(double size)`(都触发 `FontSizeChanged`),滚轮分支改调它。
- 修改 `src/VelaShell/ViewModels/MainWindowViewModel.cs` `RegisterCommands`:注册 `tab.goto.1..8`、`tab.goto.last`、`pane.focus.*`、`view.zoom.*`、`session.close.all`;`edit.clear` 补 `Shortcut: "Ctrl+Shift+K"`;`split.*` 补 `Shortcut`。
- 修改 `src/VelaShell/Views/MainWindow.axaml` `KeyBindings`:加 `Ctrl+Alt+D1…D9`、`Ctrl+Shift+D`、`Ctrl+Shift+S`、`Ctrl+OemPlus`、`Ctrl+OemMinus`、`Ctrl+D0`、`Ctrl+Shift+K`、`Ctrl+Shift+W`,全部 `Command="{Binding RunCommand}" CommandParameter="<id>"`。
- 修改 `src/VelaShell/Views/MainWindow.axaml.cs`:构造函数 `AddHandler(KeyDownEvent, OnPaneNavigationKeyDown, RoutingStrategies.Tunnel)`,只在 `vm.Layout.HasMultipleGroups && e.KeyModifiers == Alt && e.Key is Left/Right/Up/Down` 时执行 `pane.focus.*` 并 `Handled`。
- 修改 `src/VelaShell/ViewModels/ShortcutCatalog.cs`:`Sc_GroupTabsAndPanels` 加 7 条,`SetVm_SectionTerminal` 加缩放 3 条与清屏。
- 文案:`Cmd_GotoTab`(带 `{0}`)、`Cmd_GotoLastTab`、`Cmd_FocusPaneLeft/Right/Up/Down`、`Cmd_ZoomIn/Out/Reset`、`Cmd_CloseAllTabs`、`Sc_*` 若干 ×5。
- velashell-docs:`快捷键参考.md` 同步。

**关键代码**
```csharp
// DockWorkspace:沿父链找第一个方向匹配的 DockSplit,取相邻子树里"最靠近"的组
public DockGroup? FindNeighborGroup(DockGroup from, DockDirection direction)
{
    DockNode node = from;
    while (node.Parent is { } split)
    {
        bool horizontal = split.Orientation == DockOrientation.Horizontal;
        bool wants = direction is DockDirection.Left or DockDirection.Right ? horizontal : !horizontal;
        int index = split.Children.IndexOf(node);
        int step = direction is DockDirection.Left or DockDirection.Up ? -1 : 1;
        if (wants && index + step >= 0 && index + step < split.Children.Count)
        {
            return Descend(split.Children[index + step], enterFromEnd: step < 0);
        }
        node = split;
    }
    return null;
    // Descend:DockGroup 直接返回;DockSplit 取 enterFromEnd ? 最后 : 第一个 子节点递归
}
```
```csharp
// MainWindowViewModel:跳标签按"活动组内第 N 个文档",与用户眼睛看到的标签条一致
private void GotoTab(int index)
{
    DockGroup? group = Layout.ActiveDocument is { } d ? Layout.FindGroup(d) : Layout.PrimaryGroup;
    if (group is null || group.Documents.Count == 0) return;
    int i = index < 0 ? group.Documents.Count - 1 : Math.Min(index, group.Documents.Count - 1);
    Layout.ActivateDocument(group.Documents[i]);
    TerminalFocusRequested?.Invoke(this, EventArgs.Empty);
}
```

**为什么这么改**
- 手势放 `Window.KeyBindings` 而不是扩展 `KeyboardShortcutService`:现有全局手势全部走 `KeyBindings + RunCommand`(`MainWindow.axaml:21-42`),`KeyboardShortcutService` 只负责终端内文本快捷键;两套并存已是现状,不在这次把它们合并(那是 Q-01 之后的事)。
- 窗格移焦不进 `KeyBindings`:§0.4 说明了 `KeyBindings` 无条件抢键;Alt+方向在 zsh/fish 里有用户绑定,只在真有多个窗格时才拦。
- 跳标签按「活动组」而不是全局 `TabBar.Tabs`:分屏后每个组有自己的标签条,用户数的是眼前那条。

**验证**
- `DockWorkspaceTests`:`FindNeighborGroup_*` 四个方向 + 嵌套分屏 + 边缘返回 null。
- `ShortcutCatalogTests` 全绿(它会逼你把新键位登记进目录)。
- headless `MainWindowKeyboardUiTests`:`Ctrl+Alt+D2` 激活第二个文档;只有一个窗格时 `Alt+Right` 不被吃掉(终端收到 `ESC [1;3C`);两个窗格时移焦。
- `VelaTerminalControlFontTests`:`AdjustFontSize` 夹在 6–40 并触发 `FontSizeChanged`。

**风险与回滚**:`Ctrl+-` 抢走 `^_`、`Ctrl+Alt+数字` 在 AltGr 键盘上可能与符号输入冲突——两者都在 §5 拍板,并在 `快捷键参考.md` 写明替代键。回滚 = 删 KeyBindings 行。

---

### P-02 非默认背景逐格 `FillRectangle`

**目标**:同一行相邻同色背景合并成一个矩形;搜索高亮的字典查找提到行首;像素输出不变。

**改动清单**
- 修改 `src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs`:`RenderLine`(:2155)、`FlushGlyphRun`(:1425)、`AppendGlyph`(:1356);新增字段 `_bgRunStart / _bgRunEnd / _bgRunPacked / _bgRunBrush`,新增 `internal int BackgroundRectCountForTest`。

**关键代码**
```csharp
// RenderLine 循环体里,原来的
//   if (!bg.Equals(palette.DefaultBackground)) context.FillRectangle(BrushFor(bg), CellRect(col, width, y));
// 改为
if (!bg.Equals(palette.DefaultBackground))
{
    if (_bgRunStart >= 0 && (bg.Packed != _bgRunPacked || col != _bgRunEnd))
        FlushBackgroundRun(context, y);
    if (_bgRunStart < 0) { _bgRunStart = col; _bgRunPacked = bg.Packed; _bgRunBrush = BrushFor(bg); }
    _bgRunEnd = col + width;
}
else if (_bgRunStart >= 0)
{
    FlushBackgroundRun(context, y);
}

private void FlushBackgroundRun(DrawingContext context, double y)
{
    if (_bgRunStart < 0) return;
    context.FillRectangle(_bgRunBrush!, CellRect(_bgRunStart, _bgRunEnd - _bgRunStart, y));
    BackgroundRectCountForTest++;
    _bgRunStart = -1;
}
```
不变量:**背景矩形必须画在同一格字形之前**。字形是攒批后延迟画的,所以 `FlushGlyphRun` 的第一行必须先 `FlushBackgroundRun(context, y)`;否则「A 红底样式 X,B 红底样式 Y」这种序列会在 B 处先画出 A 的字形、再画整段红底把它盖掉。行尾 `FlushGlyphRun` 也会顺带把最后一段背景冲掉,不需要额外收尾。

搜索高亮:把 `_searchHighlights.TryGetValue(absoluteRow, …)` 提到 `while` 之前取一次 `searchSpans`,循环内只做区间比较。

**为什么这么改**:与字形合批完全同构(状态机、行尾冲刷),不引入第二套绘制路径;不做「整帧两遍扫描」是因为颜色解析(`palette.Resolve` + inverse + 选区 + 搜索)每格做两次的成本不比省下的绘制指令便宜。

**验证**:`VelaShell.Terminal.RenderTests` 像素回归不变;`VelaShell.Terminal.Tests` 新增 `BackgroundRunTests`:整行同色 → `BackgroundRectCountForTest == 1`;`A_A`(红/默认/红)→ 2;红底 A 样式 X 紧接红底 B 样式 Y → 像素上两格都可见字形(headless 渲染到位图取样两个格心)。

**风险与回滚**:半透明背景色叠加次序不变(每格仍只画一次);回滚 = 恢复逐格调用。

---

### P-04 每次读设置 = 整份 JSON 反序列化

**目标**:只读调用方拿共享快照,不再逐次反序列化;`GetSettingsAsync` 语义(独立可改实例)保留给设置窗口。

**改动清单**
- 修改 `src/VelaShell.Core/Data/ISettingsService.cs`:新增
  ```csharp
  /// 与最近一次读/存一致的只读共享实例;调用方不得修改。未加载前为 null。
  AppSettings? CurrentSnapshot => null;
  /// 有快照直接返回,否则加载一次(加载后即有快照)。
  async ValueTask<AppSettings> GetSnapshotAsync()
      => CurrentSnapshot ?? await GetSettingsAsync().ConfigureAwait(false);
  ```
  两个都是**默认接口实现**,`Substitute.For<ISettingsService>()`(测试里 23 处)不受影响。
- 修改 `src/VelaShell.Infrastructure/Persistence/SonnetDbSettingsService.cs`:字段 `volatile AppSettings? _snapshot`;`GetSettingsAsync` 首次加载后 `_snapshot = DeserializeFresh(json)`;`SaveSettingsAsync` 里在写 `_settingsJsonCache` 之后同样刷新 `_snapshot`(从 json 反序列化一份新的,不直接引用调用方传入的对象);`CurrentSnapshot => _snapshot`。
- 修改调用方为快照:
  - `src/VelaShell.Core/Sftp/SftpService.cs:651` `GetTransferTuningAsync` → `await settingsService.GetSnapshotAsync()`;
  - `src/VelaShell.Infrastructure/Net/ProxyResolver.cs:41` `ReadOptions` → `settings.CurrentSnapshot?.Proxy ?? settings.GetSettingsAsync().GetAwaiter().GetResult().Proxy`(首次仍同步,之后零反序列化);
  - `src/VelaShell.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs:205-243` 四处工厂委托同上;
  - `src/VelaShell/ViewModels/MainWindowViewModel.cs` 只读处(`LoadSettingsSnapshotAsync` 及其调用者)改 `GetSnapshotAsync`;`SettingsViewModel` 与需要改写的地方保留 `GetSettingsAsync`。

**为什么这么改**:当初缓存 JSON 文本是为了「调用方拿独立实例可安全修改」;现在把这条语义留在 `GetSettingsAsync`,新加一条只读通道,调用方自己选,行为零变化。不做 `AppSettings` 不可变化(`ObservableOptions` 带 INPC、设置页直接双向绑定,改不动)。

**验证**:`SonnetDbSettingsServiceTests` 新增:① Save 后 `CurrentSnapshot` 字段与保存值一致;② 修改 `GetSettingsAsync` 返回的实例不影响 `CurrentSnapshot`;③ 快照在 Save 后是新引用。`SftpServiceTests` 用 `Substitute` 断言批量传输 N 个文件时 `GetSettingsAsync` 调用 ≤ 1。

**风险与回滚**:调用方误改快照会污染后续读取——接口注释与 code review 把关;回滚 = 调用方改回 `GetSettingsAsync`。

---

### P-06 资源监视器每个 tick 重建全部行对象

**目标**:行对象按 key 复用、只更新属性;选中行在刷新后保留。

**改动清单**
- 修改 `src/VelaShell/ViewModels/ResourceMonitorRows.cs`:`ProcessRow`、`PartitionRow`、`GpuProcessRow`、`ConnectionRow`、`CoreRow` 由 `record` 改为 `sealed class … : ReactiveObject`(属性名不变,XAML 零改动),各加 `Key` 与 `Update(...)`;新增静态 `RowMerge.Apply<TRow, TSource, TKey>(ObservableCollection<TRow> target, IEnumerable<TSource> source, Func<TSource, TKey> key, Func<TRow, TKey> rowKey, Func<TSource, TRow> create, Action<TRow, TSource> update)`。
- 修改 `src/VelaShell/ViewModels/ResourceMonitorWindowViewModel.cs`:`:836 / :932 / :1025 / :1172 / :1256 / :1285` 六处 `Fill(...)` 改 `RowMerge.Apply(...)`;`Fill<T>` 删除。

**关键代码**
```csharp
public static void Apply<TRow, TSource, TKey>(ObservableCollection<TRow> target, IEnumerable<TSource> source,
    Func<TSource, TKey> key, Func<TRow, TKey> rowKey, Func<TSource, TRow> create, Action<TRow, TSource> update)
    where TKey : notnull
{
    var byKey = new Dictionary<TKey, TRow>(target.Count);
    foreach (TRow row in target) byKey[rowKey(row)] = row;
    int i = 0;
    foreach (TSource item in source)
    {
        TKey k = key(item);
        if (byKey.TryGetValue(k, out TRow? row)) { update(row, item); int at = target.IndexOf(row); if (at != i) target.Move(at, i); }
        else { row = create(item); target.Insert(i, row); }
        i++;
    }
    while (target.Count > i) target.RemoveAt(target.Count - 1);
}
```
key 选择:`ProcessRow.Pid`、`PartitionRow.MountPoint`、`ConnectionRow.(Peer, Process)`、`GpuProcessRow.(GpuText, Pid)`、`CoreRow.Label`。

**为什么这么改**:与 `ProcessManagerViewModel.Merge/ApplyView`(:337 / :373)同一思路,让同一个窗口里两套列表行为一致;`Move` 而不是删了再插,是那边注释里写明的教训(选中项被冲掉)。

**验证**:`ResourceMonitorWindowViewModelTests`(`MonitorUI` 分类):同 PID 两次 `Apply` 后 `ReferenceEquals` 为 true;进程消失被移除、新进程插到正确位置;选中行保持。实机开 1 秒档进程页看 CPU 占用。

**风险与回滚**:`record` 的 `with`/相等语义若有人用到——全仓 grep 无;回滚 = 恢复 record + Fill。

---

### F-02 备用屏无 Alternate Scroll

**目标**:`less` / `vim` / `man`(未开鼠标)里滚轮变方向键;应用可用 `?1007` 关闭;设置可整体关闭。

**改动清单**
- 修改 `src/VelaShell.Terminal/Emulation/TerminalModes.cs`:`public bool AlternateScroll = true; // xterm ?1007`,`Reset()` 置回 true。
- 修改 `src/VelaShell.Terminal/Emulation/TerminalEmulator.cs` DECSET 分支(:784 附近):`case 1007: Modes.AlternateScroll = set; break;`。
- 修改 `src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs`:新增 `public bool AlternateScrollEnabled { get; set; } = true;`;`OnPointerWheelChanged`(:3131)在鼠标追踪分支之后插入下面的分支。
- 修改 `src/VelaShell.Core/Models/AppSettings.cs` `TerminalBehaviorOptions`:`AlternateScroll`(bool,默认 true)。
- 修改 `src/VelaShell/Views/Settings/TerminalSettingsPage.axaml` 「滚动」节:加一行 `SetTerm_AlternateScroll` / `SetTerm_AlternateScrollDesc` + `ToggleSwitch IsChecked="{Binding TerminalBehavior.AlternateScroll}"`。
- 修改 `src/VelaShell/ViewModels/MainWindowViewModel.cs` `ApplyLiveTerminalSettings`(:4328 附近):`control.AlternateScrollEnabled = behavior.AlternateScroll;`。
- 文案 ×5。

**关键代码**
```csharp
if (AlternateScrollEnabled && Emulator.Modes.AlternateScroll
    && Emulator.IsAlternateScreen && Emulator.Modes.Mouse == MouseTracking.None && e.Delta.Y != 0)
{
    int lines = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y) * WheelScrollLines));
    byte[] one = InputEncoder.Encode(e.Delta.Y > 0 ? Key.Up : Key.Down, KeyModifiers.None, Emulator.Modes, Emulator.Type)!;
    var payload = new byte[one.Length * lines];
    for (int i = 0; i < lines; i++) one.CopyTo(payload, i * one.Length);
    SendTypedInput(payload);
    e.Handled = true;
    return;
}
```
走 `InputEncoder.Encode(Key.Up …)` 而不是手写 `ESC [ A`:DECCKM 开着时应用要的是 `SS3 A`,编码器已经会判。

**为什么这么改**:这是 xterm(`alternateScroll` 资源)、Windows Terminal、iTerm2 的默认行为;`?1007` 是 xterm 定义的开关,放进 `TerminalModes` 让应用能自己关。

**验证**:`VelaTerminalControlMouseTests`(`Mouse` 分类):备用屏 + 无鼠标追踪 + 滚轮上 → `UserInput` 收到 `ESC[A` ×3;DECCKM 开 → `ESC O A`;`?1007l` 后不再发送;主屏滚轮仍滚回滚。

**风险与回滚**:全屏程序若既不开鼠标也不想要方向键(极少)——`?1007l` 或设置关。回滚 = 设置默认改 false。

---

### F-04 会话树没有过滤/搜索框

**目标**:侧栏树顶部一个过滤框,按 名称/主机/用户名/标签/分组名 过滤,命中的分组自动展开,清空后恢复原折叠状态;`Ctrl+Shift+E` 聚焦。

**改动清单**
- 修改 `src/VelaShell.Presentation/ViewModels/SessionTreeViewModel.cs`:新增 `FilterText`(set 后 `SyncRows()`)、`HasFilter`、`ClearFilterCommand`、`Dictionary<Guid,bool> _expansionBeforeFilter`;`SyncRows()`(:177)在 `HasFilter` 时按下面逻辑取行。
- 修改 `src/VelaShell/Views/SidebarView.axaml:85` 上方:加 `TextBox x:Name="TreeFilterBox"`(`Watermark="{loc:Localize Sidebar_FilterSessions}"`,右侧清除按钮 `IsVisible="{Binding SessionTree.HasFilter}"`),`Text="{Binding SessionTree.FilterText}"`。
- 修改 `src/VelaShell/Views/SidebarView.axaml.cs`:`Esc` 在过滤框 → 清空并把焦点还给树;`Down` → 焦点到树第一行。
- 修改 `src/VelaShell/Views/MainWindow.axaml`:`KeyBinding Ctrl+Shift+E → RunCommand "view.sessions.filter"`;`MainWindowViewModel` 注册该命令 → 事件 `SessionFilterFocusRequested`,`MainWindow.axaml.cs` 订阅后 `SidebarHost.FocusTreeFilter()`(侧栏折叠时先展开)。
- `ShortcutCatalog` + 文案 `Sidebar_FilterSessions`、`Cmd_FilterSessions` ×5。

**关键代码**
```csharp
private bool Matches(SessionTreeNodeViewModel node, string q)
{
    if (node.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
    if (!_sessionCache.TryGetValue(node.Id, out SessionProfile? p)) return false;
    return p.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
        || p.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
        || p.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
}

// SyncRows 里 desired 的构造改为:
foreach (SessionTreeNodeViewModel node in Nodes)
{
    if (!HasFilter) { /* 原逻辑 */ continue; }
    if (node.IsGroup)
    {
        var hits = node.Children.Where(c => Matches(c, q)).ToList();
        if (hits.Count == 0 && !node.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
        desired.Add(node);
        desired.AddRange(hits.Count > 0 ? hits : node.Children);   // 组名命中则整组展示
    }
    else if (Matches(node, q)) desired.Add(node);
}
```
进入过滤时记录每个分组的 `IsExpanded` 到 `_expansionBeforeFilter`,过滤态下把命中的组 `IsExpanded = true`(箭头方向正确);清空时按记录恢复——`QuickCommandsViewModel._expansionBeforeSearch` 是同一模式。

**为什么这么改**:过滤在 `SyncRows` 这一层做而不是另建一个 `FilteredRows`:拖放、选中、状态圆点全部绑在 `Rows` 上,换集合等于把这些都再接一遍。

**验证**:`SessionTreeViewModelTests`(`SessionTree` 分类):按主机名过滤只剩命中行且所在组在前;组名命中显示整组;清空后折叠状态恢复;选中项在过滤后若不可见则落到其组。headless:`Ctrl+Shift+E` 焦点落在过滤框。

**风险与回滚**:拖放到被过滤隐藏的组——过滤态下禁用拖放(`AllowDrop = !HasFilter`)。回滚 = 隐藏输入框。

---

### F-05 命令面板无相关度排序

**目标**:结果按分排序,命中字符高亮,最近用过的命令/会话靠前。

**改动清单**
- 新增 `src/VelaShell/ViewModels/PaletteScorer.cs`:`static int Score(string title, string? hint, string query, out (int Start, int Length)[] spans)`。
- 修改 `src/VelaShell/ViewModels/CommandPaletteItem.cs`:加 `Score`、`Highlights`(`IReadOnlyList<(int,int)>`)、`Id`(命令 id 或 `session:{guid}`)。
- 修改 `src/VelaShell/ViewModels/CommandPaletteViewModel.cs` `Rebuild()`(:135):过滤 → 打分 → 按 `(Score desc, Recency desc, 原序)` 排序 → 分组;`ExecuteSelected` 后调用 `_recency.Touch(item.Id)`。
- 新增 `src/VelaShell/Services/PaletteRecency.cs`:`IAppDataStore` 集合 `palette_recency`,文档 `{ id, lastUsedUtc, count }`,内存字典 + 防抖落盘。
- 修改 `src/VelaShell/ViewModels/MainWindowViewModel.cs` `BuildPaletteItems`(:2321):给每个 item 传 `Id`。
- 修改 `src/VelaShell/Views/CommandPaletteView.axaml:167`:标题 `TextBlock` 改为 `Inlines` 绑定(命中段用 `VelaAccent` 加粗),或一个 `HighlightTextBlock` 小控件(放 `VelaShell.Controls`)。

**关键代码**
```csharp
// 分层打分:前缀 > 单词首字母 > 连续子串 > 子序列;越靠前越高;hint 命中减半
public static int Score(string title, string? hint, string q, out (int, int)[] spans)
{
    int idx = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
    if (idx == 0) { spans = [(0, q.Length)]; return 1000 - title.Length; }
    if (MatchesWordStarts(title, q, out spans)) return 800 - spans[0].Item1;
    if (idx > 0) { spans = [(idx, q.Length)]; return 600 - idx; }
    if (Subsequence(title, q, out spans)) return 300 - Gaps(spans);
    if (hint is not null && hint.Contains(q, StringComparison.OrdinalIgnoreCase)) { spans = []; return 100; }
    spans = []; return int.MinValue;
}
// 最终分 = Score + Math.Min(recency.Count, 5) * 20 + (最近 7 天用过 ? 50 : 0)
```

**为什么这么改**:现有 `Fuzzy` 只回答「能不能匹配」,排序靠注册顺序,是可感知的「找不到」;打分算法故意保持四档、可解释,避免引入第三方模糊匹配库。最近使用存 `IAppDataStore` 而不是 `AppSettings`:这是使用痕迹,不该进配置导出。

**验证**:`PaletteScorerTests`:`"st"` 对 `Settings` / `Sftp` / `Trace Route` 的排序为 前缀 > 首字母 > 子序列;`CommandPaletteViewModelTests` 既有用例 + 「执行过的项下次排前」。

**风险与回滚**:高亮控件在 CJK 标题上按字符偏移工作正常(用 `string.IndexOf` 的字符索引,不涉及字形)。回滚 = `Score` 恒返回 0。

---

### U-02 颜色字面量

**目标**:Views 里 27 处 `#` 字面量与三个 C# 固定配色全部改成令牌;新增一条测试禁止回流。

**改动清单**
- 修改 `src/VelaShell/Services/ThemeTokenApplier.cs` `BuildTokens`:新增
  - `VelaAccentPalette0..7`:从 seed 的 `Info / Success / Warning / Magenta / Accent / Yellow / Error / 一个蓝` 派生(亮色主题取同一 seed,本身已过对比度尺子);
  - `VelaSyncChannelA..D` = `Magenta / Info / Warning / Success`;
  - `VelaDangerHover` = `WithAlpha(error, 0xE6)`(关闭钮悬停红);
  - `VelaDropTargetGroup` = `WithAlpha(yellow, 0x20)`、`VelaDropTargetRemove` = `WithAlpha(error, 0x20)`(会话树拖放高亮)。
  - 同步 `VelaShellTokens.axaml` / `VelaTokens.axaml` 的编译期默认值(`ThemeTokenApplierTests` 会逼着对齐)。
- 修改 `src/VelaShell/Converters/SessionStatusToBrushConverter.cs`:三支画刷改 `Application.Current.FindResource("VelaStatusConnected")`——更简单的做法是**删掉转换器**,XAML 里用 `Classes.connected/connecting/disconnected` + 样式 `Setter Fill={DynamicResource VelaStatus*}`(会话树 `SessionTreeView.axaml` 已经是这种写法),两处用它的地方一并改。
- 修改 `src/VelaShell/Services/ConnectionAccent.cs`、`SyncInputChannels.cs`:`BrushFor` 改为返回令牌键名(`string`),XAML `Fill="{DynamicResource {Binding AccentKey}}"` 不可行 → 用 `IBrush` 时通过 `Application.Current!.Resources.TryGetResource(key, theme, out …)` 现取(它们本来就是每次 `BrushFor` 调用返回,不缓存即可跟主题)。
- 修改 XAML 27 处:`TerminalTabView.axaml:391-392` → `VelaAccentForeground`;`MainWindow.axaml:143-151` → `VelaScrim`;`SessionTreeView.axaml:125/131` → `VelaDropTargetGroup/Remove`、`:406` 阴影 → `VelaShadowWindow`;四个窗口的关闭钮 `#E81123` → `VelaDangerHover`;`RecordingPlayerView.axaml:42` → `VelaError`;`ConnectionProfileView.axaml:370/593`、`SessionImportView.axaml:105` → `VelaWarning`(边框)/`VelaError`(文字);`AboutPage` 渐变列入白名单。
- 新增测试 `tests/VelaShell.Tests/Design/NoColorLiteralsTests.cs`:扫描 `src/VelaShell/Views/**/*.axaml`,正则 `="#[0-9A-Fa-f]{6,8}"`,白名单 `AboutPage.axaml`;同样扫 `src/VelaShell/**/*.cs` 的 `Color.Parse("#` 排除 `UiThemeCatalog.cs / ThemeTokenApplier.cs / MainWindow.axaml.cs 的 Scrim*`(后者若改成令牌则也不豁免)。

**为什么这么改**:DESIGN.md §2.0 已经把「六十多个令牌由种子派生」定成规则,这次只是把漏网的补进同一张表;转换器改样式类而不是改转换器取资源,是因为 `IValueConverter` 里取 `Application.Current.Resources` 拿不到主题变体,样式选择器天然跟变体。

**验证**:`ThemeTokenApplierTests` / `UiThemeCatalogTests`(对比度)全绿;`NoColorLiteralsTests` 绿;实机切到 `Sakura` / `GitHub Light` 看树上圆点、标签强调条、同步徽章、重连按钮文字。

**风险与回滚**:`ConnectionAccent` 改为跟主题后,同一会话在不同主题下颜色不同(之前固定 Dracula)——这是期望。回滚 = 逐文件恢复。

---

### Q-05 遗留死代码

**改动清单**:删除 `src/VelaShell.Terminal/ScrollbackBuffer.cs`、`TerminalLine.cs`、`SearchMatch.cs`(若无引用)、`tests/VelaShell.Terminal.Tests/ScrollbackBufferTests.cs`;`ITerminalEmulator.cs:45` 删 `ScrollbackBuffer` 属性;`VelaTerminalControl.cs:708` 删实现。

**为什么**:`ITerminalEmulator.ScrollbackBuffer` 在生产代码里零消费,`new(1)` 只是为了满足接口;留着会误导新人以为回滚存在两份。

**验证**:编译通过、`VelaShell.Terminal.Tests` 全绿。

---

### Q-07 `plan.md` 两处与实现不符

**改动清单**(只改文案)
- `plan.md:186`:`13. ✅**会话标签自定义颜色/图标**…` → `13. ⏳**会话标签自定义颜色/图标**:当前为 `ConnectionAccent` 按 profileId 哈希自动配色(8 色),用户不可选;可选颜色随 F-06 的 `SessionProfile.Terminal.TabColor` 落地。`
- `plan.md:125`:「启动时自动检查 / 自动下载 —— 仍未实现」→ `CheckUpdatesOnStartup` 已于 2026-08-30 随消息中心接线(`MainWindowViewModel.RefreshNotificationSourcesAsync`);`AutoDownloadUpdates` 仍无消费者。

---

### (P-09 撤回)

见 §0.2。`plan-claude.md` 同步改为「撤回」。

---

## 3. 第二批

### P-01 终端输出洪流:单次 Feed 无上限、无背压

**目标**:每帧最多解析 `FeedBudgetBytes`;读线程在积压超过高水位时等待;`cat` 大文件时界面可交互、内存有上限。

**改动清单**
- 修改 `src/VelaShell.Terminal/SshTerminalBridge.cs`:
  - 常量 `FeedBudgetBytes = 1 << 20`、`HighWaterBytes = 8 << 20`、`LowWaterBytes = 2 << 20`;
  - 字段 `long _pendingBytes`、`SemaphoreSlim _drainGate = new(0, 1)`;
  - `EnqueueForFeed`:`Interlocked.Add(ref _pendingBytes, chunk.Length)`;
  - `ReadLoopAsync`:入队后 `if (Volatile.Read(ref _pendingBytes) > HighWaterBytes) await _drainGate.WaitAsync(token)`;
  - `FlushPending`:只摘取累计 ≤ `FeedBudgetBytes` 的块(至少一块);喂完后 `Interlocked.Add(-length)`;若仍有积压 → `Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Background)`(保持 `_flushScheduled = 1`);若降到 `LowWaterBytes` 以下且 `_drainGate.CurrentCount == 0` → `_drainGate.Release()`;
  - `Dispose`:`_drainGate.Release()`(如有等待者)再 `Cancel`,避免读循环卡在门上。
- 可选 `internal` 属性 `PendingBytesForTest`、`LastFlushBytesForTest`。

**关键代码**
```csharp
// FlushPending 摘取阶段
lock (_pendingLock)
{
    int taken = 0;
    while (_pending.Count > 0 && (taken == 0 || taken + _pending[0].Length <= FeedBudgetBytes))
    {
        _draining.Add(_pending[0]); taken += _pending[0].Length; _pending.RemoveAt(0);
    }
    more = _pending.Count > 0;
}
// … Feed …
Interlocked.Add(ref _pendingBytes, -taken);
if (Volatile.Read(ref _pendingBytes) <= LowWaterBytes && _drainGate.CurrentCount == 0) _drainGate.Release();
if (more) Dispatcher.UIThread.Post(FlushPending, DispatcherPriority.Background); else Interlocked.Exchange(ref _flushScheduled, 0);
```
`_pending.RemoveAt(0)` 是 O(n) 但 n 是块数(≤ 8 MB / 16 KB = 512),可接受;要更稳可换 `Queue<PendingChunk>`。

**为什么这么改**
- 续帧用 `Background` 优先级:Avalonia 的 `Render` 优先级高于 `Background`、低于 `Normal`;用默认的 `Normal` 续帧会把渲染饿死,等于没分片。
- 背压走 `SemaphoreSlim` 而不是 `Channel` 有界队列:现有 `_pending + _pendingLock` 已经是队列,只缺一个「满了等一等」,加一扇门最小。
- 不改 `Feed` 语义:合批一次 `Feed` = 一次 `Updated` = 一次重绘的约定原样保留。

**验证**:`TerminalBridgeTests` 新增:① 灌 100 MB(测试替身 `ReadAsync` 每次返回 16 KB)→ 每次 `Feed` 长度 ≤ 1 MB;② `_pendingBytes` 峰值 ≤ 8 MB + 一块;③ 门在 `Dispose` 时被放开(读循环 50 ms 内退出);④ 既有 `UserInput_*` / `DataReceived_*` 不变。实机 `cat /dev/urandom | base64 | head -c 200M` 期间切标签、滚滚动条。

**风险与回滚**:SSH 流控回压会让远端 `cat` 变慢——这就是目的;回滚 = 把三个常量设为 `long.MaxValue`。

---

### P-03 状态栏每秒一次 SSH exec 探测

分两段:A(第二批)是设置与共享;B(第三批)是常驻通道。

**A. 间隔可调 + 失焦降频 + 与资源监视器共享**

改动清单
- `AppSettings.cs` `GeneralOptions`:`StatusMetricsIntervalSeconds`(int,默认 2,允许 1/2/5/10),`Normalize()` 钳制。
- `GeneralSettingsPage.axaml`:加一行下拉(与资源监视器的 1/2/5/10 一致)`SetGen_StatusMetricsInterval`。
- `MainWindowViewModel.StartStatusMetricsPolling`(:1696):`Interval` 取设置;`OnSettingsSaved` 里更新;新增 `SetStatusPollingReduced(bool unfocused)`(失焦 → `Max(interval, 10s)`),由 `MainWindow` 的 `Deactivated/Activated` 调用(与现有 `SetStatusPollingSuspended` 并列)。
- `MainWindowViewModel.OpenResourceMonitor`(:707)/ `ResourceMonitorWindowViewModel`:窗口打开时向主 VM 暴露 `event Action<SessionMetrics> Sampled`;主 VM 在该会话有监视器窗口时**不自采**,改用窗口的采样刷新状态栏(窗口关闭后恢复自采)。

**B. 常驻探测通道**(第三批)

改动清单
- 新增 `src/VelaShell.Infrastructure/Ssh/SessionMetricsStream.cs`:用 `ISshClientWrapper.StreamCommandAsync(command, includeStandardError: false, onLine, ct)` 跑
  ```sh
  while :; do echo __BEGIN__; <SessionMetrics.BuildCommand(scope)>; echo __END__; sleep N; done
  ```
  `onLine` 累积到 `__END__` 时 `SessionMetrics.Parse` + `ApplyDeltas`,通过 `event Action<SessionMetrics>` 推给订阅者;间隔变化 → 取消并重启;会话断开(`client.Disconnected`)→ 结束。
- `ISessionMetricsService` 新增 `IDisposable Subscribe(Guid sessionId, MetricsScope scope, TimeSpan interval, Action<SessionMetrics> onSample)`;`SessionMetricsService` 内按 `(session, scope)` 维护流,最后一个订阅者退订后 30 秒回收。
- 主 VM 状态栏改为订阅;资源监视器窗口也改为订阅(间隔档位 = 重启流)。`GetMetricsAsync` 保留给一次性调用者(插件能力面)。

为什么这么改:每秒一次 `ExecuteAsync` 是「通道握手 + 远端 fork sh + 解释脚本」三笔固定开销,只有第三笔是必要的;一条常驻通道把前两笔归零,而且 EOF 天然等于断线检测。

验证:A:`MainWindowViewModelTests` 计时器间隔随设置变化、失焦降频、监视器打开时不自采。B:`SessionMetricsStreamTests` 用替身 `StreamCommandAsync` 喂两段带分隔符的输出,断言解析两次且差分正确;间隔变更重启;`sshd -d` 实机看通道数。

风险与回滚:B 的远端脚本在 `sleep` 不支持时(BusyBox 支持整数秒)——只用整数;回滚 = 退回轮询实现(接口保留两种)。

---

### P-05 启动路径上的同步 IO

**目标**:先有启动打点;DB 打开与两份文档读取与 Avalonia 初始化重叠;更新收尾只在有标记时扫目录。

**改动清单**
- 新增 `src/VelaShell.Infrastructure/Diagnostics/StartupTrace.cs`:`Mark(string stage)` 记录 `(stage, elapsedMs since process start)`;`Dump()` 在主窗口 `Opened` 后一次性 `Trace.WriteLine` 全部节点(进 U-08 的日志);`VELASHELL_STARTUP_TRACE=1` 时另打到控制台。
- 修改 `src/VelaShell/Program.cs`:
  - `Main` 起点 `StartupTrace.Mark("main")`;
  - 单实例锁拿到后:`StartupWarmup.Begin(paths)` → 后台 `Task` 里 `new SonnetDbEngine(paths)`、`new SonnetDbSettingsService(engine, legacy)`、`GetSettingsAsync()`、`IQuickCommandRepository.LoadAsync()`(它需要 engine + 仓储,构造顺序与 DI 一致);
  - `FinalizePendingUpdate`:`if (!File.Exists(Path.Combine(appDir, ".update-pending"))) return;`(由 `UpdateApplier` 在开始换版时写、`TryFinalizeStartup` 成功后删;首次升级到带此逻辑的版本时标记不存在 → 兜底再扫一次:`if (!markerExists && !legacyScanDone) …`,用 `~/.velashell/update-scan.done` 标记只扫一次)。
- 修改 `src/VelaShell.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs:52-63`:`AddSingleton<SonnetDbEngine>` / `ISettingsService` 改为优先取 `StartupWarmup.Result`(存在则直接注册实例,否则按原样构造——测试与设计器不走预热)。
- 修改 `src/VelaShell/App.axaml.cs`:`ApplyPersistedPreferences`(:505)与 `:195` 的 `GetResult()` 改为 `StartupWarmup.Settings.GetAwaiter().GetResult()`(此时通常已完成,阻塞≈0);`Initialize` 与 `OnFrameworkInitializationCompleted` 各打一个点;`MainWindow.Opened` 打 `first-frame` 并 `Dump()`。

**关键代码**
```csharp
internal static class StartupWarmup
{
    public static Task<(SonnetDbEngine Engine, SonnetDbSettingsService Settings, AppSettings Loaded, QuickCommandLoadResult QuickCommands)>? Result { get; private set; }
    public static void Begin(VelaShellStoragePaths paths) =>
        Result = Task.Run(async () =>
        {
            StartupTrace.Mark("db-open-begin");
            var engine = new SonnetDbEngine(paths);
            var settings = new SonnetDbSettingsService(engine, LegacyDirs(paths));
            AppSettings loaded = await settings.GetSettingsAsync();
            var quick = await new SonnetDbQuickCommandRepository(engine, …).LoadAsync();
            StartupTrace.Mark("db-open-end");
            return (engine, settings, loaded, quick);
        });
}
```

**为什么这么改**
- 先打点:plan 里所有启动数字都是估计,没有数据就分不清 DB 打开、字体加载、XAML 加载哪个是大头。
- 预热放在单实例锁**之后**:第二个实例不该去碰数据库(SonnetDB WAL 独占锁)。
- 快捷命令迁移保持在 UI 之前(注释里的竞态约束),只是挪到后台线程与 Avalonia 初始化并行。

**验证**:`StartupTraceTests`(纯逻辑);`ci` 上 `VELASHELL_STARTUP_TRACE=1` 跑一次 headless 启动用例看节点都在;实机对比改前后 `first-frame` 值(记进 plan.md)。

**风险与回滚**:预热失败(DB 锁)要走原来的 `IsDatabaseLockedFailure` 提示——`Result` 的异常在 `App` 里 `GetResult()` 时抛出,落在同一个 catch;回滚 = 不调用 `Begin`。

---

### F-07 关闭已连接标签无确认

**目标**:设置「关闭已连接标签前确认」(默认开);传输进行中时提示更强。

**改动清单**
- `AppSettings.cs` `GeneralOptions`:`ConfirmCloseConnectedTab`(bool,默认 true);`GeneralSettingsPage.axaml` 加行 `SetGen_ConfirmCloseTab`。
- 修改 `src/VelaShell/Docking/Model/DockWorkspace.cs`:新增 `Func<DockDocument, Task<bool>>? CloseInterceptor { get; set; }` 与 `public void RequestClose(DockDocument document)`:
  ```csharp
  public void RequestClose(DockDocument document)
  {
      if (CloseInterceptor is null) { CloseDocument(document); return; }
      _ = RequestCloseAsync(document);
  }
  private async Task RequestCloseAsync(DockDocument d) { if (await CloseInterceptor!(d)) CloseDocument(d); }
  ```
  `CloseDocument` 保持无条件(供拦截器通过后与程序性关闭使用)。
- 修改 `src/VelaShell/Docking/Controls/DockTabItemBase.cs:79` `CloseTab_Click` → `Workspace?.RequestClose(Document!)`;右键菜单「关闭其他/全部」同样经拦截(逐个询问太吵 → 一次询问「关闭 N 个已连接会话?」:`RequestCloseMany(IEnumerable<DockDocument>)`)。
- 修改 `MainWindowViewModel`:`CloseActiveTab`(:2971)与 `session.close` / `session.close.all` 改走 `RequestClose`;设置 `Layout.CloseInterceptor = ConfirmCloseDocumentAsync`:
  ```csharp
  private async Task<bool> ConfirmCloseDocumentAsync(DockDocument document)
  {
      if (document is not TerminalDocument { Terminal: { IsConnected: true } tab }) return true;
      if (_latestSettings?.General.ConfirmCloseConnectedTab != true) return true;
      bool transferring = FileTransfer.HasActiveTransfersFor(tab.SessionId);   // FileTransferViewModel 新增:按会话查在飞传输
      return CloseConfirmer is null || await CloseConfirmer(tab.Title, transferring);
  }
  public Func<string, bool, Task<bool>>? CloseConfirmer { get; set; }   // 视图注入 MessageDialog.ConfirmAsync
  ```
- `MainWindow.axaml.cs` `OnWindowOpened`:`vm.CloseConfirmer = (title, transferring) => MessageDialog.ConfirmAsync(this, Strings.Get("Main_CloseTabConfirmTitle"), Strings.Format(transferring ? "Main_CloseTabConfirmTransferring" : "Main_CloseTabConfirmBody", title));`。
- 文案 ×5。

**为什么这么改**:关闭入口有四个(标签 ×、Ctrl+W、命令面板、右键菜单),拦截器放在 `DockWorkspace` 这一层才一处管住;`CloseDocument` 保持同步无条件是为了不破坏 `OnDocumentClosed` 现有的同步语义与既有用例。

**验证**:`DockWorkspaceTests`:拦截器返回 false 时文档仍在、`DocumentClosed` 未触发;`MainWindowViewModelTests`:断开的标签不询问、设置关闭不询问;headless:标签 × 弹确认。

**风险与回滚**:`SftpDocument` / `PluginWorkspaceDocument` 走拦截器返回 true(不询问);回滚 = `CloseInterceptor = null`。

---

### F-08 睡眠唤醒 / 网络切换后不主动重连;固定间隔无退避

**改动清单**
- 新增 `src/VelaShell.Infrastructure/Net/ConnectivityMonitor.cs`(`IConnectivityMonitor`:`event Action Resumed`):订阅 `NetworkChange.NetworkAvailabilityChanged`(仅 `IsAvailable == true`)与 Windows 的 `Microsoft.Win32.SystemEvents.PowerModeChanged`(`PowerModes.Resume`,`OperatingSystem.IsWindows()` 守卫);两者合并后 2 秒防抖再触发 `Resumed`。DI 单例,`IDisposable`。
- 修改 `MainWindowViewModel`:注入 `IConnectivityMonitor?`,`Resumed` → `Dispatcher` 上对所有 `ConnectionStatus == Disconnected && !UserRequestedDisconnect && LocalShell is null` 的标签 `ResetReconnectAttempts()` + `_ = ReconnectTabAsync(tab)`。
- 修改 `OnTabDisconnected`(:2756):`delaySeconds = Math.Min(settings.General.ReconnectIntervalSeconds, 1 << Math.Min(tab.ReconnectAttempts - 1, 6))`(1,2,4,8,…封顶设置值);状态栏文案不变。

**为什么这么改**:唤醒后网络往往几秒内恢复,等 keepalive 超时(默认几十秒)再按固定间隔重试,用户看到的是「合盖回来要等一分钟」。`NetworkChange` 是 .NET 自带跨平台事件,不引依赖。

**验证**:`ConnectivityMonitorTests`(注入假事件源);`MainWindowViewModelTests`:`Resumed` 触发对断开标签重连、跳过用户主动断开与本地终端;退避序列 1,2,4,8,8…。

**风险与回滚**:`SystemEvents` 需要消息循环(Avalonia 主线程有);回滚 = 不注册监视器。

---

### F-09 本地文件面板不监听目录变化

**改动清单**
- 修改 `src/VelaShell/ViewModels/LocalFileSystem.cs` `ILocalFileSystem`:`IDisposable? Watch(string directory, Action onChanged)`;实现用 `FileSystemWatcher`(`NotifyFilter = FileName | DirectoryName | Size | LastWrite`,`IncludeSubdirectories = false`),`Error` 事件时静默停用。
- 修改 `LocalFilePaneViewModel.cs`:`NavigateToAsync` 成功后 `RearmWatcher(canonical)`;回调防抖 300 ms → `RefreshSilentlyAsync()`(新增,镜像 `FileBrowserViewModel.RefreshSilentlyAsync` 的版本检查 + `ListingUnchanged` + 保留选择);`Dispose` 释放。
- 修改 `MainWindowViewModel` 的本地面板生命周期处调用 `Dispose`。

**为什么这么改**:与远端面板对称(远端已经有静默刷新);网络盘上 `FileSystemWatcher` 可能失败,所以失败即退化为现状,不报错。

**验证**:`LocalFilePaneViewModelTests` 注入假 `ILocalFileSystem`:触发 `onChanged` 后列表更新、选中保留、导航中途的回调被丢弃。

---

### F-10 内置远程编辑器缺查找替换、编码选择、自动换行、大文件保护

**改动清单**
- 新增 `src/VelaShell.Core/Models/TerminalEncodings.cs`:把 `SettingsViewModel.cs:566` 的 `AvailableEncodings` 数组搬过来作为 `TerminalEncodings.All`,设置页与编辑器共用。
- 修改 `src/VelaShell/Views/RemoteFileEditorView.axaml`:头部工具区加 `ComboBox`(编码,`ItemsSource="{x:Static TerminalEncodings.All}"`)、`ToggleButton`(自动换行)、`TextBlock`(LF/CRLF);`Editor` 加 `WordWrap="{Binding #WrapToggle.IsChecked}"`。
- 修改 `RemoteFileEditorView.axaml.cs`:
  - 构造函数 `SearchPanel.Install(Editor.TextArea)`,并给 `SearchPanel` 套 Vela 令牌(AvaloniaEdit 的面板用自己的默认色;当前仓库没有 AvaloniaEdit 的换肤文件,`RemoteFileEditorView.axaml` 是内联画刷 —— 新增 `Themes/AvaloniaEditThemes.axaml`,放一组 `Style Selector="ae|SearchPanel …"`,在 `App.axaml` 引入);`Ctrl+H` 打开替换;
  - 新增 ctor 参数 `Encoding fallback`(调用方传会话编码);
  - `LoadFileAsync`:先 `FileInfo.Length > 5 MiB` → `MessageDialog.ChooseAsync`(只读打开 / 用外部编辑器 / 取消);`DetectEncoding`:BOM → 否则 `new UTF8Encoding(false, throwOnInvalidBytes: true)` 严格解码,失败用 `fallback`;保留 `_bytes` 供切换编码时重新解码;
  - 编码切换:未修改直接重解码;已修改先确认;`SaveAsync` 用当前编码,CRLF 检测结果用于保存时保持原行尾(`Editor.Options`/文本替换)。
- 文案:`Editor_Encoding`、`Editor_WordWrap`、`Editor_LargeFileTitle/Body/ReadOnly/External` ×5。

**为什么这么改**:GBK 文件按 UTF-8 解码再原样存回是**破坏性**的(每个无效序列变 U+FFFD),这是这一项里唯一的 P0 级子项,所以「严格解码 + 回退会话编码」先做;查找面板是 AvaloniaEdit 自带能力,只是没装。

**验证**:`RemoteFileEditorEncodingTests`(纯逻辑抽到 `EditorEncodingDetector` 静态类):BOM / 合法 UTF-8 / GBK 字节 三种输入的判定;headless:打开 6 MB 文件弹选择框。

**风险与回滚**:严格解码对「UTF-8 里夹二进制」的文件会退到会话编码——用户可在下拉切回;回滚 = 恢复 BOM-only 检测。

---

### U-01 无障碍与键盘可达性

**改动清单**
- 新增 `src/VelaShell/Behaviors/A11y.cs`:附加属性 `NameFromToolTip`(bool);属性变更时若 `AutomationProperties.GetName(control)` 为空且 `ToolTip.GetTip(control)` 是 `string`,则 `AutomationProperties.SetName`。
- 修改 `src/VelaShell/App.axaml`(或 `Themes/ButtonThemes.axaml`):`<Style Selector="Button, ToggleButton, RepeatButton"><Setter Property="(b:A11y.NameFromToolTip)" Value="True"/></Style>` —— 一条样式覆盖全部图标按钮。
- 18 个 `Window` 视图:`Opened` 时 `FocusFirstInput()`(第一个 `TextBox`/`Button`);未有 `Esc` 关闭的加 `KeyBinding Escape → CancelCommand`;`MessageDialog` 的默认按钮 `IsDefault/IsCancel`。
- 修改 `VelaTerminalControl.cs`:`protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);`(`ControlAutomationPeer` 子类:`GetAutomationControlTypeCore = Document`、`GetNameCore = 宿主标签标题(新增 AccessibleName 属性)`、`GetClassNameCore = "Terminal"`)。
- 新增测试 `tests/VelaShell.Tests/Design/AutomationNamesUiTests.cs`:headless 实例化每个 View,遍历 `Button/ToggleButton`,断言 `AutomationProperties.Name` 或 `Content` 文本非空。

**为什么这么改**:116 处 `ToolTip.Tip` 说明「图标按钮的名字其实都写过了」,只是写在读屏器读不到的地方;附加行为让这份文案一次性对无障碍生效,不用逐个补 14 → 130 处属性。

**验证**:新增测试;Windows Narrator 实机过一遍主窗口与连接对话框。

**风险与回滚**:样式对所有按钮生效,行为只在 Name 为空时写入;回滚 = 删样式。

---

### U-04 设置窗口一次实例化全部 12 页

**改动清单**
- 修改 `src/VelaShell/Views/SettingsView.axaml:236-250`:`Panel` 里 12 个页面换成 `<ContentControl Content="{Binding SelectedSectionKey}" ContentTemplate="{StaticResource SettingsPageSelector}"/>`。
- 新增 `src/VelaShell/Views/Settings/SettingsPageSelector.cs`:`IDataTemplate`,`Build(object key)` 按 `SettingsSectionKey` `switch` 创建对应 `UserControl`,用 `Dictionary<SettingsSectionKey, Control>` 缓存(切回时保留滚动位置与展开状态),`DataContext` 由 `ContentControl` 继承(页面现在也是继承 `SettingsViewModel`)。
- 修改 `SettingsViewModel`:`SelectedSectionIndex`(:513)旁加 `SelectedSectionKey`(由索引映射),两者同步。

**为什么这么改**:`IsVisible` 切换的写法让 12 页控件树在窗口打开时全部构建;`IDataTemplate + 缓存` 保留了「切回不丢状态」这一点,`TransitioningContentControl` 可作动画替代,不影响结构。

**验证**:headless `SettingsUi` 用例:打开窗口后 `SettingsView` 视觉树里只有一个页面;切换两次后仍是同一实例(`ReferenceEquals`)。

**风险与回滚**:页面里若有依赖「其它页已实例化」的名称引用(如 `#SomeControl` 跨页绑定)会断——搜索 `ElementName=` 跨页引用为零;回滚 = 恢复 Panel。

---

### U-05 状态栏信息密度低、不可点击

**改动清单**
- `StatusBarViewModel`:新增 `EncodingLabel`、`TerminalTypeLabel`、`GridLabel`(`200×50`)、`SelectionLabel`(有选区时「已选 N 字符」);命令 `OpenResourceMonitorCommand`、`ChangeEncodingCommand<string>`。
- `MainWindowViewModel.UpdateStatusBarForActiveTab`:填 `tab.EncodingName` / `tab.TerminalTypeName`;订阅 `ActiveTerminalControl` 的尺寸变化(`Emulator.Resize` 处触发新增事件 `GridChanged`)与 `SelectionChanged`(新增)刷新两段;`ChangeEncoding` → `control.SetEncoding(ResolveEncoding(name))` + `tab.EncodingName = name`(仅本会话,不持久化;F-06 落地后可存入覆盖项)。
- `StatusBarView.axaml`:右侧加三段可点击 `Button.statusbar-chip`(编码用 `MenuFlyout` 列 `TerminalEncodings.All`),CPU/内存指标外包 `Button` → `OpenResourceMonitorCommand`。
- 文案 `Status_Selected`(`{0}`)×5。

**为什么这么改**:状态栏是终端类产品放「当前会话事实」的地方(Xshell/SecureCRT 都在这里显示编码与尺寸);编码热切现有 `SetEncoding` 已支持。

**验证**:`StatusBarViewModelTests`;实机切编码后中文 `ls` 立刻正常。

---

### U-06 终端链接无悬停反馈

**改动清单**
- 修改 `VelaTerminalControl.cs`:
  - `OnPointerMoved`(:3031)末尾:`UpdateLinkHover(e)`:仅当 `e.KeyModifiers.HasFlag(Control)`;取 `(row, col)`;行文本与上次相同则复用缓存的 `SemanticMatcher.Match` 结果;命中 `Url`/`IpAddress` → `Cursor = _handCursor`(静态 `new Cursor(StandardCursorType.Hand)`)+ `ToolTip.SetTip(this, url)` + `_hoverLink = (row, span)`;否则复位。
  - `OnKeyUp`/`OnPointerExited`/`OnLostFocus`:复位光标与提示。
  - 右键:`OnPointerPressed` 右键且悬停在 IP 上 → `ContextMenu`:「复制」「用此地址新建连接」「路由追踪」(后两项经事件 `LinkActionRequested(kind, text)` 交宿主)。
- `MainWindowViewModel` 订阅 `LinkActionRequested` → `NewConnectionRequested`(预填主机)/ `TraceRouteRequested`。

**为什么这么改**:`SemanticMatcher.UrlAt` 已是 Ctrl+点击用的判定函数,悬停复用它,不新增第二套识别;只在 Ctrl 按下时算,避免每次移动都做匹配。

**验证**:headless:Ctrl 按下悬停 URL 格 → `Cursor.Hand`;松开 → 默认。

---

### U-07 非虚拟化列表

**改动清单**:`NotificationPanelView.axaml:168`、`CommandPaletteView.axaml:148-153`、`ResourceMonitorWindow.axaml:1300 / 1127 / 1656` 五处 `ItemsControl` → `ListBox`(`Background="Transparent"`,`ListBoxItem` 样式压掉选中/悬停底色,或保留用于键盘导航);命令面板改为**单列表 + 分组头项**(`CommandPaletteRow` 抽象:`GroupHeader | Item`),`SelectedItem` 沿用。

**为什么**:`ListBox` 自带 `VirtualizingStackPanel`;命令面板的嵌套 `ItemsControl` 让键盘选中项无法用 `ScrollIntoView`,扁平化后这个问题一起解决。

**验证**:既有 `CommandPalette` 用例;1000 条会话时按键无卡顿(手测)。

---

### U-09 连接对话框字段校验的内联提示

**改动清单**
- `ConnectionProfileViewModel`:新增只读 `HostError / PortError / UsernameError / KeyPathError`(`ObservableAsPropertyHelper`,由对应字段 `WhenAnyValue` 派生;私钥路径非空且文件不存在 → 错误);`canExecute`(:190)追加 `KeyPathError is null`。
- `ConnectionProfileView.axaml`:每个字段下加 `TextBlock Classes="field-error" IsVisible="{Binding XError, Converter=IsNotNullOrEmpty}"`,`Foreground={DynamicResource VelaError}`。
- 文案 `Profile_ErrHostRequired / ErrPortRange / ErrUserRequired / ErrKeyMissing` ×5。

**为什么**:门槛已经有,用户只看到按钮灰掉;把「为什么灰」写在字段旁边即可,不引入 `INotifyDataErrorInfo`(那会改绑定模式并影响 §40 的数字框守门)。

---

### Q-04 `async void` 与静默 catch

**改动清单**
- 新增 `src/VelaShell/Services/FireAndForget.cs`:`static void Run(Func<Task> action, [CallerMemberName] string? site = null)`,异常 → `Trace.WriteLine($"[FireAndForget] {site}: {ex}")`;`MainWindow.SafeFireAndForget`(:1668)删除改用它。
- 33 处 `async void` 事件处理器改为 `private void X_Click(...) => FireAndForget.Run(() => XAsync(...))`;`VelaTerminalControl.cs:826` 与 `:3225` 同样。
- 18 处 `catch { }` 加一行 `Trace.WriteLine` 带上下文(保留吞异常语义)。
- 顺带:`SshTerminalBridge.Dispose` 里的 `Wait` 改 `DisposeAsync`(P-09 撤回后的整洁项;`TerminalTabViewModel` 调用处已在后台线程,行为不变)。

**验证**:`FireAndForgetTests`:异常被记录且不上抛。

---

### Q-06 偶发失败与慢测试

**证据**(第二轮 trx):`RunAsync_RereadsTheAiSettingsEveryTurn` 8.3 s、`RunAsync_NamesTheModelWhenTheProviderRejectsTheCall` 8.2 s、`Fetch_AllowListEntry_MayIncludeSchemeAndTrailingSlash` / `Fetch_AllowsAnExplicitlyListedInternalHost` / `Fetch_ConfiguredSearxngHost_PassesTheGuardWithoutBeingAllowListed` 各 7.3 s、`Backspace_RemovesTheWholeAcceptedReference_WithoutAnotherListing` 4.7 s。

**改动清单**
- 三个 `Fetch_*`:它们只验证「放行/拦截」的判定,却真的发起了 HTTP(命中不可达主机后靠超时结束)→ 给 `WebFetchTool`(或对应类型)注入 `HttpMessageHandler`,测试用返回 `200` 的假 handler;判定逻辑单独抽成纯函数用例。
- 两个 `RunAsync_*`:8 s 疑似真实重试退避 → `AgentLoop` 注入 `TimeProvider`/`Func<TimeSpan, Task> delay`,测试传零延时。
- `Backspace_*` 4.7 s:UI 用例里 `Task.Delay` 等防抖 → 用 `DispatcherTimer` 可控时钟或把防抖时长做成可注入。
- 偶发失败:CI(Q-03)固定产出 trx,失败用例名进 issue;对 `Infrastructure.Tests` 里含 `Task.Delay`/`Stopwatch` 断言的用例加 `[Timeout]` 与更宽松的时间窗。

**验证**:`VelaShell.Plugin.Ai.Tests` 总时长目标 < 40 s。

---

## 4. 第三批

### P-07 回滚缓冲整行满宽存储;设置无钳制

**改动清单**
- 修改 `src/VelaShell.Terminal/Emulation/TerminalRow.cs`:
  - 字段 `_cells` 变为**物理容量可小于逻辑列数**:新增 `int _columns`(逻辑宽度,`Columns => _columns`);
  - 索引器 `get`:`(uint)col < (uint)_cells.Length ? _cells[col] : TerminalCell.Empty`;`set` 与 `CellRef`:`EnsureCapacity(col + 1)`(按逻辑宽度一次扩到位);
  - 新增 `void Compact()`:`int keep = LastOccupied() + 1; if (keep < _cells.Length) Array.Resize(ref _cells, keep);`
  - `Span` 仍返回物理段;`Fill/FillRange/Resize/DeleteCells/InsertCells` 先 `EnsureCapacity(_columns)`。
- 修改 `TerminalScreen.ScrollUp`(:220):`_scrollback.Add(retired)` 之前 `retired.Compact()`;`ReflowResize`/`EmitLogicalLine` 读 `row.Span` 处按 `row.Columns` 语义补齐(尾部本就是空格,可直接接受更短的 span)。
- 修改 `AppSettings.Normalize()`(:76):钳制 `ScrollbackLines ∈ [100, 200000]`、`TerminalFontSize ∈ [6, 40]`、`DefaultPort ∈ [1, 65535]`、`General.ConnectTimeoutSeconds ∈ [1, 600]`、`KeepAliveSeconds ∈ [0, 3600]`、`MaxRetries ∈ [0, 100]`、`ReconnectIntervalSeconds ∈ [1, 300]`、`Transfer.MaxConcurrentTransfers ∈ [1, 16]`(与各页 `NumericUpDown` 的 `Minimum/Maximum` 取同一组常量,新建 `SettingsLimits` 静态类)。
- 可选:`TerminalSettingsPage` 回滚项旁「≈ xx MB / 标签(按 200 列估)」。

**为什么这么改**:回滚行退休后只读,裁掉尾部空白是无损的;活动屏行保持满宽,`CellRef` 的热路径不受影响(退休前不 Compact)。不做压缩/分页是因为 16 字节格 + 裁尾已经能把典型日志的内存降一个量级。

**验证**:`TerminalCellMemoryTests` 加「1 万行 × 20 字符 × 200 列」堆内存断言(`GC.GetTotalMemory`,阈值留 2×);`TerminalScreenTests` 的 reflow/选区/搜索/折叠全绿;`AppSettingsNormalizeTests` 钳制。

**风险与回滚**:任何对退休行 `Span.Length == Columns` 的隐含假设——grep `.Span` 只有 reflow 与 `CopyTextTo`;回滚 = `Compact()` 空实现。

---

### P-08 VT 解析器 Ground 态批量打印

**改动清单**
- `IVtActions.cs`:`void PrintRun(ReadOnlySpan<char> text) { foreach (char c in text) Print(c); }`(默认实现,测试替身零改动)。
- `VtParser.Parse`:
  ```csharp
  for (int i = 0; i < text.Length; )
  {
      if (_state == State.Ground && !Vt52Mode)
      {
          int j = i;
          while (j < text.Length && text[j] is >= (char)0x20 and <= (char)0x7E) j++;
          if (j > i) { actions.PrintRun(text.Slice(i, j - i)); i = j; continue; }
      }
      … 原逐字符路径 …
  }
  ```
- `TerminalEmulator.PrintRun`:快路径条件 `!_decGraphics[gl] && _singleShift < 0 && !Modes.InsertMode`;按 `Columns - CursorX` 切段,每段 `Screen.CellRef` 直接写(复制 `_fg/_bg/_flags`),段末按 `AutoWrap` 走现有 `_pendingWrap` 规则;不满足条件退回逐字符 `Print`。

**验证**:`VtParserTests` 属性式用例:随机 ASCII/控制符混合串,`PrintRun` 路径与逐字符路径的屏幕网格逐格相等;P-10 基准记录吞吐。

---

### P-10 性能基准工程

**改动清单**
- 新增 `tests/VelaShell.Benchmarks/VelaShell.Benchmarks.csproj`(`OutputType=Exe`,`BenchmarkDotNet`,`IsTestProject=false`,不进 `dotnet test`);基准:`VtParserBench`(ASCII / ANSI 密集 / CJK 各 10 MB)、`TerminalScreenBench`(滚动 10 万行、reflow)、`RenderLineBench`(headless `RenderTargetBitmap` 一屏)、`BufferSearchBench`、`SessionMetricsParseBench`、`BridgeBatchBench`。
- `scripts/bench.ps1`:`dotnet run -c Release -- --job short --memory`,结果 markdown 复制进 `plan.md` 对应节。
- `src/Directory.Packages.props`:`BenchmarkDotNet` 版本。

**为什么**:P-01/P-02/P-07/P-08 都要用数字验收;`--job short` 十几秒能跑完,足够做前后对比。

---

### F-06 会话级覆盖项与标签颜色

**改动清单**
- `SessionProfile.cs`:新增
  ```csharp
  public SessionTerminalOverrides? Terminal { get; set; }
  public List<Guid> AutoStartTunnelIds { get; set; } = [];
  public SessionProfile Clone() { … 全字段 … }
  public sealed class SessionTerminalOverrides { string? Encoding; string? TerminalType; string? ColorScheme; string? TabColor; string? StartupDirectory; int? KeepAliveSeconds; }
  ```
- 四处手写拷贝改 `Clone()`:`ConnectionProfileViewModel.cs:1009`、`SonnetDbSessionRepository.cs:150`、`ConnectionWorkflowService.cs:158`、`SessionTreeViewModel.cs:633`。
- 新增测试 `SessionProfileCloneTests`:反射遍历 `SessionProfile` 全部可写公共属性,给随机值后 `Clone()` 逐属性相等 —— 以后加字段漏拷贝直接红。
- `ConnectionProfileViewModel/View`:高级选项区(仅 SSH)加 编码 / 终端类型 / 配色 / 标签颜色(8 色 + 无)/ 初始目录 / 心跳。
- 消费点:`ConfigureTerminal`(:4171)与 `CreateConnectingTab` 里 TERM 协商取 `profile.Terminal?.TerminalType ?? settings.TerminalType`;编码同理;`StartupDirectory` 并入 `BuildStartupCommand`(`cd` 一次);`ConnectionAccent.BrushFor(profile)` 优先 `TabColor`;`AutoStartTunnelIds` 在连接成功后交 `TunnelPanelViewModel` 启动。
- Gist 同步:`SessionProfile` 是整体序列化,自动带上;`SessionImport`(Xshell/WinSCP)按需填。

**为什么**:字段收在一个子对象里而不是平铺,是为了「null = 跟随全局」语义清楚,也不再让 `SessionProfile` 每加一个覆盖项就多一列;`Clone()` + 反射测试是 plan §37 那次「五处都不能漏」的根治。

---

### F-11 键盘交互式认证

**结论**:Tmds.Ssh 0.24 无此凭据类型,本仓库做不了。

**改动清单**
- velashell-docs 记录评估结论;向 Tmds.Ssh 提 issue / PR 跟进(`KeyboardInteractiveCredential(Func<KeyboardInteractivePrompt, ValueTask<string[]>>)`)。
- 本仓库:`TmdsSshInterop.Translate` 对「服务器仅接受 keyboard-interactive」的认证失败给出专门异常文案 `Msg_KbdInteractiveUnsupported`(连接诊断中心同样显示),不再是笼统的认证失败。
- 预留:`AuthenticationDialogView` 的两步流程已能承载「逐条提示」,SDK 一到即可接。

---

### U-10 统一 toast

**改动清单**
- 新增 `src/VelaShell/ViewModels/ToastCenterViewModel.cs`(`Show(ToastSeverity, string title, string? body, ToastAction? action, TimeSpan? ttl)`,最多 3 条堆叠,Error 不自动消失)与 `Views/ToastHostView.axaml`(挂在 `MainWindow.axaml` 主区域右上,`IsHitTestVisible` 只对卡片);样式复用 `FileTransferView` 的浮层卡片。
- `StatusBar.Status` 的 54 处写入分三类迁移:断线 / 自动重连倒计时 / 安全告警 / 连接失败 → toast(Warning/Error,并镜像进消息中心);复制成功 / 导出完成 → toast(Info,2 s);「就绪」类静态文案留在状态栏。
- `MainWindowViewModel` 新增 `Toasts` 属性;`securityAlertService.Alerted` 与 `OnTabDisconnected` 改写。

**为什么**:一个文本槽承载所有反馈,后写覆盖先写;toast 分级、可堆叠、可点击,和消息中心形成「即时 / 历史」两层。

---

### Q-01 God 类拆分

**第一步(零行为变化,只搬文件)**:`MainWindowViewModel` 拆成 partial:`.Connections.cs`(`TryConnect* / Reconnect* / Teardown / Handshake / PostAuth`)、`.Documents.cs`(SFTP/FTP/插件文档开关、关闭任务)、`.Metrics.cs`(轮询与 tooltip)、`.TerminalSettings.cs`(`ApplyLive* / Configure* / Gutter / FontSize`)、`.Notifications.cs`、`.Commands.cs`(`RegisterCommands`)。`PluginManager` 同法拆 `.Install.cs / .Trust.cs / .Activate.cs / .Dev.cs`。
**第二步(逐个 PR)**:把 `.Metrics.cs` 抽成 `StatusMetricsPoller`(P-03 顺手)、`.TerminalSettings.cs` 抽成 `TerminalSettingsApplier`、`TerminalTabView` 的提示判定(`IsInteractivePrompt` 一族 + 防抖)抽成 `SuggestionController`(纯逻辑,补单测)。

**验证**:第一步靠编译与全量测试;第二步每个抽出的类补自己的单测。

---

### Q-02 `TabBarViewModel` 与 `DockWorkspace` 双模型

**改动清单**
- `DockWorkspace`:新增 `event Action<DockDocument> DocumentAdded`、`ActivateNext()` / `ActivatePrevious()`(活动组内循环)。
- `MainWindowViewModel`:28 处 `TabBar.*` 改为 `Layout`:`TabBar.Tabs` → `TerminalTabs => Layout.AllDocuments().OfType<TerminalDocument>().Select(d => d.Terminal)`;`ActiveTab` 订阅 → `Layout.ActiveDocumentChanged`(已有 `SetActiveFromDocument`);`OnTabsCollectionChanged` 的状态合并 → `DocumentAdded/Removed`。
- 其它 8 处(`App.axaml.cs:455`、`HostSessionOpener.cs:70`、`MainWindow.axaml.cs:443/914/980`、`TerminalTabView.axaml.cs:1151/1154`、`MainWindow.axaml:31-32`)同法;`Ctrl+Tab` 绑到 `RunCommand "tab.next"`。
- 删除 `TabBarViewModel`、`TabViewModel` 只保留为 `TerminalTabViewModel` 的基类或内联。

**为什么**:§24 / §39 同形 bug 修了两次,根因就是两份「标签集合」各自维护状态;单一事实后这一类 bug 没有生存空间。

**验证**:全量 `TerminalTab` / `Docking` / `SessionTree` 分类用例;headless `Ctrl+Tab` 在分屏下只在活动组内循环。

---

## 5. 需要先拍板的决策

1. **跳标签手势**:`Ctrl+Alt+1…9`(与 Windows Terminal 一致,不占终端控制字符;AltGr 键盘上可能与符号冲突)vs `Ctrl+1…9`(与浏览器一致,但吃掉 `Ctrl+2…7` 六个控制字符)。本文按前者写。
2. **`Ctrl+-` 缩小字号** 会抢走 `^_`(emacs undo 的一种按法);保留 `Ctrl+Shift+-` 送 `^_`。接受否?
3. **分屏手势** `Ctrl+Shift+D`(右)/ `Ctrl+Shift+S`(下),会话树过滤 `Ctrl+Shift+E`。
4. **P-04 快照的接口形态**:默认接口实现(本文方案,测试替身零改动)vs 抽象成员(强制所有实现)。
5. **F-07 默认值**:「关闭已连接标签前确认」默认开(本文)还是默认关。
6. **P-03(B)** 是否要做常驻通道,还是 A 段(间隔可调 + 共享)够用——建议先做 A,拿 `sshd` 日志与远端负载再定。
7. **U-02 转换器**:删掉 `SessionStatusToBrushConverter` 改样式类(本文)还是保留转换器改取资源。
