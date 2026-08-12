# VelaShell 项目进展与参考文档

> 本文件记录已完成的工作、当前架构、关键文件索引与后续待办,供后续开发参考。
> 最近更新:**2026-07-22**(§17:SSH 传输层由 SSH.NET 迁移到 Tmds.Ssh、自研 ZMODEM(rz/sz)、独立 SFTP 标签与本地/远程双栏、SFTP 断点续传、远程文件编辑器语法高亮;§1/§2/§3/§4/§7/§10/§12 按现状勘误)。
> 上一版:2026-07-14(§16:自研 VelaDock 落地合并、主窗自绘无边框标题栏补齐 Win11 原生贴靠手感(WndProc)、终端行号/时间侧栏、本地终端 Job Object 秒杀进程树、Windows MSI 安装包与自定义安装目录、集中式包管理、Avalonia 12.1.0、全项目 XML 注释与每项目 README)。
> 上一版:2026-07-12(§13:设置审计整改三批完成;新特性 —— 主机指纹三选项确认与已信任主机管理、GitHub Gist 云同步、会话录制与回放、支持与捐赠页、双许可 AGPL-3.0 + 商业授权、终端配色随主题联动;测试计数见 §7)。

## 1. 技术栈现状

| 项       | 版本/说明                                                                                                                                            |
| -------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET     | **net11.0**(2026-07 由 net10.0 切入;`global.json` 锁 `11.0.0` + `rollForward: latestFeature`,实际以 `11.0.100-preview.x` 构建。`Directory.Build.props` 对 net11 开启 `EnablePreviewFeatures` + `Features=runtime-async=on`,并 `NoWarn` 掉 CA2252/SYSLIB5007;`LangVersion=preview`) |
| UI 框架  | **Avalonia 12.1.0**(已从 11.x → 12.0.5 → 12.1.0)                                                                                                     |
| MVVM     | ReactiveUI 23.2.28 / ReactiveUI.Avalonia 12.0.3                                                                                                      |
| 停靠框架 | **自研 VelaDock**(`src/VelaShell/Docking/`,零第三方依赖;已替换 Dock.Avalonia,见 `docs/dock-replacement-plan.md`)                                     |
| SSH/SFTP | **Tmds.Ssh 0.23.0**(全托管 async-first;2026-07 由 SSH.NET 迁入,库类型只在 `Infrastructure/Ssh/` 出现,异常经 `TmdsSshInterop` 翻译为 `VelaSsh*Exception`) |
| 持久化   | **SonnetDB.Core 3.0.1 嵌入式多模型数据库**(`%LocalAppData%/VelaShell/sonnetdb`;文档集合 + 时序 measurement;旧 JSON 首次运行一次性导入;LiteDB 已移除) |
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

- **模型层** `Docking/Model/`(纯 INPC,可单测):`DockWorkspace`(结构操作 + `DocumentClosed`/`ActiveDocumentChanged` 事件)、`DockGroup`(标签组,主组不折叠)、`DockSplit`(分栏树)、`DockDocument`;空的次级组自动折叠、单子分栏自动提升。方案与集成面分析见 `docs/dock-replacement-plan.md`。
- **控件层** `Docking/Controls/`:`DockWorkspaceControl`(按树渲染 Grid+GridSplitter,star ↔ Proportion 回写;**按文档缓存视图**,切标签复用同一 `TerminalTabView`,取代原 ControlRecycling)、`DockGroupControl`(标签条 + 溢出三连钮 + 标签列表下拉)、`DockTabItem`(标签视觉 + 右键菜单:关闭系列/水平垂直拆分/标签位置)、`DockDragController` + `DockDropOverlay`(拖拽重排插入线、跨组并入、五区拖放分屏,Esc 取消;浮动窗口按产品决策不存在)。
- `Docking/TerminalDocument.cs` 包装 `TerminalTabViewModel`,实现 `IDockViewProvider` 自建视图。
- `MainWindow.axaml` 用 `<dockc:DockWorkspaceControl Workspace="{Binding Layout}" />` 承载;`TabBar`(Ctrl+Tab/W 逻辑集合)与工作区激活态**双向同步**(原 Dock 集成缺 TabBar→文档区半边)。
- `Controls/ReparentingHost.cs` — 沿用:内容宿主挂缓存视图前先从旧父级摘除,保证共享终端控件任一时刻只有一个父级。
- `Themes/DockStyles.axaml` 保留全局通用样式(ToolTip/ContextMenu/MenuFlyout/tab-nav 等);标签视觉内联在 `DockTabItem.axaml`。

