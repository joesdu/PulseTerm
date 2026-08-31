# VelaShell 项目进展与参考文档

> 本文件记录已完成的工作、当前架构、关键文件索引与后续待办,供后续开发参考。
> 最近更新:**2026-08-14**(新增 §18:2026-07-24 ~ 08-14 批次盘点 —— 插件系统 v1 + AI 助手插件、系统资源监控、路由追踪+离线归属地、连接诊断中心、远端任务管理器、Xshell/WinSCP 会话一键迁移、FTP/FTPS、全局网络代理、MSIX 商店版等;§6/§7/§10/§12 状态全面勘误:已完成项补 ✅,确认与当前架构/产品决策冲突的项标 ❌ 不实现)。
> 上一版:2026-07-22(§17:SSH 传输层由 SSH.NET 迁移到 Tmds.Ssh、自研 ZMODEM(rz/sz)、独立 SFTP 标签与本地/远程双栏、SFTP 断点续传、远程文件编辑器语法高亮;§1/§2/§3/§4/§7/§10/§12 按现状勘误)。
> 上一版:2026-07-14(§16:自研 VelaDock 落地合并、主窗自绘无边框标题栏补齐 Win11 原生贴靠手感(WndProc)、终端行号/时间侧栏、本地终端 Job Object 秒杀进程树、Windows MSI 安装包与自定义安装目录、集中式包管理、Avalonia 12.1.0、全项目 XML 注释与每项目 README)。
> 上一版:2026-07-12(§13:设置审计整改三批完成;新特性 —— 主机指纹三选项确认与已信任主机管理、GitHub Gist 云同步、会话录制与回放、支持与捐赠页、双许可 AGPL-3.0 + 商业授权、终端配色随主题联动;测试计数见 §7)。

## 1. 技术栈现状

| 项       | 版本/说明                                                                                                                                            |
| -------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET     | **net11.0**(2026-07 由 net10.0 切入;`global.json` 锁 `11.0.0` + `rollForward: latestFeature`,实际以 `11.0.100-preview.x` 构建。`Directory.Build.props` 对 net11 开启 `EnablePreviewFeatures` + `Features=runtime-async=on`,并 `NoWarn` 掉 CA2252/SYSLIB5007;`LangVersion=preview`) |
| UI 框架  | **Avalonia 12.1.0**(已从 11.x → 12.0.5 → 12.1.0)                                                                                                     |
| MVVM     | ReactiveUI 23.2.28 / ReactiveUI.Avalonia 12.0.3                                                                                                      |
| 停靠框架 | **自研 VelaDock**(`src/VelaShell/Docking/`,零第三方依赖;已替换 Dock.Avalonia,见 `velashell-docs zh/host/dock-replacement-plan.md`)                                     |
| SSH/SFTP | **Tmds.Ssh 0.23.0**(全托管 async-first;2026-07 由 SSH.NET 迁入,库类型只在 `Infrastructure/Ssh/` 出现,异常经 `TmdsSshInterop` 翻译为 `VelaSsh*Exception`) |
| 持久化   | **SonnetDB.Core 3.0.1 嵌入式多模型数据库**(`~/.velashell/sonnetdb`;文档集合 + 时序 measurement;旧 JSON 首次运行一次性导入;LiteDB 已移除) |
| 打包     | 便携压缩包(zip / tar.gz,6 RID)+ 自研应用内自更新(GitHub Releases `latest.json`;Velopack 已移除 2026-07-17;WiX MSI 定义保留但不随 CI 发布)            |
| 依赖管理 | **集中式**:`src/Directory.Packages.props` 统一 NuGet 版本(`ManagePackageVersionsCentrally`);SourceLink.GitHub 构建期启用                             |
| 测试     | **MSTest 4.3.2**(已从 xUnit 全量迁移;FluentAssertions 已移除)                                                                                        |

## 2. 解决方案分层

```
src/
├── VelaShell/                桌面入口、DI 组合根、视图(axaml)、App 层 ViewModel、停靠、行为
├── VelaShell.Presentation/   跨层 ViewModel、连接/隧道工作流服务
├── VelaShell.Controls/       自定义控件(LucideIcon)与设计 token(VelaTokens/VelaShellTokens/Icons)
├── VelaShell.Terminal/       ★ 自研 VT 终端引擎 + 自绘渲染控件 + ZMODEM 路由
├── VelaShell.Core/           领域模型、抽象契约、数据存储、SSH/SFTP 封装接口、ZMODEM 协议引擎、本地化
└── VelaShell.Infrastructure/ Tmds.Ssh/SFTP/隧道实现、SonnetDB 持久化、存储路径、DI 扩展
tests/  6 个 MSTest 项目(见 §7)
解决方案文件:仓库根目录 VelaShell.slnx(注意:曾在 src/ 下,VS 打开后移到了根目录)
```

## 3. 自研终端引擎(核心,替换了坏掉的 AvaloniaTerminal)

彻底移除第三方 `AvaloniaTerminal 1.0.0-alpha.7`,改为手写 VT 引擎。位于 `src/VelaShell.Terminal/Emulation/` 与 `Rendering/`:

- `VtParser.cs` — Paul Williams DEC ANSI 状态机(Ground/Escape/CSI/OSC/DCS…)+ 独立 VT52 语法路径;消费 Unicode 标量,派发到 `IVtActions`。
- `TerminalScreen.cs` + `TerminalRow/TerminalCell/CellFlags/TerminalColor` — 网格、主/备屏、滚动区域(DECSTBM)、scrollback、光标、tab stops。
- `TerminalEmulator.cs` — 仿真器大脑(实现 `IVtActions`):SGR(16/256/truecolor)、光标/擦除/插删行列、模式(DECAWM/DECOM/应用键盘/插入/括号粘贴/鼠标跟踪…)、DEC 线绘字符集、DA/DSR 应答、备用屏。
- `TerminalType.cs` — **vt52/100/102/220/320/340/420/520/xterm/xterm-256color** 十种 profile,各自 TERM 名 + Device Attributes 应答;`FromTermName`/`ToTermName`;**xterm-256color 为默认**。
- `Utf8Sink.cs` — 增量解码,**可配置任意编码**(UTF-8 默认,GBK/Big5 等);`CharWidth.cs` — wcwidth(CJK 双宽);`TerminalPalette.cs` — 256 色 + 设计稿 term-\* 配色;`Charsets.cs` — DEC 线绘映射;`InputEncoder.cs` — 按键→字节(应用光标键、xterm 修饰键、VT52)。
- `Rendering/VelaTerminalControl.cs` — 纯自绘 Avalonia `Control`:glyph 渲染、光标、选区、滚轮回溯、剪贴板(含括号粘贴);**同时实现旧 `ITerminalEmulator` 接口**以无缝接回 `SshTerminalBridge` 与视图。默认网格 120×32;`ApplyLayoutSize` 拒绝 <2 列/行的早期布局(修过"横幅每字一行"bug)。

## 4. SSH / PTY

- `SshTerminalBridge` 只读循环,**不再向 shell 预写 `\n`**(修过"末行提示符重复"bug)。
- **PTY 实时改窗**:`IShellStreamWrapper.Resize` → Tmds.Ssh `RemoteProcess` 的终端窗口尺寸变更(实现见 `Infrastructure/Ssh/ShellStreamWrapper.cs`);`ITerminalEmulator.PtySizeChanged(cols,rows)` 由控件布局时抛出,`TerminalTabViewModel` 后台线程转发给 PTY。
- **连接失败不崩溃**:`MainWindowViewModel.TryConnectProfileAsync` 捕获认证/网络/超时异常,映射中文提示写入状态栏 + `LastConnectionError`;交互式连接失败弹错误对话框。`Program.cs` 装了 `TaskScheduler.UnobservedTaskException`/`AppDomain.UnhandledException` 兜底。
- **连接持久化**:`ConnectionWorkflowService.SaveProfileAsync`→`SonnetDbSessionRepository`(SonnetDB `session_profiles` 集合,密码 AES-256 加密);`MainWindowViewModel.InitializeAsync` 启动时加载侧栏"最近连接"(SonnetDB `conn_history` 时序)与会话树;侧栏最近项**双击重连**;命令面板也可连。
- **新建连接密码框仅限 ASCII**:`Behaviors/AsciiOnlyInput.cs` 拦截 IME/中文 TextInput + VM setter 剥离粘贴的非 ASCII。

## 5. 停靠 / 分屏(自研 VelaDock,已替换 Dock.Avalonia)

- **模型层** `Docking/Model/`(纯 INPC,可单测):`DockWorkspace`(结构操作 + `DocumentClosed`/`ActiveDocumentChanged` 事件)、`DockGroup`(标签组,主组不折叠)、`DockSplit`(分栏树)、`DockDocument`;空的次级组自动折叠、单子分栏自动提升。方案与集成面分析见 `velashell-docs zh/host/dock-replacement-plan.md`。
- **控件层** `Docking/Controls/`:`DockWorkspaceControl`(按树渲染 Grid+GridSplitter,star ↔ Proportion 回写;**按文档缓存视图**,切标签复用同一 `TerminalTabView`,取代原 ControlRecycling)、`DockGroupControl`(标签条 + 溢出三连钮 + 标签列表下拉)、`DockTabItem`(标签视觉 + 右键菜单:关闭系列/水平垂直拆分/标签位置)、`DockDragController` + `DockDropOverlay`(拖拽重排插入线、跨组并入、五区拖放分屏,Esc 取消;浮动窗口按产品决策不存在)。
- `Docking/TerminalDocument.cs` 包装 `TerminalTabViewModel`,实现 `IDockViewProvider` 自建视图。
- `MainWindow.axaml` 用 `<dockc:DockWorkspaceControl Workspace="{Binding Layout}" />` 承载;`TabBar`(Ctrl+Tab/W 逻辑集合)与工作区激活态**双向同步**(原 Dock 集成缺 TabBar→文档区半边)。
- `Controls/ReparentingHost.cs` — 沿用:内容宿主挂缓存视图前先从旧父级摘除,保证共享终端控件任一时刻只有一个父级。
- `Themes/DockStyles.axaml` 保留全局通用样式(ToolTip/ContextMenu/MenuFlyout/tab-nav 等);标签视觉内联在 `DockTabItem.axaml`。

## 6. UI / 视图与设置

- **状态栏跟随激活 Tab**:每个 `TerminalTabViewModel` 携带 `ConnectionSummary/TerminalTypeName/EncodingName`;`UpdateStatusBarForActiveTab` 投影连接串/状态/类型/编码/尺寸/延迟;订阅 `ActiveTerminalTab` 变化 + Dock `ActiveDockableChanged`/`FocusedDockableChanged` → 切换标签/窗格实时更新左下角。
- **窗口壳:自绘无边框标题栏(2026-07-13 定稿)**:主窗 `WindowDecorations="None"`(与全部对话框同款全自绘模式);`Views/TitleBarView` 自绘 36px 标题栏 —— 左 logo+产品名,右 全局功能图标组(搜索/SFTP 文件管理/路由追踪/进程管理器/隧道/命令面板,经命令注册表,**已全部启用**;分屏走命令注册表 `split.horizontal`/`split.vertical`;多会话同步输入已以标签右键 A/B/C/D 频道菜单落地,见 §12-7 —— 2026-08-14 勘误,此前"组同步/广播未实现、禁用半透明"的描述已过时)+ 最小化/最大化/关闭三枚窗口控制按钮(46×35,关闭 hover #E81123)。**并非回退原生 chrome** —— Avalonia 12.x 的 `ExtendClientArea`/`WindowDecorationsElementRole` 托管装饰在 Win32 上会拦截标题栏输入(按钮点不动、窗口拖不动),整套机制不可用故弃用;改以**自绘 + 原生行为补齐**:空白区 `BeginMoveDrag`(原生移动循环,Win11 边缘贴靠有效)、双击切最大化;**Win11 Snap Layouts 经 `MainWindow` 的 WndProc 钩子处理 `HTMAXBUTTON`**(提交 `ce71b32`,`nc-hover` 类由 NC 消息挂/摘);窗口四周 5px + 四角 10px 自绘缩放抓取区(`BeginResizeDrag`,最大化时关闭)。**文字菜单(会话/编辑/…)已整体移除**——与命令面板功能重复(用户决策);随之移除设置里的"显示菜单栏"开关(`ShowMenuBar` 存储字段保留兼容)。
  - **踩坑备忘(自绘壳为何不走 extend/原生 chrome,Avalonia 12.0.5 观察)**:①`VisualRoot as Window` 恒为 null(视觉根是 TopLevelHost),取窗口必须走逻辑树 `FindLogicalAncestorOfType<Window>()`——曾令标题栏按钮/拖动看似"无输入"数小时;②`ExtendClientAreaToDecorationsHint`/`BorderOnly` 的托管装饰(`WindowDrawnDecorations`)会绘制重复标题与含"全屏"的按钮,且 `WindowDecorationsElementRole` 的输入重定向未落地(HT\*BUTTON 点击无动作、User 角色不可点),BorderOnly 还丢 WS_CAPTION(HTCAPTION 拖动与最小/最大化动画失效,issue #21160/#21212)——整套 extend 机制在 12.0.5 不可用,故弃用。
- **命令面板(Ctrl+P / Ctrl+K)**:`ViewModels/CommandPaletteItem.cs`(+Group)、`CommandPaletteViewModel.cs`(模糊子序列搜索、分类分组、上下循环导航、执行/关闭)、`Views/CommandPaletteView.axaml(.cs)`;`MainWindow` 半透明遮罩浮层,条目=最近会话(Enter 连接)+ 全局命令。
- **终端类型/编码设置项**:`AppSettings.TerminalType`(默认 xterm-256color)/`TerminalEncoding`(默认 UTF-8);`SettingsViewModel`/`SettingsView` 两个下拉;`Program.cs` 注册 `CodePagesEncodingProvider`(GBK/Big5);连接时 `MainWindowViewModel.ConfigureTerminal` 应用到 PTY 的 TERM 与控件。`ISettingsService`/`JsonDataStore` 已入 DI。
- 快捷命令面板、隧道管理面板此前已有完整 View+VM。
- **设置窗口现为 12 页**(2026-08-14,840×740):常规 / 外观 / 终端 / 密钥管理 / 快捷键参考(纯展示) / 文件传输 / 安全审计(含会话录制与已信任主机) / **网络代理(2026-08-14 新增,见 §12-10)** / 代码片段 / 云同步 / 关于(含贡献者) / 支持与捐赠;整改详情见 §13 与 `velashell-docs zh/host/settings-audit.md`。
- **终端配色跟随主题**:未自定义时 暗=Dracula / 亮=Solarized Light 实时切换;配色方案下拉的“(默认)”后缀与选中项随主题动态联动,选默认方案 = 恢复出厂跟随态。

## 7. 测试(已全量迁移到 MSTest)

- 6 个测试项目。**规模(2026-07-22 静态计数):968 个 `[TestMethod]` + 164 个 `[DataRow]`**,较 2026-07-12 的 ≈606 大幅增长,主要来自 ZMODEM 协议套件、SFTP 双栏与传输续传、自更新链路与 headless 视图测试。精确通过数以 `dotnet test` 为准。
- **已知失败(2026-08-14 复核)**:✅ QuickCommands/命令建议 12 个失败已消除 —— `QuickCommandCatalog` 现有 28 条内置命令,测试改为**从目录推导计数**(`BuiltInCount`/`SampleBuiltIn`,不写死数字,见 `QuickCommandsViewModelTests.cs:11-20`);ConPTY 无头握手用例仍环境相关按需跳过。当前 `dotnet test` 全套 **1657 通过 / 0 失败**(2026-08-14)。
- 已移除 `xunit`/`xunit.v3`/`FluentAssertions`/`Avalonia.Headless.XUnit`;改用 `MSTest.TestFramework`+`MSTest.TestAdapter` 3.11.1,全局 `using Microsoft.VisualStudio.TestTools.UnitTesting`。
- 转换约定(供新增测试参考):`[Fact]`→`[TestMethod]`;`[Theory]`+`[InlineData]`→`[DataTestMethod]`+`[DataRow]`;`[Trait("Category","X")]`→`[TestCategory("X")]`;每类 `[TestClass]`;`ITestOutputHelper`→`public TestContext TestContext {get;set;}`;`IAsyncLifetime`→`[TestInitialize]`/`[TestCleanup]`。
- 断言:MSTest `Assert.AreEqual(EXPECTED, ACTUAL)`(期望在前);异常用 `Assert.ThrowsExactly`/`Assert.ThrowsExactlyAsync`;字符串用 `StringAssert`;序列用 `CollectionAssert`。
- 注意点:`long`/`uint` 期望值要带后缀(`AreEqual(object,object)` 类型严格);`bool?` 用 `x == true`;非记录类型对象等价用 JSON 序列化比较。
- 早期约定「测试不渲染 Avalonia」**已放宽**:`Terminal.Tests` 与 `VelaShell.Tests` 现引 `Avalonia.Headless`,`VelaShell.Tests/Views/` 下有一批 headless 视图与像素回归用例(`VelaHeadlessApp` 为其宿主)。纯逻辑用例仍只 `new` 控件、不起 UI。`VelaShell.Tests/ModuleInit.cs` 用 `[ModuleInitializer]` 初始化 ReactiveUI 调度器,保留。
- 集成测试(`SshIntegrationTests` 需 Docker+SSH 服务器、`CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`)按环境早退跳过。

## 8. 关键约定 / 已知坑

- 构建/测试用根目录 `VelaShell.slnx`。运行 App 后 DLL 被占用会导致构建报"文件被锁定"——先停掉运行实例。
- Bash 工具用 Git Bash;不要用 `Read`/`Grep` 直接读 `.pen`(加密,只能走 pencil MCP)。
- 记忆索引见 `C:\Users\Joe\.claude\projects\G--VelaShell\memory\`(terminal-engine、docking、sonnetdb-storage、connect-flow)。
- SonnetDB 要点:`Tsdb.Open(new TsdbOptions{RootDirectory})`;文档 `db.Documents.Open(name)` 的 Upsert/Get/Scan/Delete;时序 `db.Write(Point.Create(...))` + `SqlExecutor.Execute` SELECT;`FieldType` 在 `SonnetDB.Storage.Format`(是 `Int64` 不是 `Long`,写值用 `FieldValue.FromLong`);**时序 tag 值不允许空串**(临时连接不写 profile_id);**SQL 方言:`ORDER BY time` 要求 SELECT 列表包含 time 列**;`DELETE FROM measurement` 可能不受支持(录制存储以 drop+回写压缩兜底回收);仓储加密必须写副本、不可原地改传入的 profile(内存明文用于活动连接)。
- Avalonia 12 坑:`Run.Text` 绑定会在卸载等时机回写(展示转换器 `ConvertBack` 返回 `BindingOperations.DoNothing`、绑定标 `Mode=OneWay`);ComboBox 的 `SelectedItem` 在 ItemsSource 为空/Clear 时会把 null 写回数据源(载入顺序先填列表再回填选中,见默认密钥修复);XML 属性值中的换行被规范化为空格(多行文案拆多个 TextBlock)。

## 9. 2026-07-08 完成情况(6 次提交,514 测试全绿)

按"每部分一次提交"推进,提交顺序即依赖顺序:

1. **`2a270e5` feat(storage) SonnetDB 存储层** —— 持久化全面切换嵌入式 SonnetDB。
   - `SonnetDbEngine`(单例,退出 Dispose 刷 WAL):文档集合 `session_groups` / `session_profiles`($.groupId 索引)/ `app_config`(settings/state 单文档)/ `known_hosts` / `ui_config` / `quick_commands`;时序 measurement `conn_history`(最近连接)/ `audit_log`(审计)。
   - 新接口:`IRecentConnectionService`、`IAuditLogService`、`IAppDataStore`(通用 JSON 文档存取)、`ISecretProtector`。
   - `AesSecretProtector`:AES-256-GCM + 本地密钥文件 `secret.key`,密文前缀 `enc1:`,历史明文读取兼容。
   - 旧 JSON(sessions/settings/state/known_hosts/quick-commands)首次运行导入后改名 `.migrated.bak`;LiteDB 包移除。
2. **`10e9e70` fix(ui) 侧边栏快速连接区** —— history 图标修正并接刷新;移除输入框;最近连接改"名称-分组 + 相对时间"两行(user@host:port 移入悬停提示);数据源 = SonnetDB 连接历史(去重、倒序、上限 10),重启不丢;双击按 ProfileId 解析档案重连。
3. **`1e1fa6b` feat(ui) 新建连接弹窗** —— 按设计 oAHna 重构(自绘标题栏/协议标签页/记住密码/会话分组/高级选项/测试/保存/连接)。保存只落库、连接落库+建会话;`SessionProfile.RememberPassword=false` 时凭据不持久化。**修复仓储加密副作用 bug**(原地加密会把内存明文密码改成密文导致重连认证失败,改为写副本)。会话树按 GroupId 接线(含"未分组"节点、双击/右键连接、右键编辑、保存后刷新);Ctrl+N 打开弹窗。
4. **`f5405f5` feat(auth) 两步登录验证** —— `AuthenticationDialogView` 按设计 oNZIM/twD13(第 1 步用户名+指纹,第 2 步密码/证书/密钥分段);凭据缺失时 `TryConnectProfileAsync` 经 `InteractiveAuthenticator` 弹窗,认证失败自动重试(≤3 次);SSH 握手接主机密钥 **TOFU**(首次记录指纹到 known_hosts,指纹变化拒绝连接);连接成败写 `audit_log`。
5. **`3ef6bed` docs** —— architecture.md / 架构设计.md / 隧道功能规划.md 持久化方案全部改为 SonnetDB 并补数据结构说明。
6. **`2812048` feat(settings) 设置窗口九页** —— 自绘对话框 + 图标导航(常规/外观/终端/密钥管理/快捷键/文件传输/安全审计/代码片段/关于),Ctrl+, / 侧边栏齿轮 / 命令面板均可打开;`AppSettings` 扩展分组选项(General/Appearance/TerminalBehavior/Transfer/Security/Keys)嵌套持久化;密钥管理页为真实功能(`SshKeyService`:枚举 ~/.ssh、类型+SHA256 指纹解析、生成 RSA、导入/删除/复制公钥);代码片段页复用 `quick_commands`;常规页清除历史/配置导入导出可用。

此前 §9 的"设置子页补全"与"安全(密码明文)"两大项**已完成**;会话树已接线。

## 10. 后续待办 / 已知问题(2026-07-09 复盘)

**A. 设置项接线状态(2026-07-09 全量排查后)**

✅ **已完成接线**(本轮实现,详见各消费点):终端行为全套(光标样式/闪烁、行高、选中即复制、右键粘贴、复制去尾空格、双击选词、多行粘贴确认、Ctrl+C 复制、滚动行为、Bell 三模式+标签闪烁、IME 开关)、外观(终端四色+ANSI16 稀疏覆盖、窗口透明度、菜单栏显隐、侧边栏位置、启动窗口状态、UI 字体/字号)、常规(默认端口、连接超时/心跳、自动重连+间隔+重试、关闭前确认、断开提醒+声音、开机自启、托盘、恢复会话、会话日志+保留清理、全局记住密码)、文件传输(远程初始目录、下载目录、显示隐藏文件、最大并发、双向冲突策略(下载查本地/上传 stat 远端,询问弹窗+覆盖/跳过/重命名,2026-07-10)、保留时间戳、完成通知、带宽限速、传输日志+保留清理)、安全(首次指纹人工确认、指纹变更阻断/人工裁决、告警通道应用内+Webhook+审计)、密钥(默认认证密钥)。
关键接线点:`MainWindowViewModel.ApplyLiveTerminalSettings` / `MainWindow.ApplyWindowAppearance+OnClosing` / `InfrastructureServiceCollectionExtensions`(超时/心跳/指纹策略)/ `SftpService`(带宽/时间戳)/ `FileBrowserViewModel.TransferOptions`。
默认值调整:LineHeight 1.2→1.0、ScrollOnOutput true→false、CopyOnSelect false→true、RemoteInitialPath "/home/user"→""(空=家目录)。

⏳ **仍未实现(2026-07-11 起这些 UI 已按设置审计从界面隐藏——不再以禁用控件示人;字段仍持久化,实现后恢复展示)**:

| 项                                            | 设置位置 | 未实现原因 / 实现思路                                                                                                                                                                                                       |
| --------------------------------------------- | -------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 标签栏位置(顶部/底部)                         | 外观     | **仍未实现**(2026-08-14 复核):VelaDock 替换后 UI 已从外观页撤下,仅剩 `TabBarPosition` 持久化字段与 VM 索引映射(`TabBarPositionIndex`),Docking 层零消费。自研 `DockGroupControl` 后技术上已可做(改标签条停靠边),按需排期 |
| 启动时自动检查 / 自动下载                     | 常规     | **仍未实现**(2026-08-14 复核):`CheckUpdatesOnStartup`/`AutoDownloadUpdates` 字段无任何运行时消费者,设置页也未展示;唯一更新入口是关于页手动按钮。核心 `UpdateService` 已就绪(频道开关已生效),接启动路径+开关即可 |
| 主密码保护                                    | 常规     | **仍未实现**(2026-08-14 复核,UI 已撤下并留注释 R-04):需主密码派生密钥替换 `AesSecretProtector` 的本机密钥文件 + 启动解锁弹窗 + 密文迁移,安全敏感需单独设计                                                                 |
| ~~断点续传~~ / 自动续传 / 传输重试 / ~~临时文件清理~~ | 文件传输 | ✅ **断点续传已实现**(2026-07,`SftpService` 双向按偏移续写 + 尾部 64KB 核实起点)。✅ **临时文件清理已实现**(消费点 `FileBrowserViewModel.CleanupPartialTargetAsync`:仅 `ResumeEnabled` 关闭时生效,失败/取消删半截目标文件)。`AutoResume` 已降级为遗留兼容字段(运行时不消费,实际开关是 `ResumeEnabled`);**失败重试**(`TransferMaxRetries`)仍未实现,需传输队列持久化 |
| ~~会话录制 / 输入脱敏~~                       | 安全审计 | ✅ 2026-07-12 录制与回放已实现(SonnetDB 时序 + 回放中心,见 §13);输入脱敏确认不做(仅录输出流,密码无回显)                                                                                                                     |
| 自动加载密钥到 Agent                          | 密钥管理 | **仍未实现**(2026-08-14 复核,字段无消费者,R-06):需集成 Windows OpenSSH ssh-agent(named pipe 协议)或 Pageant。注意 §17-A 的凭据装配已**刻意整体替换**默认凭据列表以排除 SshAgentCredentials(Windows 上 SSH_AUTH_SOCK 非命名管道会刷异常),实现本项时须同步调整该处 |

❌ **确认当前架构不实现,已从设置界面与 `AppSettings` 移除**(2026-07-10,见 velashell-docs 的 zh/host/架构设计.md §11):连字 Ligatures(自绘渲染器按单元格排版,无法跨字符连字)、自适应标题栏颜色(系统原生标题栏由 OS 托管)、系统通知 Toast(需 AppUserModelID/通知框架;常规页用「声音提示」、安全审计页告警通道改为「提示音」`Security.AlertSound` 替代)。
✅ **上传方向冲突策略已实现**(2026-07-10):上传前 `ISftpService.ExistsAsync` stat 远端同名文件,按策略询问(弹窗:覆盖 or 跳过)/覆盖/跳过/重命名(`file (1).txt` 取首个可用名);「覆盖」策略下不额外 stat,沿用 SFTP 覆盖语义;编辑器保存回传属有意覆盖,不走冲突检查。

**B. 功能缺口**

- 非 SSH 协议:**SFTP 已开放**(`ConnectionType.SFTP`,独立 SFTP 标签,见 §12-14);**FTP / FTPS 已开放**(2026-08-13,`ConnectionType.FTP` + `SessionProfile.Ftp`,FluentFTP 后端 + 连接池 + 按会话分派的 `RoutingRemoteFileService`,见 velashell-docs 的 zh/host/FTP客户端可行性调研.md);**Telnet 已开放**(2026-08-17,**以插件形式**:`plugins/VelaShell.Plugin.Telnet`,RFC 854 协商 + NAWS + 8 位透明;宿主为此新增「终端协议」能力 `IProtocolTerminal`,插件会话经 `PluginTerminalShellStream` 适配成 `IShellStreamWrapper`,复用既有的桥/VT 引擎/ZModem/重连;会话类型仍是 `ConnectionType.Plugin`,故调研文档里那套「协议泛化」改造整套免掉);**串口仍禁用**,将复用同一能力做成 `velashell.serial` 插件——见 [`velashell-docs zh/host/Telnet与串口可行性调研.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Telnet与串口可行性调研.md) 顶部的落地说明。第 2 步"证书"认证仍禁用。
- ✅ 快捷键展示表已与真实绑定逐条核对重建(2026-07-11,删除虚构项、补 Ctrl+N 绑定);**自定义键位确认不做**(产品决定,页面定位为"快捷键参考")。
- 密钥生成仅 RSA(PEM+OpenSSH 公钥);ed25519 生成缺失(2026-08-14 复核仍缺:`ISshKeyService` 只有 `GenerateRsaKeyAsync`,ed25519/ecdsa 仅用于识别已有密钥类型;.NET 无内置 OpenSSH ed25519 私钥导出,需自行实现 OpenSSH 私钥封装或引入 BouncyCastle);导入不校验私钥有效性;删除无二次确认。
- 审计日志已在写(connect/connect-failed),但**无查看界面**(2026-08-14 复核:`SonnetDbAuditLogService.QueryAsync` 在 UI 层零调用);`audit_log`/`conn_history` 无保留策略(retention),长期运行会累积。
- ✅ 配置导出文案已修正为"仅应用设置"(2026-07-11,settings-audit C-08);导出/导入为**全量 `AppSettings` 序列化 + 整体覆盖**(2026-08-14 复核),选择性(分类勾选)导出待做——注:**Gist 云同步(§13)已覆盖设置/连接/隧道/片段的跨设备迁移场景**。⚠️ 全量导出含 Security/Proxy 等敏感块(代理密码明文)。
- ✅ 命令面板"会话"类目已含**全部已保存会话**(2026-08-14 复核:`MainWindowViewModel:1756-1825`,"最近连接"快速通道 + "会话"全量类目带分组徽章,按 ProfileId 去重,§12-3)。
- ✅ 关于页"检查更新"已重写为**自研便携式自更新**(2026-07-17,Velopack 已移除):`UpdateService` → GitHub Releases `latest.json` 清单(stable 走 `releases/latest/download` 固定地址不占 API 配额,preview 走 API 列表),按 RID 选包 → 下载(进度)→ SHA-256 校验 → `UpdateApplier` 原地换版(`.new` 暂存 + 两段重命名,只动包内文件)→ `--after-update` 重启(等待单实例锁),下次启动清理 `*.old` / 崩溃回滚(`App/Services/Update/`)。便携版任意目录可更新;目录不可写(如 Program Files MSI)提示手动下载;更新通道跟随设置页 stable/preview 开关。"更新日志"仍禁用。
- ✅ 会话录制与回放已组件化(2026-07-12,§13);✅ 主机信任中心以"安全审计 → 已信任主机"落地(查看/删除/地址脱敏)。设计稿面板 2026-08-14 复核:✅ **系统资源监控面板已实现**(§18-B)、✅ **连接诊断中心已实现**(§18-C)、✅ **文件传输 toast 已实现**(`FileTransferView`,浮动活动/历史项);仅**运维编排中心**(设计帧 bR5c4)仍未实现。

**C. 技术债 / 小瑕疵(2026-07-09 处理完毕,余项见末尾)**

- ✅ QuickConnect 组件已删除(View/VM/SidebarViewModel 引用与测试同步清理;Strings 资源保留供本地化测试)。
- ✅ SonnetDB 锁粒度:**决定保留全局信号量**——文档集合与时序共享同一 Tsdb 实例(同一 WAL/存储引擎),SonnetDB 未承诺内部线程安全,按集合分锁有并发损坏风险。实际瓶颈是设置读热点(每次连接/每个传输文件都读一次),已在 `SonnetDbSettingsService` 加 **settings JSON 缓存**(缓存序列化文本、按次反序列化,调用方语义不变),读路径不再进锁/碰盘。
- ✅ 硬编码 `#0A0E14` 前景已抽成 `PulseAccentForeground` 令牌(暗=深字/亮=浅字;亮色 accent #644AC9 深底配深字对比不足的问题即此);用户自定义强调色时 `App.ApplyAccent` 按亮度自动配对前景。AboutPage 固定青色渐变上的图标有意保留硬编码。亮色主题整体仍建议实机走查一遍。
- ✅ `Ctrl+T` 改为打开新建连接(与 Ctrl+N 一致;旧绑定往已不显示的 TabBar 塞空标签)。Dock 布局持久化**缓做**:文档即活动 SSH 会话,单独恢复布局无意义;需与"恢复会话"联动(布局节点 ↔ profile 映射,Dock.Avalonia DockSerializer + 自定义 document 还原器),列为后续特性。
- ✅ OSC 52(远端写剪贴板,tmux/vim yank;只支持写方向,查询"?"一律不应答防剪贴板泄露;1MB 上限)与 DECRQSS(应答 SGR "m" 与 DECSTBM "r",其余回 `DCS 0 $ r`)已实现,含单测。**顺带修复解析器预存在 bug:全局"ESC 重启序列"会把 ST(ESC \)结尾的 OSC/DCS 整段丢弃**(BEL 结尾才能用),现已在 ESC 分支先分发在途载荷,补了回归测试。
- ✅ 运行时热切终端类型:**按设计不做**——TERM 在连接时向远端协商,活动会话热切只会造成本地仿真与远端 TERM 能力档不一致;设置修改对新连接生效即为正确语义。CJK 回退:字体链(Cascadia Mono→JetBrains Mono→Consolas→Microsoft YaHei→monospace)+ 渲染器逐格 FormattedText 回退路径已覆盖双宽字形,视为已解决。
- ⏳ sixel 图形仍挂起(多日工作量,依赖需求评估)。
- ✅ 需实机确认(代码层无法验证):三个自绘对话框(连接/验证/设置,SystemDecorations=None)的拖动与阴影;亮色主题下各对话框观感;命令面板圆角修复效果。注:主窗与三个对话框均为 `WindowDecorations="None"`/`SystemDecorations=None` 自绘无边框(§6),需实机确认自绘标题栏拖动/贴靠/阴影观感。

## 11. 设计稿分析已记录的问题(供实现时对照)

- 设置-终端 缺终端类型/编码选择器(已在代码补上)。
- term-\* 只定义 8 个 ANSI 色,无 bright/256(引擎侧已补全)。
- 未指定 CJK/双宽回退字体。
- 终端交互(光标样式、选区色、终端内搜索、分屏)设计未建模。
- 亮色主题 `bg-terminal=#1E1E2E` 仍为深色(疑似有意)。
- Logo 有一个 `enabled:false` 残留图标;文件列表"修改时间"列无固定宽度。

## 12. 与主流终端工具的功能缺口(2026-07-09 对照 Xshell / MobaXterm / Tabby / WindTerm 分析)

> §10.B 已列的缺口(Telnet/串口/证书认证、快捷键自定义、ed25519 生成、审计查看界面、全量配置导出、更新检查等)不在此重复。以下为本次新识别的缺口,按优先级排列。

**P1 —— 日常使用高频(2026-07-09 全部实现,除第 1 项外各自独立提交)**

1. ✅ **本地终端标签**(⚠️ 待实机验证,暂未提交):`Infrastructure/Pty/ConPtyShellStream.cs`(CreatePseudoConsole + 双匿名管道;进程退出 → 300ms 排空 → 关伪控制台 → 读端 EOF 归一化)实现 `IShellStreamWrapper`,复用既有 桥→VT 引擎→自绘控件 管线;`App/Services/LocalShellCatalog.cs` 探测 pwsh / Windows PowerShell / CMD / WSL / Git Bash 并动态注册命令面板入口(`local.*`);本地标签强制 UTF-8、不自动重连(exit 是用户意图)、Enter/Ctrl+R 重开进程。**已知注意点**:本机(Windows 预览版 conhost)对无头测试进程不渲染屏幕帧(新版 ConPTY 先发 `CSI 1t`/`CSI c`/`?1004h`/`?9001h` 协商,DA 无应答约 3 秒自杀),单测只断言 拉起+握手+输入通路+EOF 契约;GUI 内 VT 引擎会自动应答 DA,需实测确认出帧,若仍无帧则下一步补 win32-input-mode/更完整的终端应答。
2. ✅ **SSH 跳板机(ProxyJump)**:`SessionProfile.JumpHostProfileId` 引用另一条已保存配置作跳板(链式即多段跳,≤5 跳、带环检测,`ConnectionWorkflowService.BuildChainAsync`);**指纹按各跳逻辑主机校验**(绝不按 127.0.0.1 记录);连接对话框-高级选项 选跳板;跳板配置需已保存凭据。
   **实现已随 Tmds.Ssh 迁移简化**(2026-07):原先的 `JumpChainSshClientWrapper` 手工逐跳建链(前一跳开 `ForwardedPortLocal(127.0.0.1:0)` 承载下一跳)已删除,改用 Tmds.Ssh 原生 `SshProxy` 链——`InfrastructureServiceCollectionExtensions.BuildProxyChain` 按跳板配置递归构造 `SshClientSettings.Proxy`,建链与回收由库负责。
3. ✅ **保存的会话全部进命令面板**:"最近连接"(快速通道)+"会话"(session_profiles 全量、分组名徽章、按名排序),两组按 ProfileId 去重;缓存随会话树刷新(`RefreshPaletteSessionsAsync`)。
4. ✅ **导出终端缓冲区**:命令面板"导出终端输出到文件"(`terminal.export`):有选区导出选区、否则全量(scrollback+屏幕,逐行去尾空格、截掉尾部空行),保存对话框预填 标签名-时间戳.txt。
5. ✅ **配色方案预设**:`Core/Models/TerminalColorScheme.cs` 内置 Dracula / Solarized Dark / Solarized Light / Nord / Gruvbox Dark / One Dark / Monokai / Tokyo Night;外观页"配色方案"下拉一键写入整套颜色(保存生效);选 Dracula 即恢复默认、继续跟随主题。
6. ✅ **克隆会话**:`session.clone`(Ctrl+Shift+N / 命令面板)对当前标签的 Profile 再连一次。

**P2 —— 进阶运维能力**
7. ✅**多会话同步输入**(send to all / 命令多发):对选中的多个标签广播键入,集群运维刚需;在 `UserInput` 分发处加广播开关即可。
8. ✅**ZMODEM(rz/sz)**:终端内直接收发文件,Xshell/SecureCRT 标配。**自研协议引擎**(未走 trzsz):`Core/ZModem/`(帧读写、ZDLE 转义、CRC-16/32、`ZModemSender`/`ZModemReceiver`,只依赖 `IByteDuplex`,传输无关)+ `Terminal/ZModem/`(`ZModemDetector` 在输出流嗅探 ZRQINIT/ZRINIT 引导,`ZModemTerminalRouter` 在会话期间把字节从终端改路由到引擎,结束自动复位)+ `App/Services/ZModem/`(文件源/落盘目录/进度上报)。排障置 `VELASHELL_ZMODEM_TRACE=1` 打印协议帧。**测试教训**:互操作期望值必须按 lrzsz `zm.c`/`zmodem.h` 手工构造(见 `LrzszInteropTests`)——用自家编码器生成期望值时,编解码同时错也照样全绿,CRC 双重增广的 bug 当初正是这么溜进来的。
9. ⏳**SSH config 导入**(2026-08-14 复核仍缺):解析 `~/.ssh/config`(Host/HostName/Port/User/IdentityFile/ProxyJump)批量导入会话。导入框架已就绪 —— `ISessionImportService` 多来源自动扫描架构 + Xshell/WinSCP 两个导入器已落地(§18-H),再加一个来源只需在 DI 追加一行,是低成本待办。
10. ✅**连接代理**(2026-08-14 落地,应用级全局代理而非按会话):设置 → 网络代理(无代理 / 系统代理 / HTTP / SOCKS5,主机/端口/用户名/密码 + 「使用代理执行 DNS 查找」)。统一抽象:`Core/Net/IProxyResolver`(唯一代理出口,新功能接网络一律消费它)+ `Infrastructure/Net/`(`ProxyStreamConnector` 自研 HTTP CONNECT/SOCKS5(RFC 1928/1929)握手、`LoopbackProxyRelay` 环回中继、`VelaWebProxy` 进程级 `HttpClient.DefaultProxy`)。三条通道:SSH 走环回中继(Tmds.Ssh 0.23.0 的 `Proxy` 抽象成员是 internal,外部无法派生,故在 `TmdsSshClientWrapper.ConnectAsync` 把首个真实 TCP 出站跳(有跳板链时为最内层跳板)改写到 127.0.0.1 中继;主机指纹按原始 `ci.Host` 键控不受影响);FTP 走 FluentFTP 代理子类(代理下强制被动模式);全部 HttpClient(更新/Gist/Webhook/头像/插件)由 `VelaWebProxy.Install` 进程级接管,保存即生效。代理配置不完整时抛错拒连,绝不静默直连。注意别与**动态 SOCKS 转发**(`-D`)混淆——那是隧道功能,方向相反。ICMP(ping/traceroute)与连接诊断的裸 TCP 不走代理(协议不支持/诊断语义即直连)。
11. ⏳**防空闲断开(Anti-idle)**(2026-08-14 复核仍缺):按间隔发送自定义串(如 `\0` 或空格),与已实现的 SSH keepalive 互补(keepalive 防 NAT 超时,协议层已接 `KeepAliveSeconds`→`SshClientSettings.KeepAliveInterval`;anti-idle 防服务端 shell 超时踢出,需向 PTY 输入流发字节,当前无实现)。
12. ✅**known_hosts 管理界面**:已落地为 设置 → 安全审计 → 已信任主机(2026-07-12,列出/删除/截图防泄露地址脱敏);导出未做。
13. ✅**会话标签自定义颜色/图标**:多环境(生产红/测试绿)一眼区分;SessionProfile 加 color 字段 + 标签条着色。

**P3 —— 锦上添花**
14. ✅**SFTP 本地/远程双栏**:已落地为独立 SFTP 标签(`ConnectionType.SFTP` + `Docking/SftpDocument`),`SftpDocumentView` 左 `LocalFilePaneView` / 右 `FileBrowserView`,支持双栏互拖与 OS→远端拖放。**剩余差距逐项列在 [`velashell-docs zh/host/SFTP双栏与WinSCP差距分析.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/SFTP双栏与WinSCP差距分析.md)**(该文把差距分为「接线债 / 能力缺失 / 架构级缺失」三类,不要混在一起排期)。
15. ⏳**用户自定义关键字高亮规则**(2026-08-14 复核仍缺):语义高亮已内置且为编译期硬编码 7 条规则(`SemanticMatcher` 的 `[GeneratedRegex]`:Url/Ip/Error/Warning/Success/Option/Number);开放用户正则+颜色规则表(WindTerm 卖点)需把规则集改为运行时可配 + 设置 UI,当前无任何用户规则机制。
16. ✅**命令自动补全/历史建议**:输入时基于本地命令历史悬浮建议(WindTerm 式);已有 quick_commands 可作为数据源之一。
17. ✅**OSC 52 剪贴板**:已实现(见 §10.C —— 只支持写方向,查询"?"一律不应答防剪贴板泄露,1MB 上限,含单测;此条为重复归档,2026-08-14 补标)。
18. ⏳**触发器/自动应答**(2026-08-14 复核仍缺):输出匹配正则时自动发送响应(expect 式),如自动 yes/密码带外输入;全仓无输出匹配→自动发送机制。
19. ❌**多窗口 —— 确认不实现(与当前架构/产品决策冲突,2026-08-14)**:新开独立主窗口与现架构三处硬冲突 —— ①应用为**单实例**(`Program.cs` 命名 Mutex,自更新重启依赖等锁交接);②主窗口为唯一组合根(单 `MainWindowViewModel` 持有会话/布局/状态栏全部状态,无多窗口状态分片);③自研 VelaDock **产品决策不做浮动窗口**(§5),多主窗意味着跨窗口拖拽/布局持久化整套推翻重做。多屏需求由分屏(五区拖放)承担。
20. **Mosh / SSH 证书(certificate)认证**(2026-08-14 拆分定性):
    - ❌ **Mosh —— 确认不实现(与当前传输架构冲突)**:全部远程通道抽象建立在 SSH 流式通道之上(`ISshClientWrapper`/`IShellStreamWrapper`,Tmds.Ssh);Mosh 是独立的 UDP + 状态同步(SSP)协议栈,.NET 无可用实现,接入等于并行维护第二套传输/终端预测引擎,收益不成比例。弱网场景由自动重连 + keepalive 缓解。
    - ⏳ **SSH 证书认证**:代码零踪迹(`src/` 下的 Certificate 命中全部是 FTPS/TLS 证书);连接对话框第 2 步"证书"项仍禁用。能否实现取决于 Tmds.Ssh 对 OpenSSH user certificate 的支持程度,按需求评估。

## 13. 2026-07-11 ~ 07-12 批次(设置审计整改 + 四个新特性)

**A. 设置审计整改**(台账与逐项状态见 `velashell-docs zh/host/settings-audit.md`,共三批):
BellMode/VisualBell 合并(旧配置经 `AppSettings.Normalize()` 迁移)、自动重连次数统一、默认值来源统一、显示隐藏文件写回持久化、恢复默认/清除历史加确认、误导性文案与九组相似命名修正、12+ 个未实现禁用控件隐藏或删除、选项类统一 `ObservableOptions`(INPC,从属设置条件显隐真正生效)、快捷键页与真实绑定核对重建(自定义键位确认不做)。

**B. 主机指纹三选项确认 + 已信任主机管理**:
`IHostKeyPrompt.DecideAsync` 三态(永久信任=写 known_hosts / 仅本次信任=进程内 `HostTrustOnceCache` 不落盘 / 取消=fail-closed);SFTP 独立通道补主机指纹校验(修复默认信任任意指纹的 MITM 缺口);安全审计页新增"已信任主机"列表(删除即可重触发首次确认;地址默认脱敏防截图泄露)。

**C. GitHub Gist 云同步**(`Core/Sync` + `Infrastructure/Sync` + 设置"云同步"页):
同步范围 = 应用设置(剔除设备本地字段)+ 连接配置(含分组与隧道,upsert 合并不删本地)+ 代码片段;单文件 secret Gist,版本管理复用 Gist 原生 revision(列表含来源设备,可恢复任意版本);可选 PBKDF2-SHA256(200k)+AES-256-GCM 端到端加密(未启用时凭据绝不上传);智能方向判定(本地改动标记 × 远端 revision,双端都改按较新者胜);自动同步 = 启动拉取 + 设置保存防抖推送;PAT/口令经 `ISecretProtector` 机器绑定加密,永不进载荷。

**D. 会话录制与回放**(设计 `NceE6`;`Core/Recording` + `SonnetDbSessionRecordingStore` + `RecordingPlayerView`):
录制 = 桥输出 600ms/64KB 缓冲成块写 SonnetDB 时序 measurement `session_recording_chunks`(元数据在文档集合 `recordings`);开关 `Security.RecordProductionSessions`(安全审计页,对新连接生效),保留天数随会话日志;回放中心 = 列表 + 只读终端按时间轴重放 + seek(重置瞬时重放)+ 1x/2x/4x + 跳过空闲 + 删除 + 导出 asciicast v2。输入脱敏确认不做(仅录输出流)。

**E. 支持与捐赠页**(设置导航末位):支付宝/微信/Wise(链接可点击+复制),收款码已裁剪入 `Assets/`;文案强调 PR/Issue 是最好的支持。

**F. 后续增量(同批小项)**:

- 回放中心:窗口 1200×820 + 无边框缩放(右下手柄/最大化/双击标题栏)、列表选中态改主题令牌、播完点播放自动从头、倍速扩至 1x~16x;录制保留随日志天数清理 + DELETE 不可用时 drop+回写压缩兜底(防孤儿数据块磁盘只增不减)。
- **双许可落地**:MIT → AGPL-3.0(`LICENSE` 官方全文)+ 商业授权(`LICENSE-COMMERCIAL.md`,联系 dygood@outlook.com,含轻量 CLA 条款);README/关于页同步正版声明(名称与 Logo 不在开源授权范围);商标注册与历史贡献者重许可确认为线下待办。
- 关于页**贡献者区**(设计 kGwqX,仅头像+名称):真实提交者(joesdu/tsaiggo),GitHub 头像异步加载(失败回退首字母),点击跳转主页。
- **终端配色随主题联动**:亮色默认调色板由 Alucard 换为 Solarized Light;配色方案下拉“(默认)”标注与跟随态选中项随主题动态切换(选默认方案 = 恢复出厂跟随态;显式选其它方案 = 钉住)。已知边界:覆盖模型以 Dracula 色值为出厂基准,亮色下无法“钉住 Dracula”(选它即回跟随态)。

**已知遗留**:QuickCommands 相关 12 个测试在用户某次提交后失败(测试期望 11 个内置命令含 htop,`QuickCommandCatalog` 只有 8 个,测试与目录不同步,与上述改动无关)。

## 14. 多语言(2026-07-12 全量补齐,C-09 一并完成)

- **五语言**:简体中文 / English / 繁體中文 / 日本語 / 한국어。资源按 .NET 标准命名:`Strings.resx` 为英文默认(`NeutralLanguage=en`),卫星 `zh-Hans/zh-Hant/ja/ko`(脚本中性文化,zh-CN/zh-SG→Hans、zh-TW/zh-HK→Hant 沿标准回退链自动命中);2026-07-12 首次补齐时为 867 键,随后续特性增长,**现为 938 键**五语齐平(键集平价有测试守护)。
- **全仓提取**:~900 处硬编码文案迁入 resx —— axaml 用 `{loc:Localize Key}`(实时切换),C# 动态文案用 `Strings.Get/Format`(占位符 {0}/{1})。不翻译:协议/提示符匹配串(密码提示关键词、"$ " 等)、TERM/编码名、shell 命令文本、日志。
- **实时切换的两处根因修复**:①`LocalizationService` 自持目标文化 —— 线程文化随 ExecutionContext 回卷、且 UI 线程显式设置过文化后 DefaultThreadCurrentUICulture 失效,均不可靠;②`LocalizeExtension` 改绑按键缓存的 `LocalizedText` 条目**普通属性**(Avalonia 12 绑定引擎不响应 `Item[]` 索引器变更通知),换语言逐条目发标准属性通知。语言选择:设置 → 常规 → 语言(5 项,存储值 zh-CN/en/zh-TW/ja/ko)。
- **测试守护**:键集平价(五文件同键、双向)+ 具体文化回退链(zh-SG/zh-HK/ja-JP)用例,见 `LocalizationTests`。已知边界:VM 构造时求值的标签(设置导航、快捷键参考页、状态栏初值、内置快捷命令描述)换语言后需重开窗口/重启刷新。

## 15. 版本与发布(2026-07-12)

- **版本号单一来源**:`Directory.Build.props` 的 `<Version>`(当前 `0.0.1-dev`;`AssemblyVersion`/`FileVersion` 另给不带后缀的 `0.0.1`,并关掉 `IncludeSourceRevisionInInformationalVersion` 以免 `+sha` 后缀);关于页版本运行时读程序集 InformationalVersion,不再硬编码;发版由 Release 标签经 `-p:Version` 覆盖。
- **本地发布**:`pwsh scripts/publish-all.ps1` → `publish/` 产出 6 个包(2026-07-17 起,`-noruntime` 变体已裁撤):Windows x64/arm64 便携 zip,macOS 与 Linux x64/arm64 tar.gz(全部含运行时;2026-08-12 起摊开发布,不再单文件 —— 隔离插件的 `VelaShell.PluginHost` 需要磁盘上的真实可执行体,换版随之从"移动"改为"复制"),外加自更新清单 `latest.json` 与 `SHA256SUMS.txt`。
- **CI/CD**:`.github/workflows/release.yml` —— GitHub 页面发布 Release(publish)即触发:windows/macos/ubuntu 三原生 runner 并行构建同一套 6 产物(版本号取 Release 标签,`-p:Version` 覆盖,发版无需改代码),汇总生成 `SHA256SUMS.txt` 与 `latest.json`(应用内自更新清单:版本/标签/各 RID 产物名+sha256+大小),经 `gh release upload` 全部附加到该 Release。macOS 产物未签名/未公证(需 Apple 证书后续补);Linux 为便携 tar.gz(.deb/AppImage 为后续扩展点)。
- **Windows 安装包(2026-07-13;2026-07-17 调整)**:Velopack `Setup.exe` 链路已整体移除——其默认安装目录曾与当时的 `%LocalAppData%\VelaShell` 应用数据根冲突,卸载会清空用户数据,且自打的便携 zip 无法经 Velopack 更新。现行数据根已改为 `~/.velashell`;分发方案为便携 zip + 自研应用内自更新(任意目录原地换版)。WiX v4 MSI 定义(`installer/VelaShell.wxs`,x64/arm64,`WixUI_InstallDir` 中文向导支持自定义安装目录,静默安装 `msiexec /i VelaShell.msi /qn INSTALLFOLDER="D:\Tools\VelaShell"`;`ProductVersion` 须为纯数字 x.y.z,`UpgradeCode` 固定走 MajorUpgrade)保留可手动构建,不再随 CI 发布;MSI 装进 Program Files 后应用内更新按"目录不可写"如实提示手动下载。

## 16. 2026-07-13 ~ 07-14 批次(VelaDock 合并、原生窗口壳、终端侧栏、安装包、工程化)

> 本批以数个独立 PR 合入 `dev`/`main`(#3 replacedock、#5、#6)。多为架构/工程化收尾与使用体验修正。

**A. 自研 VelaDock 正式落地(PR #3)**:详见 §5 与 `velashell-docs zh/host/dock-replacement-plan.md`(已补「已完成」横幅)。模型/控件/拖拽全套自研替换 `Dock.Avalonia`,零第三方停靠依赖;拆分对所有标签组一致生效(单标签次级组也可水平/垂直拆分);点击窗格内容区即激活该组文档(SFTP 面板与状态栏随焦点窗格切换)。关于页开源许可列表已删 Dock.Avalonia 条目。

**B. 主窗自绘无边框标题栏 + 原生行为补齐(体验优化)**:详见 §6。主窗保持 `WindowDecorations="None"` 全自绘(`TitleBarView` 含 logo/名称 + 功能图标组 + 自绘 min/max/close),**未走原生 chrome**(extend/角色重定向在 Win32 不可用);改以 `BeginMoveDrag` 原生移动循环 + **WndProc 钩子处理 `HTMAXBUTTON` 实现 Win11 Snap Layouts**(`ce71b32`);并修 `c12a8ff`(Avalonia 12 `VisualRoot` 非 `Window`,取窗口须走逻辑树 `FindLogicalAncestorOfType<Window>`)。中途 `7580052` 试过原生 chrome 集成,因保真问题走回自绘+WndProc 方案。对话框同为自绘无边框。

**C. 终端行号 / 时间侧栏(gutter)**:`Terminal/Rendering/GutterLayout.cs` + `GutterFoldModel.cs` 为终端左侧新增**行号**与**时间戳**两列侧栏,各自独立开关、支持快捷键切换;含折叠标记与空白间隔,折叠模型重构以增强可测性(`GutterFoldTests`/`GutterLayoutTests`/`GutterFoldUiTests`/`LineTimestampTests`)。

**D. 本地终端进程树秒杀**:`ConPtyShellStream` 引入 **Windows Job Object**,关闭标签/退出时连带杀掉子进程树(如 WSL、pwsh 派生进程),优化关闭体验,避免孤儿进程。

**E. 交互式提示下的补全判定**:密码类提示行(sudo/密码提示关键词命中)**不再弹出命令补全**弹层(sudo 密码提示下按键误弹智能提示);新增交互式判定单元测试。

**F. 工程化收尾**:①**集中式包管理** `src/Directory.Packages.props`(`ManagePackageVersionsCentrally`,各 csproj 只写包名不写版本)+ 构建系统重构;②**Avalonia 12.0.5 → 12.1.0** 升级;③**全项目补充详细 XML 注释**(`GenerateDocumentationFile`);④为**每个 src / tests 项目新增独立 `README.md`**(架构、目录职责、依赖关系),根 `README.md` 与 `velashell-docs zh/host/architecture.md`/`架构设计.md` 同步刷新至当前状态(版本、VelaDock、原生标题栏、五语言、发布/安装包、命名 `Pulse*`→`Vela*`)。

**已知遗留(延续)**:§13 末 QuickCommands 相关 12 个测试仍待与 `QuickCommandCatalog` 对齐(测试期望 11 个内置命令含 htop,目录只有 8 个);ConPTY 无头握手用例环境相关失败,均与本批改动无关。

## 17. 2026-07 批次(SSH 传输层迁移、ZMODEM、SFTP 双栏)

**A. SSH.NET → Tmds.Ssh 迁移**(`tmds-ssh` 分支):换成全托管、async-first 的 [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh) 0.23.0。
Core 的中立抽象证明有效——**迁移一行 Core 代码都没改**,改动全部落在 `Infrastructure/Ssh/`:

- `SshClientWrapper`/`SftpClientWrapper` → `TmdsSshClientWrapper`/`TmdsSftpClientWrapper`;`ShellStreamWrapper` 现包装 `RemoteProcess`。
- `SshNetInterop` → `TmdsSshInterop`:库异常翻译为 Core 的 `VelaSsh*Exception` 族。Tmds.Ssh 的 `ConnectFailedException` 是 `internal`,只能按消息前缀 `"The connection could not be established - {reason} - "` 提取原因再分派,这是当前实现的已知脆弱点。
- **ProxyJump 简化**:删除手工建链的 `JumpChainSshClientWrapper`,改用库原生 `SshProxy` 链(`BuildProxyChain`,见 §12-2)。
- **回归**:`连接代理(SOCKS5/HTTP 经由连接)` 随 SSH.NET 一起失去(§12-10 已改回 ❌)。
- ✅ **已修(2026-07-22)**:`MainWindowViewModel` 曾按旧类型名字符串匹配异常(`"SshAuthenticationException"` 等),而实际类型已是 `VelaSshAuthenticationException`,导致**认证失败重试与全部分类错误提示静默失效**(都落进兜底文案)。现改为直接匹配 `VelaSsh*Exception` 类型,并去掉已无对应实现的 `ProxyException` 分支(`Msg_ProxyError` 资源键保留)。
  **为什么没被测出来(重要)**:`InteractiveAuthFlowTests` 与 `MainWindowSshFeatureTests` 各自定义了一个**私有的假异常 `SshAuthenticationException`**,注释明写"Named to match SSH.NET's ... so the VM's type-name mapping applies" —— 测试专门迎合了实现的怪癖,于是生产路径从未被覆盖、测试长期全绿。根子在 `VelaSshClientException.cs` 的注释:它声称这些类型的简单名与 SSH.NET 一致(实际带 `Vela` 前缀,从来对不上),代码照着错注释写。两个假异常已删除、改抛真类型,该注释也已改写。
  **约定**:跨层识别异常一律 `ex is VelaSshXxxException` 类型匹配,**绝不用 `GetType().Name` 字符串** —— 换库或改名时字符串匹配不会产生任何编译错误。

**B. 自研 ZMODEM(rz/sz)**:见 §12-8。

**C. 独立 SFTP 标签 + 本地/远程双栏 + 断点续传**:见 §12-14 与 §10.A;差距清单见 `velashell-docs zh/host/SFTP双栏与WinSCP差距分析.md`。

**D. 远程文件编辑器语法高亮**:`App/Services/Syntax/`(`FileTypeDetector` + `SyntaxHighlightingService`)接 AvaloniaEdit,按扩展名对常见文件类型着色;保存即经 SFTP 回传(有意覆盖,不走冲突检查)。

**E. net10 → net11**:`Directory.Build.props` 统一切到 `net11.0`(全仓一处),`global.json` 锁 `11.0.0` + `rollForward: latestFeature`;对 net11 开启 `EnablePreviewFeatures` 与 `Features=runtime-async=on`。**代价**:构建依赖 .NET 11 预览版 SDK,全平台发布需实机冒烟;回退 net10(LTS)仍是一行改动。

**F. 拖入文件夹的异常风暴修复(2026-07-22)**:把一个文件夹拖进文件浏览器时,调试输出会被 `ConnectFailedException` / `VelaSshConnectionException` 刷屏。三处根因:

1. **`AutoConnect` 默认为 true**。`BuildSshClientSettings` 从未设置它,于是会话掉线后**每一次** SFTP 操作都会各自静默重连一次,失败抛一发 `ConnectFailedException`——这与同文件里"主连接不在时不得偷偷另建连接"的注释意图直接矛盾。现显式 `AutoConnect = false`,连接只由 `TmdsSshClientWrapper.ConnectAsync` 发起。
2. **续传探测逐文件打远端**。`TryResumeAsync` 走裸 `ExistsAsync`,绕开了本批已经预列举好的目录名单(`RemoteExistsAsync`,零往返)。拖入 N 个文件 = N 次额外往返,叠加第 1 点就是 N 次隐式重连。现已改走名单。
3. **“覆盖”策略下不列举目录**,导致开启断点续传时第 2 点退化回逐文件探测。现改为「覆盖 **且** 不续传」才跳过列举。

**G. 文档**:新增 `velashell-docs zh/host/SFTP双栏与WinSCP差距分析.md` 与 `velashell-docs zh/host/Telnet与串口可行性调研.md`(均为 2026-07-22 的决策清单,标注了每项的实现代价与"未核实"项)。

## 18. 2026-07-24 ~ 08-14 批次盘点(2026-08-14 补记;此前均已落地但未入本文件)

**A. 插件系统 v1 + AI 助手插件(08-10 ~ 08-13,本批最大特性)**
- **双宿主模式**:manifest `hostMode` 选进程内(可收集 ALC + dock 标签页)或**隔离进程**(`src/VelaShell.PluginHost/`,命名管道自研轻量 RPC、令牌握手、心跳自愈、空闲回收、独立卡片窗口);插件源码两模式零改动。关键路径 `Infrastructure/Plugins/`(`PluginManager`/`PluginContext`/`Capabilities/`/`Isolated/`/`PluginPermissionGate`)。
- **SDK**:`plugin-sdk/VelaShell.PluginSdk`(能力域 sessions/remoteFs/remoteExec/commands/events/storage/secrets/clipboard/terminal/timeSeries)+ `PluginSdk.Testing` 测试替身;管理页 `PluginManagerWindow` + 权限对话框。
- **分发**:目录即插件 + `.vpx` 包(zip,含 zip-slip 防护)一键装卸;**无商店/签名 —— 用户决策不做/推迟**(见 `velashell-docs zh/plugins/STATUS.md`,权威进度页);发布形态因此改**摊开发布**(隔离插件需磁盘上真实 PluginHost 可执行,§15 已记)。
- **AI 助手插件**(`plugins/VelaShell.Plugin.Ai`):多提供商流式对话(OpenAI Responses / Chat Completions 兼容 / Anthropic Messages,自填 Base URL+Key 走 Secrets 加密);**Agent 模式**(M.E.AI 工具循环,桥接 sessions/terminal/remoteExec/remoteFs,危险操作面板内逐条审批);**自定义 MCP 服务器**(`McpManager` 把用户自配 MCP 工具并入工具箱,非只读工具走同一审批闸);会话持久化到插件私有时序库(历史列表/切换/删除、↑↓ 调取、`@` 远端文件引用)。示例插件 HelloWorld。
- 文档:`velashell-docs zh/plugins/` 16 篇蓝图 + `STATUS.md` + `dev-guide.md`;英文镜像 `docs-en/`(08-14,31 个文件)。

**B. 系统资源监控窗口(08-01,08-29 修正)**:`ResourceMonitorWindow`(+状态栏内嵌弹层 `ResourceMonitorView`),六页 总览/CPU/GPU/内存/磁盘/网络;CPU 页热力图/迷你折线/列表三态,GPU 无卡自动隐藏;自研图表控件 `TimeSeriesChart`/`UsageHeatGrid`/`MeterBar`(`VelaShell.Controls`)。采集为**单条复合 shell 探针**分段解析(`Core/Services/SessionMetrics.cs`,`MetricsScope` 按页按需取 Basic/Detail/Gpu/Processes):CPU 含 user/sys/iowait/steal 与逐核、内存 htop 口径、磁盘逐分区 df + diskstats IO 速率、网络逐网卡 + `ss -ti` 逐连接速率、GPU nvidia-smi、进程 Top。入口:状态栏按钮。状态栏每秒轮询一次(`SessionMetricsService`),但只对 POSIX 远端发命令:`RemoteShellProbe` 判否即返回"无数据"。此前不拦,Windows 远端上每秒起一个 cmd.exe,而 cmd 把 `echo __P__; nproc; …` 整行原样回显,`Parse`(只在输出为空时返回 null)据此解出一份全 0 的假指标,状态栏一本正经地显示 CPU 0.00%。

**C. 连接诊断中心(07-25 前后)**:`Presentation/Services/ConnectionDiagnosticsService` 四步诊断 **DNS 解析 → TCP 建链 → SSH 握手(读 banner)→ 用户认证**,输出问题标题/描述/修复建议(`DiagnosticReport`);跳板会话前三步针对第一跳、认证走完整链。UI `ConnectionDiagnosticsView` 独立窗口,入口:会话树右键"诊断"。(诊断的裸 TCP/DNS **有意不走全局代理** —— 诊断语义即测直连链路,§12-10。)

**D. 路由/链路追踪 + 离线 IP 归属地(07-25)**:`PingTraceRouteService`(ICMP TTL 递增,免管理员;Linux TTL 不可用时抛可读异常而非假表)+ `MmdbIpGeolocationService`(本地 MMDB 离线库,默认 `~/.velashell/geoip/`,缺库静默降级、面板内引导下载);`TraceRouteWindow` 左侧 `TraceWorldMap` 世界地图落点 + 右侧 mtr 式跃点表(Loss/Sent/Last/Avg/Best/Worst、ECMP 额外地址)。入口:标题栏图标。设计文档 `velashell-docs zh/host/路由追踪设计.md`。

**E. 远端任务管理器(07-25,08-29 修正)**:SSH 进程管理(`IRemoteProcessService`/`RemoteProcessService`),入口:标题栏"进程管理器"图标。采集前按 `RemoteShellProbe` 判定对端是否 POSIX shell,不是就直接报"不可用"而不发命令 —— cmd.exe 会把整行探测命令原样 echo 回来,`Parse` 只在输出为空时返回 null,于是面板显示的是一张 CPU 0.0%、0 进程的**假空表**而非那句"需要一个已连接的 Linux 会话"。

**F. 文件浏览器跟随终端目录(07-24,08-20、08-29 修复)**:SFTP 上传按钮右侧 map-pin 开关(`FileBrowserViewModel.FollowTerminal`);终端 cwd 由对端 shell 的提示符发 OSC 7,`TerminalEmulator` 解析(`ParseOsc7Path`)→去重→浏览器同步。SSH bash 会话会静默安装一个仅负责 OSC 7 上报的 `PROMPT_COMMAND` 钩子(不再包含已撤除的提示符补行/光标查询逻辑);其他 shell 可在远端 rc 中按各自机制上报 OSC 7。注入前先由 `RemoteShellProbe` 走一条独立 exec 通道确认对端认 sh 语法(考验 `printf` 与 `$((...))` 算术展开,结果按主机缓存),Windows OpenSSH(默认 shell 为 cmd.exe/PowerShell)一律不注入 —— 否则整行会被当命令执行,屏幕上留下 `'test' 不是内部或外部命令`(#305)。

**G. SFTP 传输面板增强(07-27 ~ 07-29)**:框选、批量操作、文件+文件夹混选上传、冲突与历史交互优化;文件传输 toast 面板(`FileTransferView`,浮动活动/历史项,不跨重启持久化)。

**H. Xshell / WinSCP 会话一键迁移(07-30;08-13 改默认全自动)**:`ISessionImportService` 多来源自动扫描架构(`Infrastructure/Import/`:Xshell(Rc4/XshellCrypto)+ WinSCP(WinScpCrypto)),`SessionImportView` 对话框 —— 打开即遍历全部来源扫描、**默认全自动导入 + 高级手选**、按源目录建分组、跳过已存在、密码恢复三态提示 + 主密码告警(主密码库密码不可恢复,仅导会话)。新增来源只需 DI 追加一行(SSH config 导入的现成落点,§12-9)。

**I. UI 字体/字号令牌体系(07-30)**:设置 → 外观 → 界面字体/字号,经 `VelaUiFont`/`VelaUiMonoFont`/`VelaFontSize*` 令牌全局下发(派生字号按比例缩放、钳 9–24);曾清理 ~500 处 axaml 写死字体/字号(写死即令该处失效,见记忆索引)。同批:对话框按钮主题统一。

**J. 终端交互增强(08-05 ~ 08-07)**:Ctrl+Backspace 删词、Alt+左键矩形块选、右侧留白带。

**K. 外观增强**:应用背景图(`BackgroundImagePath` + 图层/内容双不透明度,gated on 有图,无图零回归)、Win11 圆角/投影/滚动条主题统一(08-14)、AvaloniaEdit 输入框(08-14)。

**L. 打包**:MSIX 商店版打包 + 自更新三段式重构 + 隐私政策(07-26)。

**M. 小项(08-14)**:会话树拖动分组(`VSESS|` 拖放载荷、空白处放下=移出分组、拖拽幽灵标签)、侧栏最近连接清除按钮(带确认)、关于页显示进程+系统架构(不一致时并列显示,自更新按进程架构选产物)。

**N. FTP / FTPS(08-13,第三方 PR;08-29 补并发自适应)**:见 §10.B —— `ConnectionType.FTP` + FluentFTP 后端 + 连接池 + `RoutingRemoteFileService` 按会话分派,上层文件浏览器/传输/限速零改动。连接池上限(`FtpSettings.MaxConnections`,默认 4)现在**会自己往下调**:①池里已有活连接却开不出新连接(`421 Too many users` 这类)→ 收到当前连接数并排队复用;②传输被 `450 Transfer busy` / "too many" 之类顶回来 → 收到 1 并重试该传输。两条合起来对付"服务端只支持单线程上传,批量上传只成功第一个"(闸门见 `AdjustableConcurrencyGate`:`SemaphoreSlim` 的许可只增不减,且超发期间只收不发)。回归测试用 `LoopbackFtpServer` 的 `MaxConcurrentSessions` / `MaxConcurrentTransfers` 复刻这两类服务器。

**O. 全局网络代理(08-14)**:见 §12-10。

## 19. 2026-08-30 隧道功能完善(计量转发 / 流量统计 / 断线自动恢复 / 端口冲突预检)

隧道规划文档 [`velashell-docs zh/host/隧道功能规划.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/隧道功能规划.md)
里挂了很久的三条迭代项一次做完。逐项的实现细节以那份文档为准,这里只记会绊到人的几点。

**A. 转发的数据面从库换成自研(`Infrastructure/Ssh/MeteredPortForwardHandle`)**:
Tmds.Ssh 把 LocalForward / SocksForward 的搬运整个做在内部,**不暴露任何连接数或字节计数**
——`TunnelInfo.BytesTransferred` 一直恒为 0 就是这个原因。要出统计只能自己接管:本地转发 =
自建 `TcpListener` + `SshClient.OpenTcpConnectionAsync`(direct-tcpip,与库内部同构,无额外跳数);
动态转发 = 自建监听 + 自研 SOCKS5 服务端握手(`Socks5Negotiation`,RFC 1928,仅 CONNECT + 无认证);
远程转发的监听端只有库能开,于是让它转发到本机一个临时计量监听,再由宿主接力到真实目标
(多一次环回拷贝换来同样的统计)。搬运保留**半关闭语义**(SSH 侧 `SshDataStream.WriteEof`,
套接字侧 `Shutdown(Send)`)—— 做成整条拆链的话,"发完请求就 shutdown 再等响应"的协议全部读不到东西,
这条已有回归测试(`MeteredPortForwardTests.Relay_ForwardsHalfClose`)钉住。
**SOCKS5 服务端的用例喂的是客户端侧 `ProxyStreamConnector.BuildSocks5ConnectRequest` 生成的字节**
——那份实现另有对着 RFC 逐字节的断言(`ProxySupportTests`),拿它当地面真值,
免得服务端与客户端一起跑偏还全绿(ZMODEM 那次的教训,§12-8)。

**B. 流量统计**:行内显示 `3 连接 · 1.4 MB`(有在传的连接时并发数一并点出)。读数由
`ITunnelService.RefreshStatistics()` 从句柄同步到 `TunnelInfo`,面板 5 秒时钟调用;
**停止隧道时先取下最后一次读数再释放句柄**,否则这条隧道跑过多少字节就再也问不到了。

**C. 断线自动恢复**:`TunnelConfig.AutoReconnect`(默认 false,旧配置缺字段即 false),
表单勾选、行内带「自动」徽标。掉线检测覆盖面板持有的**每一台**服务器而非只有选中那台
(隧道跑在后台,用户不会为了让它被照看到而一直停在那个页面上);失败按
10s → 30s → 1min → 2min → 5min 退避。**用户按停过的隧道不自动拉起** ——
`TunnelItemViewModel.StoppedByUser` 记下这一点,"自动重连"扛的是网络抖动,
不是把用户刚按下的停止键撤销掉。

**D. 端口冲突预检**:创建本地/动态转发前比对系统 TCP 监听表,命中抛 `TunnelPortInUseException`。
判定按"端口相同 且(任一方绑 0.0.0.0 或 两者地址相同)"。**探测委托做成可注入**
(`TunnelService` 的 `isLocalPortInUse` 参数)—— 不然 `TunnelServiceTests` 里那些 5432 / 3306
就要看运行机器上恰好有没有数据库在监听,测试时灵时不灵。

**E. 界面文案**:新增 9 个键(`TunnelSvc_LocalPortInUse`、`Tunnel_Stats*`、`Tunnel_Auto*`、
`Msg_TunnelAutoReconnect*`),五份 resx 已齐。

**F. 面板字号上调一档(用户反馈"字太小")**:隧道面板里压在 8/9 两档的次要文字整体上移一档
——徽标 8 → 9(`DESIGN.md` 的阶梯里 9 才是"状态标签"档,8 本就在阶梯之外)、
端点摘要/状态行/统计行/错误行/表单标签/服务器状态行 9 → 10;隧道名(11 Medium)与路由行(10)不动,
层级关系原样保留。同时给路由行补 `TextWrapping="Wrap"` —— 中文文案在 340px 面板里已经铺满一行,
而界面字号是用户可调的,不给换行的话基准字号一调大这行就被硬裁。
`TunnelPanelUiTests` 的视觉 QA 样本补上了统计行与「自动」徽标(设 `VELASHELL_VISUAL_QA_DIR` 出图),
否则截图回归看不到这两处新元素。

## 20. 2026-08-30 消息中心(侧边栏铃铛)

侧边栏底部那枚铃铛此前是**空占位**(`NotificationsCommand = ReactiveCommand.Create(() => { })`,
点了什么都不发生)。现在把它做成消息中心。设计与资讯源契约见
[`velashell-docs zh/host/消息中心与资讯源.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/消息中心与资讯源.md),
这里只记会绊到人的几点。

**A. 边界:装什么、不装什么**。消息中心收的是**要留存、可回看**的东西 —— 有新版本了、
订阅源发来的公告与安全资讯、将来后台推的运营消息。**不收运行时告警**:主机指纹变了会当场弹窗、
会话断了会亮标签写状态栏,那些要的是立即打断,已各有归宿;混进列表只会把真正要读的东西淹掉。
这条边界写在 `Core/Notifications/INotificationCenter` 的类型注释里,加内容源前先读一遍。

**B. 资讯源契约(`Core/Notifications/AnnouncementFeedDocument`)**:这是**后台系统要照着发布的格式**,
字段发出去就不能改名,加能力只能加可选字段。支持按 语言 / 平台 RID / 版本区间 定向投放,
带 `expiresAt` 自动消失。几条硬约束:单次最多 100 条、响应体上限 512 KB、
**外链只放行 https**(内容来自远端,放行 http 等于让投递方把用户导去一条可被中间人改写的链路)、
**一条坏数据不让整个源哑掉**(缺字段的条目单独跳过)。
`AnnouncementFeedDocumentTests` 逐条钉住,那份用例同时也是契约的可执行说明。
**默认不订阅**:`Notifications.FeedUrl` 为空时一个网络请求都不发 ——
终端客户端默默定期外呼在企业环境里是要被问责的事,得由用户或部署方明确开启。
(**08-31 改为默认订阅官方源**,见 §22。)

**C. 快捷跳转走 `ICommandRegistry`**:通知带 `commandId` 就执行注册表里的命令,
带 `url` 就用系统浏览器开(仅 https);**站内优先**,命令没注册(返回 false)时退回外链。
「有可用更新」用的是 `app.settings.about` —— 新增命令,打开设置并**直接落到「关于」页**,
用户就地更新而不是被丢在设置首页自己找。分区定位用新增的 `SettingsSectionKey` 枚举而非下标:
往分区列表中间插一页而忘了同步枚举,跳转会静默跑到隔壁页 —— 不报错、不崩,只是用户点了
"去更新"却看到别的东西。`SettingsSectionKeyTests` 就是为了让那种改动**当场失败**。

**D. 顺手修好一个死开关**:`General.CheckUpdatesOnStartup` 默认 true,却**全仓没有任何消费者**
(设置审计 R-01 判为"更新服务尚未接入"而隐藏)。现在它真正决定启动时查不查新版本,
因而在常规页恢复展示。同批新增的 `Notifications.FeedIntervalHours` 一开始也差点成为死开关 ——
补了周期拉取:计时器按固定半小时跳,真正拉不拉由该设置决定,用户改完下一跳就生效。

**E. 持久化与上限**:SonnetDB 文档集合 `notifications` 单份文档,**跨重启留存**
(与文件传输面板相反 —— 一次传输的进度隔天没有意义,一条公告第二天仍然成立);200 条上限,
超出丢最旧的。**同 id 重投会被跳过且保住已读状态** —— 每次启动都重投同一条"有新版本",
覆盖会把读过的又变回未读,铃铛红点就永远消不掉。

**F. 界面**:360px 非模态浮层,**锚在左下贴着铃铛**(浮层从哪个按钮开出来就该长在哪个按钮旁边)。
未读左侧 2px 强调竖条 + 主文本色标题,已读退成次要色。整行可点 = 跳转并标记已读,
做成透明按钮而非给 Border 挂手势 —— 键盘 Tab 走得到,焦点框与 hover 跟着按钮语义走。
**外链条目把主机名摆在动作旁边**:地址是远端源给的,得让用户在点之前就看见自己会被带去哪。

**G. 界面文案**:新增 24 个键(`Notify_*`、`Cmd_OpenAbout`、`SetGeneral_*` 消息中心分节),五份 resx 已齐。

**未做**:插件发通知(SDK 的 `IUiApi` 只有 `ShowPanelAsync`;宿主侧接口形状已按这个用途定好,
但要开放给插件需改 velashell-plugin-sdk 仓库 + 扩展隔离进程 IPC + 走 SDK 发版流程,是独立一批)、
系统级通知。

**H. 悬停高亮切成两半(用户反馈,同日修复)**:消息行的悬停高亮原先挂在 `Button.row` 上,
而那个按钮只占三列布局里的内容列 —— 删除键那一列不跟着变色,整行被切成深浅两块,
中间那道边界看着就像凭空多了一条竖分割线。改为挂在整行的外层 `Border.msg-row` 上。
**两个 Background 都必须由样式设**:直接在元素上写 `Background="Transparent"` 是 local value,
优先级高于样式,`:pointerover` 再也盖不过去(Avalonia 的属性优先级,不是选择器不够具体)。
回归测试 `NotificationPanelUiTests.Row_HoverHighlight_CoversWholeRow` 把指针停在**删除键那一列**
再断言整行 `IsPointerOver` —— 问题正出在那半边,指在内容区是测不出来的。

## 21. 2026-08-31 补全弹层关不掉(#315)

空行按 `Alt+Enter` 召出「快捷指令 + 最近历史」全量面板后,**Ctrl+C 关不掉、点终端也关不掉**。
两个触发条件是两处独立的缺口,凑在一起正好把这个面板最常见的召出方式变成了单向门:

**A. Ctrl+C —— 事件在跟踪器里被吞掉**:`TerminalInputTracker` 只在「行的字面内容变了」时发
`InputChanged`,而 `ResetToKnownEmpty()` 对一个**本来就是确定空行**的行返回 `false`。
弹层的收口完全挂在 `InputChanged` 上(`TerminalTabView.OnTrackedInputChanged`),于是:
`Alt+Enter` 的主场景恰恰是空行 → Ctrl+C 发出的 `0x03` 走到跟踪器 → 空行清空空行 → 不算变化 →
一拍都不发 → 面板留在屏幕上。行里有字时 Ctrl+C 是好的(内容真的变了),所以这个洞只在空行上露出来。
修法:`0x03`/`0x15` 一律记为变化 —— **「取消当前行」本身就是消费方要感知的事件**,
不是内容差分的副产品。另加视图侧的 `Key.C when Ctrl` 分支直接收口(不置 `Handled`,按键照常下发):
开了「有选区时 Ctrl+C 复制」的用户根本没有字节发往 PTY,跟踪器那条路等不到。

**B. 点击终端 —— 没有任何路径能收口**:弹层刻意不开 light-dismiss(那会吞掉关闭它的那一次点击),
而唯一的指针侧收口是终端的 `LostFocus` —— 点一个**已经聚焦**的终端不产生 `LostFocus`。
补 `TerminalHost` 上的 `PointerPressed`(Tunnel、不置 `Handled`,选区/光标行为不变)。

登记进 `ShortcutCatalog` 的补全分组并同步 `velashell-docs` 的中英快捷键参考(收起建议弹层:
`Esc` / `Ctrl+C` / 左键)。回归测试 `TerminalInputTrackerTests.CtrlC_OnAlreadyEmptyLine_StillRaisesInputChanged`
钉住 A;B 是视图层指针接线,本仓暂无宿主视图的 headless 会话,未加用例。

## 22. 2026-08-31 资讯源默认订阅官方源

`velashell-feeds` 上线后,`Notifications.FeedUrl` 的默认值从空串改为
`NotificationOptions.OfficialFeedUrl`(`https://feeds.easilynet.top/feed.json`)。
理由是安全资讯的价值在于「用户没去找的时候它自己到」:默认关闭的源几乎没人会去打开,
CISA KEV 那几条「现在就有人在打这个洞」就等于谁也收不到。

**这是一次「默认行为」变更,不是一个配置项调整**,因此三处必须跟着改,否则文档开始骗人:

- `PRIVACY.md`(中英两份正文):概述里「开发者不运营任何服务器」不再成立 —— 改成如实说明
  开发者运营**一台**服务器、默认每几小时下载一次公开 JSON、请求里不带任何标识,
  服务端能拿到的只有 IP 与时间,以及「清空地址即彻底不发」。同时在「网络连接」清单里
  新增资讯源一条。**顺手修正了上一批留下的失真**:该清单第 3 条仍写着「检查更新 —— 仅手动
  触发……从不在后台自动检查」,而 08-30 起 `CheckUpdatesOnStartup`(默认开)会在启动时查一次。
- `velashell-docs` 的消息中心文档(中英):默认值表与「留空即不订阅」段落。
- 本文件 §20-B 的「默认不订阅」结论。

**存量用户不受影响**:设置整份 JSON 持久化在 `app_config/settings`,老文档里已经存着
`FeedUrl: ""`,反序列化会把空串照原样带回来 —— 新默认只对全新安装(以及消息中心发布前
从未存过该字段的配置)生效。消息中心尚未随版本发布,因此实际上没有需要迁移的存量。

## 23. 2026-08-31 消息中心:可拖动、加大字号、动作靠右(用户反馈)

**A. 拖拽逻辑抽成一份共用实现**:消息中心成为第二个可拖动浮层,而拖拽那套东西细到
「按在标题栏的按钮上不许起拖」「`Bounds` 不含渲染变换所以它就是锚定位置」这一层 ——
复制第二份必然漂。于是从 `FileTransferView` 原样抽出 `Behaviors/PanelDragHandler`
(捕获/位移/越界夹紧/松手落盘)与 `ViewModels/IDraggablePanel`(两个偏移量 + 落盘),
两个视图各一行 `PanelDragHandler.Attach(this, DragHandle)`。持久化载体
`TransferPanelPosition` 随之更名 `PanelPosition` 并独立成文件:同一个 `ui-layout` 集合里,
文件传输是 `transfer-panel`、消息中心是 `notification-panel`,类型名不该再绑着其中一个。

**B. 拖拽空间是父容器,所以外层 Panel 必须铺满**:`MainWindow.axaml` 里消息中心原先包在一个
`HorizontalAlignment=Left/VerticalAlignment=Bottom` 的 `Panel` 里 —— 那个 Panel 紧紧贴着面板本身,
而 `PanelDragHandler` 的参考坐标系正是父容器,于是可拖范围会是零。改为**外层 Panel 铺满整行、
对齐与边距落到里面的视图上**(与 `FileTransferView` 的摆法一致)。Panel 不设 `Background`
即不参与命中测试,铺满也不会挡住底下的操作 —— 这与标题栏手柄必须显式写
`Background="Transparent"` 是同一条规则的两面。

**C. 字号加大一档**:正文 10→12、标题 11→13、徽标/时间/主机名 9→10、标题栏 11→12,
全部走 `VelaFontSize*` 令牌(仍跟随「设置 → 外观 → 界面字号」缩放)。行高随之增加,
列表 `MaxHeight` 380→440,可见条数与改字号前基本持平;每行的删除键 20→22px、图标 10→12px。

**D. 「去处」一行改为两端对齐**(用户反馈两轮):动作原先贴左边缘,而指针本来就多停在右侧
(每行删除键、滚动条都在那边),一次跳转要横穿整张卡片。第一版直接把整条靠右,用户当场
反馈「参差不齐」—— 确实:主机名长短不一(站内跳转那行根本没有),整条靠右时动作的左沿
一行一个位置。最终做成**两端对齐**:主机名钉左、`动作 + ›` 钉右,两条竖直边都是齐的。

**D-1. 但真正让它参差的是另一件事**:`Button` 的默认 `HorizontalAlignment` 是
**`Left` 而非 `Stretch`**。整行那个透明按钮因此按自己的内容缩成一团(实测三行分别
295 / 284 / 309px,而它那一列是 318px),里面再怎么"靠右"也只是靠在那团东西的右边。
只设 `HorizontalContentAlignment="Stretch"` 不够 —— 那管的是内容在按钮里怎么摆,
管不到按钮自己有多宽。补上 `HorizontalAlignment="Stretch"` 后三行按钮齐齐 318px,
右沿才真的共线。顺带:这也让内容列的空白处变成可点区域,与「整行可点 = 跳转」的原意一致。
(同时给列表显式关掉横向滚动:开着的话每行按"不换行的理想宽度"各量各的,行宽本身就不一样。)

**E. 右留白 16,给悬浮滚动条让位**(用户反馈):列表的悬浮滚动条展开后约 12px,
压在贴着右边缘的每行删除键上。标题栏右内边距与每行删除键的右外边距统一改成 16
(左仍是 12),滚动条出现时既不遮挡,两处的 x 也仍然对齐。

回归测试:`NotificationPanelViewModelTests` 补三条(落盘到 `ui-layout/notification-panel`、
构造时恢复、无存储时不炸);`NotificationPanelUiTests.DestinationLine_ActionsShareOneRightEdge`
在真实 Avalonia 布局里钉住 D/D-1/E —— 用「无主机名 / 短主机名 / 超长主机名」三行,
断言各行等宽、整行按钮铺满内容列、动作右沿共线、删除键与标题栏关闭键共线。
这条用例在写下时**是红的**(实测 314 / 303 / 328),D-1 那行 `Stretch` 才让它变绿,
不是先有结论再补的用例。拖拽手柄本身是指针接线,未加用例 —— 但那份逻辑现在只有一处,
`FileTransferViewModelTests` 的位置用例仍覆盖着它的另一半。

**F. 左下圆角被未读竖条顶方**(用户反馈):列表滚到底时,最后一行的未读竖条(通高实心色块)
一路画到卡片下沿,把 6px 圆角盖成方角。**圆角不会自己裁剪内容** —— 加一层
`CornerRadius=5 + ClipToBounds=True` 的 Border 包住内容:半径 5 = 外层 6 − 1px 描边
(子元素被布局在描边内侧,照抄 6 会盖掉描边的圆弧段,见 `CardCornerRadiusTests` 的既有约定);
而 `ClipToBounds` **不能**写在卡片自己身上 —— 那会把它自己的 `BoxShadow` 一起裁掉。
顺带订正一处过时的注释:`CardCornerRadiusTests` 的说明写着「Avalonia 的 ClipToBounds
只裁矩形边界、不按圆角裁剪子元素」,在 Avalonia 12.1 上**已经不成立**了 ——
本批的像素用例实测它确实按圆角裁。那条注释描述的是当时的版本行为,不是永恒定律。

**G. 投影调软**(用户反馈「优化窗口的阴影效果」):`VelaShadowWindow` 远处那层原来是
**65% 纯黑 + blur 10** —— 又浓又紧,压在暗色终端底上不像投影,像卡片下面糊了一道黑边。
改为 **40% + blur 12 + y4**(亮色同比降档),延展仍是 16(4+12),外边距不用动。
这是全应用共用的唯一投影令牌,自绘窗体、命令面板、隧道面板、传输浮层一起变 ——
浮层的投影语言只有一套,单独给消息中心配一份等于让同屏两个浮层互相打架。
`DESIGN.md` §4.5 与 velashell-docs 的中英设计规格同步改写:此前它们写的是
「小浮层 blur16 #00000060 / 大弹窗 blur32 #00000080」两套规格,而代码里从来只有一个令牌,
值也对不上 —— 现在文档记的是实际令牌值 + 延展 ≤ 16 的约束 + 圆角不自裁的那条坑。

回归测试:`NotificationPanelUiTests.CardBottomCorner_IsNotSquaredByUnreadBar` 直接量渲染帧的
像素 —— 把列表填到卡片下沿,断言左下角圆弧之外那一小块三角区里没有强调色。
两个坑都踩过才写对:①视图若纵向铺满窗口,卡片下沿会落在列表下方的空白处,测了个寂寞,
必须按 MainWindow 的摆法让它按内容收缩;②headless 帧是 **RGBA** 排布,
按 BGRA 读会把强调色的 R/B 读反,断言永远为真(那正是它第一版"通过"的原因)。

## 24. 2026-08-31 会话树状态卡在「连接中」(#321)

在一条**已经连上**的 SSH 会话上再开一个标签(同一条配置),趁它还在握手时立刻关掉 ——
左侧资源管理器里那条会话就永远停在「连接中」,再也不会回到「活跃」。

根因是**树上一个节点、底下多个标签,而状态按「最后一次变更」写**:
`CreateConnectingTab` 给每个标签挂一条 `ConnectionStatus → SetSessionStatus(profile.Id, …)`
的订阅,后开的标签一进来就把节点写成 `Connecting`,盖掉前一个标签的 `Connected`。
关闭那半边同样是错的:`OnDocumentClosed` 判断「同配置还有别的已连接标签吗」,
有就**跳过、不回写** —— 而此刻节点上留着的正是被关掉的那个标签写下的 `Connecting`,
于是这个状态再也走不出去。同一个洞还有第二条更短的路径:连接失败或取消走
`RemoveTerminalTab` 静默摘标签,**根本不经过 `OnDocumentClosed`**,单个标签也能卡住。

改法是把「谁来报状态」从单个标签上收走,改成按配置**合并**:

- `RefreshSessionStatus(profileId)` 扫标签栏里属于该配置的全部终端标签,取优先级最高的一个
  (`Connected > Connecting > Error > Disconnected`,`SessionStatusRank`);一个标签都不剩时归零为
  `Disconnected`。一条连着的会话不该因为旁边多了个正在握手/握手失败的标签而变成「连接中」/「离线」。
- 订阅挪进 `OnTabsCollectionChanged`(`_sessionStatusSubscriptions`,与快捷命令目标订阅同生命周期):
  标签进标签栏时挂上、离开时退订并立刻重算。这样**所有**移除路径(关闭文档、连接失败/取消的静默摘除)
  自动共用一条收口,不必各自记得回写。本地终端标签没有 `Profile`,不参与。
- `OnDocumentClosed` 里那段「还有别的连着就跳过」换成同一个重算调用(幂等兜底:文档关闭时
  标签可能已不在标签栏里)。

回归测试 `SessionTreeStatusFromTabsTests` 四条:第二个标签握手期间节点保持「活跃」→ 关掉它仍是「活跃」
(#321 本体)、唯一标签在握手中被摘掉要回到未连接、一个标签失败不把还连着的配置写成离线、
别的配置的标签不串台。前三条在未修复的代码上是红的。

**留了一处没动**:SFTP / FTP / 插件协议 / 工作台文档也往同一个节点写状态(`SetTreeSessionStatus`),
它们与终端标签之间仍是「后写的赢」。本次只把终端标签这一侧收成合并语义 —— 与既有行为等价,不是新洞,
但要彻底,得把这些来源一起纳入同一个合并器。

## 25. 2026-08-31 具名主题:九套配色 + 终端配色配对

此前"主题"只有三个值:`dark` / `light` / `system`,颜色写死在两份 axaml 的 ThemeDictionaries 里;
终端那边同样只认明暗两套(暗 Dracula / 亮 Solarized Light,硬编码在 `VelaTerminalControl`)。
本批把它改成**具名主题目录**:六套暗色 + 三套亮色,各自带一套配套的终端配色。

**改名**:原亮色 → **VelaLight**,原暗色 → **VelaDark**。持久化的 Id 仍是 `light` / `dark`
—— 老配置一行不用迁,`ThemeServiceSwitchTests` / `HeadlessUiTests` 也不用改。

**新增七套**(名字取自"Vela=船帆座"的星空与自然一系,各有明确的使用场景,不是换个色相凑数):

| 主题 | 基底 | 血统 | 配套终端方案 |
| --- | --- | --- | --- |
| Tokyo Night | 暗 | Tokyo Night | Tokyo Night |
| Nord | 暗 | Nord | Nord |
| Everforest | 暗 | Everforest | Everforest Dark |
| Obsidian | 暗 | 中性近黑(OLED) | Obsidian |
| Gruvbox | 暗 | Gruvbox | Gruvbox Bright |
| Rosé Pine Dawn | 亮 | Rosé Pine Dawn | Rosé Pine Dawn |
| GitHub Light | 亮 | GitHub Light | GitHub Light |

### 一、六十多个令牌,只写二十几个种子色

`UiThemePalette`(Core)是一套主题需要人来定的全部内容 —— 底色阶梯、四档文字、两档描边、
强调色与语义色,二十五个。其余令牌(`*Dim`、`VelaHeat1-5`、`VelaGauge*`、`VelaTrace*`、
`VelaShell*` 等)由 `ThemeTokenApplier`(宿主侧)按固定规则派生。

理由是**手抄会错,而且错了看不出来**:`#644AC922` 这种把透明度写在末尾的错拼,编译期无感、
运行期是一片绿(issue #246 的原始症状)。派生出来的令牌天生自洽,加一套主题只需要填种子色。

**运行时怎么生效**:Avalonia 的资源查找先看字典自身的条目,再看 ThemeDictionaries 与合并字典 ——
所以写进 `Application.Resources` 顶层的键会**遮蔽** axaml 里同名的主题条目,全部
`DynamicResource` 立刻跟着变(强调色覆盖 #3 一直就是这么做的,这次把它推广到整套令牌)。
axaml 那两套保留为 VelaDark / VelaLight 的编译期缺省,`ThemeTokenApplierTests` 钉住两边逐值一致。
这条遮蔽假设本身也有用例:`ThemeTokenShadowingUiTests` 在真 Avalonia 资源栈上验证"贴上去盖得住、
摘下来落得回"。

**踩到的一个坑**:清空自定义强调色的老实现是把 `VelaAccent` 三件套从资源里**删掉**,让它落回
axaml 缺省。在只有明暗两套时这是对的;有了具名主题就成了错的 —— Tokyo Night 的蓝会变成VelaDark 的紫。改为 `ThemeTokenApplier.ResetAccent` 写回**当前主题自己**的强调色。

### 二、终端配色跟着主题走

`VelaTerminalControl` 原来只听 `ActualThemeVariant`。具名主题里有六套暗色,VelaDark 换到
Tokyo Night 时变体压根没变 —— 光听变体,终端画面会原地不动,和换过颜色的界面拼在一起。

改为三层叠加:**控件自带的明暗缺省 → 宿主下发的整套主题配色(新增的 `ThemePalette`)→
用户改过的单色(原有的稀疏 `PaletteOverrides`)**。宿主在 `ApplyLiveTerminalSettings` 里按当前
主题下发,并订阅 `IThemeService.ThemeChanged` 与 `Application.ActualThemeVariantChanged`
(后者是"跟随系统"下系统明暗翻转的那条路)。

配对的硬约束:**方案背景色必须等于该主题的 `VelaBgTerminal`**。亮色主题此前配 Solarized Light
(终端底 #FDF6E3)而界面底是 #FFFBEB,终端边缘一直挂着一道看得见的拼缝 —— 这次 VelaLight 改配
**Alucard**(Dracula 官方亮色),缝消失了。`UiThemeCatalogTests` 逐主题钉住这一条。

新增五套终端方案:Alucard、Everforest Dark、Obsidian、Rosé Pine Dawn、GitHub Light,外加 Gruvbox Bright
—— 最后这套是因为原版 Gruvbox 的 normal 红(#CC241D)压在 #282828 上只有 2.7:1,报错信息比正文
还难读;Gruvbox Bright 的常规八色取官方 bright 一档,原汁原味的 "Gruvbox Dark" 仍留在列表里。

### 三、配色不是"看着顺眼"就算数

`UiThemeCatalogTests` 对**每一套**主题跑:正文两档压在七种底色(含半透明选中底压在浮层上的
观感色)上 ≥ 4.5:1;按钮文字压在强调色上 ≥ 4.5:1;状态点与语义色 ≥ 3:1;强调色淡底的 RGB 必须
等于强调色本身;配套终端方案的前景 ≥ 4.5:1、ANSI 1–6 ≥ 3:1。

这把尺子真的拦下了东西:Nord 原色红压在面板上只有 2.5:1(提亮到 #D4757F)、Rosé Pine 的
iris 压白底只有 3.7:1(压深到 #7C5F9F)、Tokyo Night 的选中底让次要文字掉到 4.3:1(压暗一档)、
Everforest 的选中底同样偏亮。Rosé Pine 原版**没有绿**,补了一支同调森林绿 —— 没有绿,
`ls` 的目录与可执行位就分不开。

### 四、顺手修掉的一个静默 bug

`TerminalPaletteOverrides.IsEmpty` 写的是 `Ansi.Length == 0`,而数组恒为 16 个槽位 ——
于是它**恒为 false**,"一色未改"也被当成有覆盖。叠加一份全 null 的覆盖不改变画面,所以从没露头;
但它让"跟随主题"与"钉死配色"这两种状态在外部无从区分。改为逐槽位判空。

### 五、界面与插件侧

设置 → 外观 → 主题模式的下拉从写死的三个 `ComboBoxItem` 改为绑定 `AvailableThemeNames`
(主题目录 + 末项"跟随系统"),`SetAppear_ThemeDark` / `SetAppear_ThemeLight` 两个 resx 键随之
从五份资源里删除,说明文案改写。终端配色方案下拉的"(默认)"后缀现在跟着**当前主题的配套方案**走。

插件契约里的 `IHostInfo.Theme` 与 `HostEvents.ThemeChanged` 仍然只给 `dark` / `light` / `system`
—— 具名主题不外泄(`UiThemeCatalog.VariantName`)。插件拿到一个没见过的字符串,多半会落到自己的
兜底分支上;而插件真正需要的信息只有明暗。隔离插件的令牌快照走 `PluginThemeTokens`,读的就是
应用资源,自动拿到新主题的颜色,无需改动。

启动读配置时先验 Id 再 `SetTheme`:用新版选过 Tokyo Night 再退回旧版,老版本不认识这个 Id,
`SetTheme` 会抛 —— 验一下就退回默认主题,启动照常。

## 26. 2026-08-31 主题命名收敛 + 「跟随主题」不再是隐式状态(用户反馈)

两处,都是上一节留下的账。

### 一、主题名改回各自调色板本来的名字

上一批给每套主题都套了 `Vela` 前缀(VelaMidnight / VelaGlacier / …),用户反馈**没必要**:
一套源自 Tokyo Night 的配色就叫 Tokyo Night,认得出的名字比统一的品牌前缀有用。现在只有两套
自家配色保留品牌名 —— **VelaDark**(Dracula)与 **VelaLight**(Alucard),其余一律用血统名:

| 主题(Id) | 基底 | 配套终端方案 |
| --- | --- | --- |
| VelaDark(`dark`,出厂默认) | 暗 | Dracula |
| Tokyo Night(`tokyo-night`) | 暗 | Tokyo Night |
| Nord(`nord`) | 暗 | Nord |
| Everforest(`everforest`) | 暗 | Everforest Dark |
| Obsidian(`obsidian`) | 暗 | Obsidian |
| Gruvbox(`gruvbox`) | 暗 | Gruvbox Bright |
| VelaLight(`light`) | 亮 | Alucard |
| Rosé Pine Dawn(`rose-pine-dawn`) | 亮 | Rosé Pine Dawn |
| GitHub Light(`github-light`) | 亮 | GitHub Light |

Id 一并对齐到名字(`midnight` → `tokyo-night` 等);`dark` / `light` 仍是历史值不动。
调过的那套 Gruvbox 终端方案改名 **Gruvbox Bright** —— 它与原版的差别就是常规八色取了官方的
bright 一档,名字直接把这件事说出来,原版 "Gruvbox Dark" 仍在列表里。

### 二、选 Dracula 没反应:「跟随主题」不能再是隐式状态

用户报的:在配套方案不是 Dracula 的主题(Nord、VelaLight…)下,配色方案下拉里选 **Dracula
毫无反应**,终端仍是主题自带的那套,选中项还会自己跳回去。

根因是一处**语义重载**。老实现把「跟随主题」隐式编码成「设置里的颜色 == 出厂默认」,
而出厂默认的色值就是 Dracula:

- `ColorSchemeIndex` 的 setter 里,选中当前主题的配套方案 = 写回出厂值(= Dracula 色值);
- `BuildPaletteOverrides` 与出厂值逐色做差,全同 ⇒ 无覆盖 ⇒ 跟随主题;
- 于是**「用户明确选了 Dracula」与「跟随主题」写出来的设置一模一样**,分不开。

在 VelaDark 上这个洞看不见(它的配套方案本来就是 Dracula,两种解释同色);具名主题一上来,
除 VelaDark 之外的每一套都会踩到。

改法是把跟随与否变成**显式**的一项:

- `AppearanceOptions.TerminalColorsFollowTheme`(`bool?`)。**不给初值**:`null` 表示配置里
  没有这一项(1.4.x 及更早),由 `TerminalColorScheme.FollowsTheme` 按老口径推断
  (颜色 == 出厂 Dracula ⇒ 跟随)。给了初值 `true`,老用户自定义过的配色会被当成跟随态丢掉。
- `BuildPaletteOverrides`:跟随 → 返回 null(一个槽位都不覆盖);不跟随 → **整套**下发,
  不再与出厂值做差。用户在设置页上看到的那套颜色,就是终端要用的那套。
- 下拉首项独立为 **「跟随主题(配套方案名)」**(`SetVm_FollowThemeScheme`,五份 resx 齐),
  其后是全部内置方案,配套的那个仍带「(默认)」后缀标明出处。选首项 = 跟随,
  并把配套方案的色值写进去 —— 下面那几个色块显示的就是屏幕上真正在用的颜色。
- 跟随态下手改前景/背景/光标/选区任一色 = 用户要自己定配色,就此脱离跟随。
  **判定不能反过来问 `FollowsTheme`**:PropertyChanged 到达时新色值已经写进去了,
  而老配置的跟随与否恰恰是按色值推断的 —— 一改就自己翻成「不跟随」,永远看不到
  「改之前在跟随」这个事实。直接置标志,幂等。(这一版第一次写就踩了,用例是红的。)
- 换主题时若处于跟随态,顺带把色块重灌成新主题配套方案的颜色(载入阶段 `_suppressPreview`
  为真,不会改动刚读进来的设置)。

回归用例 `TerminalColorSchemeSelectionTests` 五条:非 Dracula 主题下选 Dracula 真的生效且不跳回、
配套方案本身也能被钉住、选回「跟随主题」清空覆盖并显示配套色、跟随态下改单色即脱离跟随、
老配置(无标志)按老口径判定 —— 最后一条同时钉住「没改过的老用户仍跟随」与
「改过配色的老用户覆盖不丢」。

## 27. 2026-08-31 再补三套主题:One Dark / One Light / Sakura(用户反馈)

用户要「一套主流暗色 + 一套主流亮色 + 一套类似 VS 粉色主题的亮色」。主题数 9 → 12(七暗五亮),
终端方案 14 → 16。

- **One Dark**(`one-dark`)—— 编辑器暗色主题里装机量最大的那一脉(Atom / One Dark Pro),
  蓝强调、灰蓝底,谁都认得出。底色阶梯照它自己那套(`#21252B` 侧栏 / `#282C34` 编辑区),
  配套终端方案 **One Dark** 本来就在内置表里,背景恰好对得上,一行色值都不用新编。
- **One Light**(`one-light`)—— One Dark 的亮色孪生:同一套语法色搬到白底上,
  两套换着用不换性格。新增配套终端方案 **One Light**。
- **Sakura**(`sakura`)—— 粉樱亮色,带粉调的近白底 + 深玫瑰强调(`#C2185B`)。
  新增同名终端方案。

### 两处调过原版的地方,都是被对比度尺子逼出来的

- **One Light 的绿与黄**:原版 `#50A14F` / `#C18401` 压在它自家侧栏底(`#F0F0F1`)上只有
  **2.81:1**,状态点(已连接 / 连接中)在侧栏上认不出来 —— 而侧栏正是状态点唯一出现的地方。
  压深一档到 `#3F8B3E` / `#A87300`,其余照抄。
- **Sakura 的错误色**:粉色主题最容易翻车的地方是把语义色也调得又粉又淡 ——
  一屏字糊在一起,而且「错误」与「强调」两种红分不开。这里错误取偏橙的深红 `#D84315`,
  与玫瑰强调拉开色相;ANSI 十六色也全部压到白底上读得出的深度(1–6 号最低 4.2:1)。

三套都是先过 `UiThemeCatalogTests` 那把尺子才落地的,新增主题的流程也就这一步:
填一组种子色 → 跑用例 → 红了就调。

### 顺带记一笔:出厂强调色会盖掉每套主题自己的强调色

`AppSettings.AccentColor` 的**出厂默认是 `#E91E63`(粉)**,而强调色覆盖的优先级高于主题令牌
(`App.ApplyAccent` 遮蔽 `VelaAccent` 三件套)。也就是说全新安装下,十二套主题的强调色都会被
这一个粉色盖住 —— One Dark 的蓝、Nord 的冰青、Gruvbox 的琥珀都看不见,除非用户手动清空
「设置 → 外观 → 主题色」。

这是本批之前就有的行为(那时只有两套主题,盖掉的只是 Dracula 紫),不在本次改动范围内,
**未动**。要让每套主题的强调色如实生效,把这个默认值改成空串即可(空 = 跟随主题);
存量用户的配置里已经写着 `#E91E63`,不受影响,只影响新装。

## 28. 2026-08-31 切主题发卡(用户反馈:「感觉有点卡,是错觉吗?」)

不是错觉。切一次主题原来要惊动可视树六十多遍,外加把每个终端标签的字体白重设一遍。
用一个临时的 headless 用例量了一下(合成树:400 个带 `DynamicResource` 绑定的控件 + 一个真终端):

| 环节 | 改前 | 改后 |
| --- | --- | --- |
| 贴一套主题令牌 | **40~57 ms** | **1.65 ms** |
| 同样的写入但不挂在可视树上 | 0.04 ms | — |
| 单个令牌写入 | 0.29 ms(×64 ≈ 全部代价) | — |
| 重设同名终端字体 | 12.7 ms(对照:仅重绘 10.4) | 与仅重绘持平 |

### 一、六十多次全树重解析 → 一次整格替换

`ThemeTokenApplier` 原来把六十多个令牌**逐个**写进 `Application.Resources`。资源字典每被写一次
就沿树发一遍变更通知,树上**每一处** `DynamicResource` 都要重新解析 —— 代价与写入次数成正比
(实测单次 0.29 ms × 64)。

改为:令牌先写进一个**游离**字典(0.04 ms,不通知任何人),再整格换进
`Application.Resources.ThemeDictionaries` 当前明暗那一格。查找优先级不变(主题字典高于合并字典,
应用级自有条目仍高于主题字典,所以强调色覆盖照旧生效),但通知从 64 次降到 3 次
(摘旧的、挂新的、认领 Owner)。

`Apply` 因此分成两个:`Fill(字典, 主题)` 只负责把令牌填进给定字典(用例与内部复用),
`Apply(Application, 主题)` 负责建字典 + 换格。

**顺序也调了**:具名主题现在**先贴令牌再换 `RequestedThemeVariant`**。反过来的话,切到那一格时
它还装着上一套主题的调色板,整棵树要先按旧色重解析一遍、下一句再重解析一遍 —— 白多一次全树解析,
还会闪一下旧色。「跟随系统」仍是先定变体(不然不知道该往哪一格贴)。

### 二、字体白重设

`VelaTerminalControl.FontFamily` / `FontSize` 的 setter 无条件 `RecomputeMetrics()` +
`RelayoutFromBounds()` + 重绘 —— 而 `RecomputeMetrics` 会把**字形缓存整个丢掉**,下一帧整屏重新塑形。
宿主的「把当前设置刷到所有终端」是一条通用路径(保存设置、换主题、插件面板都会走),字体多半根本没变。
两个 setter 各加一句相等判断即可,每个标签省下 2~7 ms。

### 三、跨明暗切主题时,终端设置刷了两遍

`MainWindowViewModel` 既订阅 `IThemeService.ThemeChanged`,又订阅
`Application.ActualThemeVariantChanged`(为了「跟随系统」下系统明暗翻转)。选具名主题跨明暗时
两条都会响,于是所有终端标签的设置被刷两遍。后者加一个判断:只在当前是「跟随系统」时才处理。

### 四、留了一条回归保险

`ThemeTokenShadowingUiTests.ApplyingATheme_NotifiesTheTreeAConstantNumberOfTimes` 数的是**通知次数**
(≤ 4)而不是耗时 —— 耗时断言在 CI 上必然抖,而次数一旦回到逐个写就会线性涨到六十多,一测就红。