## 6. UI / 视图与设置

- **状态栏跟随激活 Tab**:每个 `TerminalTabViewModel` 携带 `ConnectionSummary/TerminalTypeName/EncodingName`;`UpdateStatusBarForActiveTab` 投影连接串/状态/类型/编码/尺寸/延迟;订阅 `ActiveTerminalTab` 变化 + Dock `ActiveDockableChanged`/`FocusedDockableChanged` → 切换标签/窗格实时更新左下角。
- **窗口壳:自绘无边框标题栏(2026-07-13 定稿)**:主窗 `WindowDecorations="None"`(与全部对话框同款全自绘模式);`Views/TitleBarView` 自绘 36px 标题栏 —— 左 logo+产品名,右 全局功能图标组(搜索/复制/分屏/隧道/命令面板,经命令注册表;组同步/广播未实现,`IsEnabled=False` 半透明禁用)+ 最小化/最大化/关闭三枚窗口控制按钮(46×35,关闭 hover #E81123)。**并非回退原生 chrome** —— Avalonia 12.x 的 `ExtendClientArea`/`WindowDecorationsElementRole` 托管装饰在 Win32 上会拦截标题栏输入(按钮点不动、窗口拖不动),整套机制不可用故弃用;改以**自绘 + 原生行为补齐**:空白区 `BeginMoveDrag`(原生移动循环,Win11 边缘贴靠有效)、双击切最大化;**Win11 Snap Layouts 经 `MainWindow` 的 WndProc 钩子处理 `HTMAXBUTTON`**(提交 `ce71b32`,`nc-hover` 类由 NC 消息挂/摘);窗口四周 5px + 四角 10px 自绘缩放抓取区(`BeginResizeDrag`,最大化时关闭)。**文字菜单(会话/编辑/…)已整体移除**——与命令面板功能重复(用户决策);随之移除设置里的"显示菜单栏"开关(`ShowMenuBar` 存储字段保留兼容)。
  - **踩坑备忘(自绘壳为何不走 extend/原生 chrome,Avalonia 12.0.5 观察)**:①`VisualRoot as Window` 恒为 null(视觉根是 TopLevelHost),取窗口必须走逻辑树 `FindLogicalAncestorOfType<Window>()`——曾令标题栏按钮/拖动看似"无输入"数小时;②`ExtendClientAreaToDecorationsHint`/`BorderOnly` 的托管装饰(`WindowDrawnDecorations`)会绘制重复标题与含"全屏"的按钮,且 `WindowDecorationsElementRole` 的输入重定向未落地(HT\*BUTTON 点击无动作、User 角色不可点),BorderOnly 还丢 WS_CAPTION(HTCAPTION 拖动与最小/最大化动画失效,issue #21160/#21212)——整套 extend 机制在 12.0.5 不可用,故弃用。
- **命令面板(Ctrl+P / Ctrl+K)**:`ViewModels/CommandPaletteItem.cs`(+Group)、`CommandPaletteViewModel.cs`(模糊子序列搜索、分类分组、上下循环导航、执行/关闭)、`Views/CommandPaletteView.axaml(.cs)`;`MainWindow` 半透明遮罩浮层,条目=最近会话(Enter 连接)+ 全局命令。
- **终端类型/编码设置项**:`AppSettings.TerminalType`(默认 xterm-256color)/`TerminalEncoding`(默认 UTF-8);`SettingsViewModel`/`SettingsView` 两个下拉;`Program.cs` 注册 `CodePagesEncodingProvider`(GBK/Big5);连接时 `MainWindowViewModel.ConfigureTerminal` 应用到 PTY 的 TERM 与控件。`ISettingsService`/`JsonDataStore` 已入 DI。
- 快捷命令面板、隧道管理面板此前已有完整 View+VM。
- **设置窗口现为 11 页**(2026-07-12,840×740):常规 / 外观 / 终端 / 密钥管理 / 快捷键参考(纯展示) / 文件传输 / 安全审计(含会话录制与已信任主机) / 代码片段 / 云同步 / 关于(含贡献者) / 支持与捐赠;新增页与整改详情见 §13 与 `docs/settings-audit.md`。
- **终端配色跟随主题**:未自定义时 暗=Dracula / 亮=Solarized Light 实时切换;配色方案下拉的“(默认)”后缀与选中项随主题动态联动,选默认方案 = 恢复出厂跟随态。

## 7. 测试(已全量迁移到 MSTest)

- 6 个测试项目。**规模(2026-07-22 静态计数):968 个 `[TestMethod]` + 164 个 `[DataRow]`**,较 2026-07-12 的 ≈606 大幅增长,主要来自 ZMODEM 协议套件、SFTP 双栏与传输续传、自更新链路与 headless 视图测试。精确通过数以 `dotnet test` 为准。
- **已知失败(非回归)**:① `ConPty_SpawnsShell_HandshakesAndSignalsEof` 环境相关(本机无头 ConPTY 不出帧,干净工作树同样失败);② QuickCommands/命令建议相关 12 个 —— 测试期望 11 个内置命令(含 htop)而 `QuickCommandCatalog` 只有 8 个,**测试与目录不同步,待对齐**(见 §13 遗留)。
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
| 标签栏位置(顶部/底部)                         | 外观     | 标签条由 Dock.Avalonia 的 DocumentDock 模板提供,需覆写其主题模板才能移到底部                                                                                                                                                |
| 启动时自动检查 / 自动下载                     | 常规     | 手动"检查更新"已重写为自研便携式自更新(2026-07-17,Velopack 已移除);更新频道 stable/preview 已实际生效(`channelProvider` 读设置)。剩两项**设置页开关**待做:启动后台静默检查、自动下载;核心 `UpdateService` 已就绪,接开关即可 |
| 主密码保护                                    | 常规     | 需主密码派生密钥替换 `AesSecretProtector` 的本机密钥文件 + 启动解锁弹窗 + 密文迁移,安全敏感需单独设计                                                                                                                       |
| ~~断点续传~~ / 自动续传 / 传输重试 / 临时文件清理 | 文件传输 | ✅ **断点续传已实现**(2026-07 迁 Tmds.Ssh 后,`SftpService` 上传/下载双向按偏移续写,并在续传前以尾部 64KB 比对**核实起点**、带安全回退窗口,核实不通过则整份重传)。剩余的自动续传、失败重试与 `.part` 临时文件清理仍需传输队列持久化 |
| ~~会话录制 / 输入脱敏~~                       | 安全审计 | ✅ 2026-07-12 录制与回放已实现(SonnetDB 时序 + 回放中心,见 §13);输入脱敏确认不做(仅录输出流,密码无回显)                                                                                                                     |
| 自动加载密钥到 Agent                          | 密钥管理 | 需集成 Windows OpenSSH ssh-agent(named pipe 协议)或 Pageant                                                                                                                                                                 |

❌ **确认当前架构不实现,已从设置界面与 `AppSettings` 移除**(2026-07-10,见 docs/架构设计.md §11):连字 Ligatures(自绘渲染器按单元格排版,无法跨字符连字)、自适应标题栏颜色(系统原生标题栏由 OS 托管)、系统通知 Toast(需 AppUserModelID/通知框架;常规页用「声音提示」、安全审计页告警通道改为「提示音」`Security.AlertSound` 替代)。
✅ **上传方向冲突策略已实现**(2026-07-10):上传前 `ISftpService.ExistsAsync` stat 远端同名文件,按策略询问(弹窗:覆盖 or 跳过)/覆盖/跳过/重命名(`file (1).txt` 取首个可用名);「覆盖」策略下不额外 stat,沿用 SFTP 覆盖语义;编辑器保存回传属有意覆盖,不走冲突检查。

**B. 功能缺口**

- 非 SSH 协议:**SFTP 已开放**(`ConnectionType.SFTP`,独立 SFTP 标签,见 §12-14);连接弹窗 Telnet/串口 标签仍禁用——可行性与改造清单见 [`docs/Telnet与串口可行性调研.md`](docs/Telnet与串口可行性调研.md)(结论:终端栈已为此预留,`IByteDuplex` 与本地化键均已就位)。第 2 步"证书"认证仍禁用。
- ✅ 快捷键展示表已与真实绑定逐条核对重建(2026-07-11,删除虚构项、补 Ctrl+N 绑定);**自定义键位确认不做**(产品决定,页面定位为"快捷键参考")。
- 密钥生成仅 RSA(PEM+OpenSSH 公钥);ed25519 生成缺失(.NET 无内置 OpenSSH ed25519 私钥导出,需自行实现 OpenSSH 私钥封装或引入 BouncyCastle);导入不校验私钥有效性;删除无二次确认。
- 审计日志已在写(connect/connect-failed),但**无查看界面**;`audit_log`/`conn_history` 无保留策略(retention),长期运行会累积。
- ✅ 配置导出文案已修正为"仅应用设置"(2026-07-11,settings-audit C-08);全量选择性导出待做——注:**Gist 云同步(§13)已覆盖设置/连接/隧道/片段的跨设备迁移场景**。
- 命令面板"会话"类目仅最近连接,不含全部已保存会话。
- ✅ 关于页"检查更新"已重写为**自研便携式自更新**(2026-07-17,Velopack 已移除):`UpdateService` → GitHub Releases `latest.json` 清单(stable 走 `releases/latest/download` 固定地址不占 API 配额,preview 走 API 列表),按 RID 选包 → 下载(进度)→ SHA-256 校验 → `UpdateApplier` 原地换版(`.new` 暂存 + 两段重命名,只动包内文件)→ `--after-update` 重启(等待单实例锁),下次启动清理 `*.old` / 崩溃回滚(`App/Services/Update/`)。便携版任意目录可更新;目录不可写(如 Program Files MSI)提示手动下载;更新通道跟随设置页 stable/preview 开关。"更新日志"仍禁用。
- ✅ 会话录制与回放已组件化(2026-07-12,§13);✅ 主机信任中心以"安全审计 → 已信任主机"落地(查看/删除/地址脱敏)。系统资源监控面板、连接诊断中心、运维编排中心、文件传输 toast 等设计稿面板状态不变。

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
9. **SSH config 导入**:解析 `~/.ssh/config`(Host/HostName/Port/User/IdentityFile/ProxyJump)批量导入会话,降低迁移成本。
10. ❌**连接代理**(2026-07-22 复核:**此前的 ✅ 标记有误,现无实现**):SOCKS5/HTTP 代理经由连接(公司内网出网场景)。原方案依赖 SSH.NET `ConnectionInfo` 的 `ProxyTypes`,迁到 Tmds.Ssh 后已无对应通路;`SessionProfile`/`ConnectionInfo` 上也从未有过代理字段。注意别与**动态 SOCKS 转发**(`-D`,`TmdsSshPortForwardHandle.StartSocksForwardAsync`)混淆——那是隧道功能,方向相反。
11. **防空闲断开(Anti-idle)**:按间隔发送自定义串(如 `\0` 或空格),与已实现的 SSH keepalive 互补(keepalive 防 NAT 超时,anti-idle 防服务端 shell 超时踢出)。
12. ✅**known_hosts 管理界面**:已落地为 设置 → 安全审计 → 已信任主机(2026-07-12,列出/删除/截图防泄露地址脱敏);导出未做。
13. ✅**会话标签自定义颜色/图标**:多环境(生产红/测试绿)一眼区分;SessionProfile 加 color 字段 + 标签条着色。

**P3 —— 锦上添花**
14. ✅**SFTP 本地/远程双栏**:已落地为独立 SFTP 标签(`ConnectionType.SFTP` + `Docking/SftpDocument`),`SftpDocumentView` 左 `LocalFilePaneView` / 右 `FileBrowserView`,支持双栏互拖与 OS→远端拖放。**剩余差距逐项列在 [`docs/SFTP双栏与WinSCP差距分析.md`](docs/SFTP双栏与WinSCP差距分析.md)**(该文把差距分为「接线债 / 能力缺失 / 架构级缺失」三类,不要混在一起排期)。
15. **用户自定义关键字高亮规则**:语义高亮已内置(URL/IP/错误词),开放用户正则+颜色规则表(WindTerm 卖点)。
16. ✅**命令自动补全/历史建议**:输入时基于本地命令历史悬浮建议(WindTerm 式);已有 quick_commands 可作为数据源之一。
17. **OSC 52 剪贴板**:远端 vim/tmux 写系统剪贴板(§8 遗留,一并归档到此)。
18. **触发器/自动应答**:输出匹配正则时自动发送响应(expect 式),如自动 yes/密码带外输入。
19. **多窗口**:新开独立主窗口(目前只有单窗口+分屏/浮动 Dock)。
20. **Mosh / SSH 证书(certificate)认证**:弱网漫游与企业 CA 场景,按需求评估。

## 13. 2026-07-11 ~ 07-12 批次(设置审计整改 + 四个新特性)

**A. 设置审计整改**(台账与逐项状态见 `docs/settings-audit.md`,共三批):
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
- **Windows 安装包(2026-07-13;2026-07-17 调整)**:Velopack `Setup.exe` 链路已整体移除——其默认安装目录 `%LocalAppData%\VelaShell` 与应用数据根目录相同,卸载即清空用户数据,且自打的便携 zip 无法经 Velopack 更新。现行方案:便携 zip + 自研应用内自更新(任意目录原地换版)。WiX v4 MSI 定义(`installer/VelaShell.wxs`,x64/arm64,`WixUI_InstallDir` 中文向导支持自定义安装目录,静默安装 `msiexec /i VelaShell.msi /qn INSTALLFOLDER="D:\Tools\VelaShell"`;`ProductVersion` 须为纯数字 x.y.z,`UpgradeCode` 固定走 MajorUpgrade)保留可手动构建,不再随 CI 发布;MSI 装进 Program Files 后应用内更新按"目录不可写"如实提示手动下载。

## 16. 2026-07-13 ~ 07-14 批次(VelaDock 合并、原生窗口壳、终端侧栏、安装包、工程化)

> 本批以数个独立 PR 合入 `dev`/`main`(#3 replacedock、#5、#6)。多为架构/工程化收尾与使用体验修正。

**A. 自研 VelaDock 正式落地(PR #3)**:详见 §5 与 `docs/dock-replacement-plan.md`(已补「已完成」横幅)。模型/控件/拖拽全套自研替换 `Dock.Avalonia`,零第三方停靠依赖;拆分对所有标签组一致生效(单标签次级组也可水平/垂直拆分);点击窗格内容区即激活该组文档(SFTP 面板与状态栏随焦点窗格切换)。关于页开源许可列表已删 Dock.Avalonia 条目。

**B. 主窗自绘无边框标题栏 + 原生行为补齐(体验优化)**:详见 §6。主窗保持 `WindowDecorations="None"` 全自绘(`TitleBarView` 含 logo/名称 + 功能图标组 + 自绘 min/max/close),**未走原生 chrome**(extend/角色重定向在 Win32 不可用);改以 `BeginMoveDrag` 原生移动循环 + **WndProc 钩子处理 `HTMAXBUTTON` 实现 Win11 Snap Layouts**(`ce71b32`);并修 `c12a8ff`(Avalonia 12 `VisualRoot` 非 `Window`,取窗口须走逻辑树 `FindLogicalAncestorOfType<Window>`)。中途 `7580052` 试过原生 chrome 集成,因保真问题走回自绘+WndProc 方案。对话框同为自绘无边框。

**C. 终端行号 / 时间侧栏(gutter)**:`Terminal/Rendering/GutterLayout.cs` + `GutterFoldModel.cs` 为终端左侧新增**行号**与**时间戳**两列侧栏,各自独立开关、支持快捷键切换;含折叠标记与空白间隔,折叠模型重构以增强可测性(`GutterFoldTests`/`GutterLayoutTests`/`GutterFoldUiTests`/`LineTimestampTests`)。

**D. 本地终端进程树秒杀**:`ConPtyShellStream` 引入 **Windows Job Object**,关闭标签/退出时连带杀掉子进程树(如 WSL、pwsh 派生进程),优化关闭体验,避免孤儿进程。

**E. 交互式提示下的补全判定**:密码类提示行(sudo/密码提示关键词命中)**不再弹出命令补全**弹层(sudo 密码提示下按键误弹智能提示);新增交互式判定单元测试。

**F. 工程化收尾**:①**集中式包管理** `src/Directory.Packages.props`(`ManagePackageVersionsCentrally`,各 csproj 只写包名不写版本)+ 构建系统重构;②**Avalonia 12.0.5 → 12.1.0** 升级;③**全项目补充详细 XML 注释**(`GenerateDocumentationFile`);④为**每个 src / tests 项目新增独立 `README.md`**(架构、目录职责、依赖关系),根 `README.md` 与 `docs/architecture.md`/`架构设计.md` 同步刷新至当前状态(版本、VelaDock、原生标题栏、五语言、发布/安装包、命名 `Pulse*`→`Vela*`)。

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

**C. 独立 SFTP 标签 + 本地/远程双栏 + 断点续传**:见 §12-14 与 §10.A;差距清单见 `docs/SFTP双栏与WinSCP差距分析.md`。

**D. 远程文件编辑器语法高亮**:`App/Services/Syntax/`(`FileTypeDetector` + `SyntaxHighlightingService`)接 AvaloniaEdit,按扩展名对常见文件类型着色;保存即经 SFTP 回传(有意覆盖,不走冲突检查)。

**E. net10 → net11**:`Directory.Build.props` 统一切到 `net11.0`(全仓一处),`global.json` 锁 `11.0.0` + `rollForward: latestFeature`;对 net11 开启 `EnablePreviewFeatures` 与 `Features=runtime-async=on`。**代价**:构建依赖 .NET 11 预览版 SDK,全平台发布需实机冒烟;回退 net10(LTS)仍是一行改动。

**F. 拖入文件夹的异常风暴修复(2026-07-22)**:把一个文件夹拖进文件浏览器时,调试输出会被 `ConnectFailedException` / `VelaSshConnectionException` 刷屏。三处根因:

1. **`AutoConnect` 默认为 true**。`BuildSshClientSettings` 从未设置它,于是会话掉线后**每一次** SFTP 操作都会各自静默重连一次,失败抛一发 `ConnectFailedException`——这与同文件里"主连接不在时不得偷偷另建连接"的注释意图直接矛盾。现显式 `AutoConnect = false`,连接只由 `TmdsSshClientWrapper.ConnectAsync` 发起。
2. **续传探测逐文件打远端**。`TryResumeAsync` 走裸 `ExistsAsync`,绕开了本批已经预列举好的目录名单(`RemoteExistsAsync`,零往返)。拖入 N 个文件 = N 次额外往返,叠加第 1 点就是 N 次隐式重连。现已改走名单。
3. **“覆盖”策略下不列举目录**,导致开启断点续传时第 2 点退化回逐文件探测。现改为「覆盖 **且** 不续传」才跳过列举。

**G. 文档**:新增 `docs/SFTP双栏与WinSCP差距分析.md` 与 `docs/Telnet与串口可行性调研.md`(均为 2026-07-22 的决策清单,标注了每项的实现代价与"未核实"项)。
