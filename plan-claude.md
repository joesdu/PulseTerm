# VelaShell 优化事项清单(plan-claude.md)

> 2026-09-05 对 `dev` 分支(`8862789`)做的一次全仓审查,覆盖 **性能 / 功能 / UI 交互 / 工程质量** 四个维度。
> 每一项都附「证据」(文件:行),可以直接跳过去核对;「建议」只给方向与验收口径,不替代设计。
> 本文**不重复** `plan.md` 已登记且仍未落地的事项,只在末尾附一个索引(§7)方便一起排期。
> 读完后按 §6 的批次决定做不做、先做哪些。

---

## 0. 审查范围、方法与基线

**范围**:`src/` 七个工程(约 9.7 万行 C# + 1.4 万行 XAML)、`plugins/VelaShell.Plugin.Ai`(2.7 万行)、`tests/`(303 个文件、2581 个 `[TestMethod]`)、构建与发布配置、`.github/`。
**方法**:静态阅读热路径(终端渲染、输出桥、VT 解析、SFTP 列举、设置读取、轮询计时器)+ 全仓模式扫描(阻塞调用、`async void`、非虚拟化列表、颜色字面量、无障碍属性、日志)+ 一次完整构建与测试运行。**没有**做真机剖析(profiler),凡是标了「需实测」的收益数字都只是经验估计。

**基线(2026-09-05 本机 Windows 11,.NET 11 preview)**:

| 项 | 结果 |
| --- | --- |
| `dotnet build VelaShell.slnx` | 成功,0 警告,0 错误 |
| `dotnet test VelaShell.slnx`(第 1 轮) | 2780 通过,4 跳过,**1 失败**(`VelaShell.Infrastructure.Tests`,-v q 下未打印用例名) |
| `VelaShell.Infrastructure.Tests` 单独重跑 | 361 通过,2 跳过,0 失败 |
| `dotnet test VelaShell.slnx`(第 2 轮,带 trx) | 2781 通过,4 跳过,0 失败 → 第 1 轮那条是**偶发失败**(见 Q-06) |
| `VelaShell.Plugin.Ai.Tests` | 559 通过,但耗时 2 分 30 秒,是全部测试工程总时长的 ~70%(见 Q-06) |

先说结论:这个代码库的**核心热路径质量很高**——GlyphRun 合批、画刷/字形缓存、16 字节单元格、回滚头指针裁剪、输出合批泵、主题整格替换、录制攒批、进程列表 diff-merge,都是对症的优化,而且几乎每处都有注释说明为什么。下面列的优化点多数是「已经做对了的地方旁边还有一块没做」,以及产品层面的缺口。

---

## 1. 总览

优先级:**P0** 用户可感知的卡顿/错误或安全性;**P1** 明显收益、低风险;**P2** 锦上添花或需要先量化。
工作量:**XS** < 半天,**S** 1–2 天,**M** 3–5 天,**L** > 1 周。

| 编号 | 类别 | 标题 | 优先级 | 工作量 |
| --- | --- | --- | --- | --- |
| P-01 | 性能 | 终端输出洪流:UI 线程单次 Feed 无上限、读线程无背压 | P0 | M |
| P-02 | 性能 | 非默认背景逐格 `FillRectangle`,没有像字形那样合批 | P1 | S |
| P-03 | 性能 | 状态栏每秒一次 SSH exec 探测 | P1 | M |
| P-04 | 性能 | 每次读设置 = 整份 `AppSettings` JSON 反序列化;传输逐文件读、HTTP 逐请求同步读 | P1 | S |
| P-05 | 性能 | 启动路径上的同步 IO 与串行等待 | P1 | M |
| P-06 | 性能 | 资源监视器每个 tick 重建全部行对象 | P1 | S |
| P-07 | 性能 | 回滚缓冲整行满宽存储,上限 20 万行,设置无数值钳制 | P1 | M |
| P-08 | 性能 | VT 解析器逐字符状态机,Ground 态无批量打印快路径 | P2 | M |
| P-09 | 性能 | ~~关标签时 UI 线程同步等待读/写任务最多 3 秒~~ **撤回**(见 change-claude.md §0.2) | — | — |
| P-10 | 性能 | 没有性能基准工程,吞吐/分配回归靠肉眼 | P2 | M |
| F-01 | 功能 | 终端内搜索只有不区分大小写的纯文本 | P1 | S |
| F-02 | 功能 | 备用屏无 Alternate Scroll(滚轮 → 方向键) | P1 | S |
| F-03 | 功能 | 「键盘优先」产品缺核心键位:分屏、跳标签、缩放、窗格移焦、清屏 | P0 | S |
| F-04 | 功能 | 会话树没有过滤/搜索框 | P1 | S |
| F-05 | 功能 | 命令面板无相关度排序、无命中高亮、无最近使用加权 | P1 | S |
| F-06 | 功能 | 连接配置缺会话级覆盖项;「标签自定义颜色」实际是按 id 哈希自动配色 | P1 | M |
| F-07 | 功能 | 关闭已连接标签无确认 | P1 | S |
| F-08 | 功能 | 睡眠唤醒 / 网络切换后不主动重连;重连间隔固定无退避 | P1 | S |
| F-09 | 功能 | 本地文件面板不监听目录变化 | P2 | S |
| F-10 | 功能 | 内置远程编辑器缺查找替换、编码选择、自动换行、大文件保护 | P1 | S–M |
| F-11 | 功能 | 键盘交互式认证(2FA/OTP)无实现 | P2 | M–L |
| U-01 | UI | 无障碍与键盘可达性几乎空白 | P1 | M |
| U-02 | UI | 27 处 XAML 颜色字面量 + 3 个 C# 固定 Dracula 配色,亮色主题下失配 | P1 | S–M |
| U-03 | UI | 侧栏底部用户名写死为 `root` | P0 | XS |
| U-04 | UI | 设置窗口一次实例化全部 12 页 | P2 | S |
| U-05 | UI | 状态栏信息密度低、不可点击 | P2 | S |
| U-06 | UI | 终端链接无悬停反馈(光标/提示) | P2 | S |
| U-07 | UI | 可能变长的列表用了非虚拟化 `ItemsControl` | P2 | S |
| U-08 | UI | 崩溃与错误只写 `Trace`,发布版无日志落盘、无「打开日志」入口 | P0 | S |
| U-09 | UI | 连接对话框无即时字段校验 | P2 | S |
| U-10 | UI | 状态栏文字是主要反馈通道,断线/告警/倒计时互相覆盖 | P2 | M |
| Q-01 | 工程 | 六个 God 类(最大 4837 行) | P1 | L(分批) |
| Q-02 | 工程 | `TabBarViewModel` 与 `DockWorkspace` 双模型并存 | P2 | M |
| Q-03 | 工程 | CI 只有发布流水线,PR 不跑构建/测试 | P0 | S |
| Q-04 | 工程 | 33 处 `async void` 事件处理器 + 18 处静默 `catch {}` | P2 | S |
| Q-05 | 工程 | 遗留死代码(`ScrollbackBuffer` / `TerminalLine`) | P2 | XS |
| Q-06 | 工程 | 一条偶发失败的测试;AI 插件测试占总时长 70% | P1 | S |
| Q-07 | 工程 | `plan.md` 两处与实现不符 | P2 | XS |

---

## 2. 性能优化

### P-01 终端输出洪流:UI 线程单次 Feed 无上限、读线程无背压 — P0 / M

**证据**
- `src/VelaShell.Terminal/SshTerminalBridge.cs:402` `EnqueueForFeed` 把每个读取块无界地追加进 `_pending`;
- `:420` `FlushPending` 把两次 UI 回调之间攒下的**全部**块拼成一个缓冲,一次 `Feed` 交给模拟器;
- 读循环(`ReadLoopAsync`)对 `_pending` 的体积没有任何等待,只要网络给得快就一直收。

**问题**
合批本身是对的(它把上百次跨线程跳转压成每帧一次),但没有上限。`cat` 一个几百 MB 的文件、或者 `tail -f` 一个刷得很猛的日志,两帧之间可能攒下几十 MB,UI 线程在一个 Dispatcher 回调里把它们全部解析完——期间界面冻结、滚动条不响应、别的标签也不刷新;内存则随读取速度无限增长(每块租自 ArrayPool,但池只是延迟归还,不限制总量)。

**建议**
1. `FlushPending` 给每帧一个**解析预算**(例如 1–2 MB 或 8 ms 时间片,两者取先到者),剩余块留在 `_pending`,重新 Post 一次;
2. 读线程在 `_pending` 总字节超过阈值(例如 8 MB)时 `await` 一个信号量,由 `FlushPending` 消费后释放——SSH 流控会把压力自然回传给远端,这正是 OpenSSH 客户端的行为;
3. 可选:预算内仍然过大时,同一帧只 `Feed` 不 `InvalidateTerminal`,让重绘按帧率走。

**验收**:新增用例往桥里灌 100 MB 突发输出,断言单次 UI 回调处理的字节数 ≤ 预算、`_pending` 峰值 ≤ 阈值;真机 `cat` 大文件时滚动条与标签切换保持可用。

### P-02 非默认背景逐格 `FillRectangle`,没有像字形那样合批 — P1 / S

**证据**:`src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs:2229`,`RenderLine` 对每个背景 ≠ 默认色的单元格调用一次 `context.FillRectangle`。同一函数里字形已经通过 `AppendGlyph` / `FlushGlyphRun`(`:1356` / `:1425`)合批成一个 `GlyphRun`。

**问题**:全屏 TUI(htop、vim 带主题、彩色进度条)以及大段选区,每帧是 O(行 × 列) 个矩形指令;200×50 的窗口最坏一万个 `FillRectangle`。字形合批做完后,这是正文重绘里剩下的最大一笔指令量。

**建议**:在 `RenderLine` 里维护「当前背景 run」(起始列、颜色),遇到颜色变化或行尾才发一次 `FillRectangle`;选区高亮与搜索高亮同样走 run。搜索高亮那段(`:2199` 附近)每格都在 `TryGetValue` 一次字典,顺手提到行首取一次。

**验收**:`VelaShell.Terminal.RenderTests` 像素回归不变;加一个用 `DrawingContext` 计数的守门用例,断言整屏同色背景只发 `rows` 次矩形。

### P-03 状态栏每秒一次 SSH exec 探测 — P1 / M

**证据**
- `src/VelaShell/ViewModels/MainWindowViewModel.cs:1704` `DispatcherTimer` 1 秒,同时驱动 `PollStatusMetricsAsync` 与 `PollLatencyAsync`(后者每 3 tick 一次 ICMP);
- `src/VelaShell.Infrastructure/Ssh/SessionMetricsService.cs` `GetMetricsAsync` 每次 `RunCommandAsync` → `src/VelaShell.Infrastructure/Ssh/TmdsSshClientWrapper.cs:251` 每次新开一条 exec 通道;
- 命令本身(`SessionMetrics.Extras.cs:120` `BuildCommand`)是一串 `/proc` 读取 + `nproc` + `df`,远端每次 fork 一个 shell 解释它。

**问题**:对远端是**每秒一次** fork/exec + SSH 通道建立/拆除;高 RTT 链路上一次采样自身就要几百毫秒;低配 VPS 上用户会在自己的资源监视器里看到 VelaShell 制造的负载。窗口最小化时已经暂停(`:1721`),但前台常驻时不可调。

**建议**
1. 短期:间隔做成设置项(1/2/5/10 秒,默认 2 秒,与资源监视器窗口的档位一致),窗口失焦时自动降到 10 秒;
2. 中期:用**一条常驻 exec 通道**跑循环脚本(`while :; do <探测>; echo __END__; sleep N; done`),读线程按分隔符切段解析——省掉每秒一次的通道握手与远端 fork,断线时通道自然 EOF;
3. 资源监视器窗口打开时,状态栏直接复用它的采样(`MetricsScope.Full` 是 `Basic` 的超集),不要两个计时器各探各的。

**验收**:抓包/`sshd -d` 观察每秒通道打开次数从 1 降到 0(常驻通道);远端 `top` 里不再看到周期性 sh 进程。

### P-04 每次读设置 = 整份 `AppSettings` JSON 反序列化 — P1 / S

**证据**
- `src/VelaShell.Infrastructure/Persistence/SonnetDbSettingsService.cs` `GetSettingsAsync`:缓存的是 JSON **文本**,每次调用都 `Deserialize<AppSettings>`(模型 1100 行、8 个子对象)再 `Normalize()`;
- `src/VelaShell.Core/Sftp/SftpService.cs:651` `GetTransferTuningAsync` **每个传输文件**读一次;
- `src/VelaShell.Infrastructure/Net/ProxyResolver.cs:41` **每个出站 HTTP 请求**同步 `GetAwaiter().GetResult()` 读一次(`VelaWebProxy` 逐请求解析代理);
- `src/VelaShell.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs:205-243` 每次建连接在四个工厂委托里各读一次;
- `MainWindowViewModel.cs` 10 处、`App.axaml.cs` 2 处。

**问题**:上传一万个小文件 = 一万次大对象反序列化(每次几百微秒到毫秒级,加上 GC 压力);Gist 同步、更新检查、插件的每个 HTTP 请求前都要同步反序列化一遍。当初把缓存做成文本是为了「调用方拿独立实例可安全修改」,但绝大多数调用方只读。

**建议**:缓存一份**不可变快照对象** + 版本号,`SettingsSaved` 时整体替换;接口加 `AppSettings Snapshot { get; }`(或 `GetSnapshot()`)给只读调用方(SftpService、ProxyResolver、DI 工厂、MainWindowViewModel 的绝大多数读取);`GetSettingsAsync` 保留给设置窗口这种确实要改的地方。`ProxyResolver` 顺手去掉同步等待。

**验收**:传输 1 万个 1 KB 文件的批次里 `GetSettingsAsync` 调用次数为 0;`dotnet-counters` 看 Gen0 分配率下降。

### P-05 启动路径上的同步 IO 与串行等待 — P1 / M

**证据**
- `src/VelaShell/App.axaml.cs:505` `ApplyPersistedPreferences` 在 UI 线程 `GetSettingsAsync().GetAwaiter().GetResult()`(首次调用 = 打开 SonnetDB + 读文档);
- `:195` `quickCommandRepository.LoadAsync().GetAwaiter().GetResult()`(含迁移);
- `src/VelaShell/Program.cs` 在 Avalonia 起来之前串行做:单实例互斥、`VelaShellDataMigration.MigrateIfNeeded`、`FinalizePendingUpdate`(便携版每次启动递归枚举应用目录找 `*.old`);
- `src/VelaShell/Views/MainWindow.axaml.cs:353` `OnWindowOpened` 再 `await GetSettingsAsync()` 一次,然后 `RestoreSessionsAsync`。

**问题**:首帧之前是「打开 DB → 读 settings → 读 quick_commands(可能迁移)→ 建主窗口」一条串行链,DB 打开与 Avalonia 平台初始化本可以并行。没有任何启动耗时打点,所以现在连「冷启动多少毫秒、瓶颈在哪」都答不上来。

**建议**
1. 先加**启动打点**(`Stopwatch` 从 `Main` 起,关键节点写进 U-08 的诊断日志,`VELASHELL_STARTUP_TRACE=1` 时打印到控制台)——先量再改;
2. `Program.Main` 里把 SonnetDB 引擎构造 + settings/quick_commands 读取放进一个后台 `Task`,与 `BuildAvaloniaApp()` 重叠,`OnFrameworkInitializationCompleted` 只 `await` 结果;
3. 快捷命令迁移移到窗口显示之后(`WireAutoSync` 已经接受一个 `markLocalChangedFirst` 标志,改成接受 `Task<QuickCommandLoadResult>` 即可);
4. `FinalizePendingUpdate` 只在存在 `.update-pending` 标记文件时才枚举目录;
5. 发布形态:csproj 注释已说明不开 `PublishReadyToRunComposite` 的取舍,建议用第 1 点的数据实测一次 composite 开/关的冷启动差,把决定建立在数字上。

**验收**:冷启动首帧时间(打点数据)下降;`OnFrameworkInitializationCompleted` 内不再有 `GetResult()`。

### P-06 资源监视器每个 tick 重建全部行对象 — P1 / S

**证据**:`src/VelaShell/ViewModels/ResourceMonitorWindowViewModel.cs:1347` `Fill<T>` 按索引 `target[index] = item` 整表替换;`:836` `new ProcessRow(...)`、`:932` `new PartitionRow(...)`、`:1172` `new GpuProcessRow(...)`、`:1256` `new CoreRow(...)` 每 tick 全部新建。`ResourceMonitorWindow.axaml` 里承载它们的是 34 个非虚拟化 `ItemsControl`。

**问题**:每次替换一个元素,`ItemsControl` 都会销毁并重新生成容器与模板;1 秒档下,窗口开着就是每秒几十上百个模板实例化,空闲 CPU 明显。同仓的 `ProcessManagerViewModel.Merge`(`:337`)已经用「同 PID 复用行、原地 Move」做对了,两处不一致。

**建议**:行改为带 INPC 的可变 VM,按 key(PID / 设备名 / 核号 / 接口名)复用,只更新属性;`Fill` 改成 key-diff。核心列表(`CoreRows`)在 List 视图下同样。

**验收**:开启 1 秒档、进程页,`dotnet-counters` 观察 Gen0 分配率与 UI 线程占用下降;选中行在刷新后保持(现在会丢)。

### P-07 回滚缓冲整行满宽存储,上限 20 万行,设置无数值钳制 — P1 / M

**证据**
- `src/VelaShell.Terminal/Emulation/TerminalRow.cs:11` 每行固定 `new TerminalCell[columns]`,16 字节/格(`TerminalCell.cs` 注释已精确到字节);
- `src/VelaShell/Views/Settings/TerminalSettingsPage.axaml:186` 回滚上限 `Maximum="200000"`;
- `src/VelaShell.Core/Models/AppSettings.cs:76` `Normalize()` 只做迁移,不钳制任何数值——损坏或手改的设置能把 `ScrollbackLines`、字号、端口写成任意值。

**问题**:200 列 × 20 万行 × 16 B ≈ **640 MB / 标签**,而典型日志行只有几十列非空。开十个标签跑长任务,内存就是 GB 级。

**建议**
1. 行退休进 scrollback 时按最后一个非空格列**裁剪数组**(渲染、选区、搜索对越界列视为空格;reflow 已经走 `Span`,改动集中在 `TerminalRow` 索引器与 `CopyTextTo`);典型输出可省 60–90%;
2. `Normalize()` 钳制 `ScrollbackLines`(100–200000)、`TerminalFontSize`(6–40)、`DefaultPort`(1–65535)、超时/心跳/重试等,与各页面 `NumericUpDown` 的范围取同一常量;
3. 可选:设置页在回滚项旁显示「按当前列宽估算约 xx MB / 标签」。

**验收**:`TerminalCellMemoryTests` 补一条「1 万行、每行 20 个字符、200 列」的内存用例;reflow / 选区 / 搜索 / 折叠现有用例全绿。

### P-08 VT 解析器逐字符状态机,Ground 态无批量打印快路径 — P2 / M

**证据**:`src/VelaShell.Terminal/Emulation/VtParser.cs` `Parse` 逐 rune `Consume` → `Ground` → `actions.Print(rune)`;`TerminalEmulator.Print` 每个字符做字符集映射、宽度判定、待换行判定、`SetCell`。

**问题**:纯文本洪流(编译日志、`cat`)每个字符走一遍完整分发。xterm.js、Windows Terminal 都在 Ground 态做「扫到下一个控制字符为止,整段一次打印」的快路径。

**建议**:`Ground` 态下先向前扫描连续的可打印 ASCII(`0x20–0x7E`,非 DEC 图形集时),调用新增的 `actions.PrintRun(ReadOnlySpan<char>)`,模拟器内部按剩余列数分段写入、处理自动换行。与 P-10 的基准一起做,先量后改。

**验收**:基准里「10 MB 纯 ASCII 文本」解析吞吐提升(目标 2× 以上);`VtParserTests` / `TerminalEmulatorTests` 全绿。

### P-09 关标签时 UI 线程同步等待读/写任务最多 3 秒 — **撤回**(2026-09-05 复核)

**原判断**:`SshTerminalBridge.Dispose`(:114)里的 `_readTask?.Wait(2s)` + `_writeTask.Wait(1s)` 会卡住 UI 线程。

**复核结果**:不成立。`TerminalTabViewModel.Dispose`(:627)与 `DetachTransport`(:787)都是 `Task.Run(bridge.Dispose)`;退出路径 `App.CloseTerminalBridgesOnExit`(:466)对全部桥并行 `Task.Run` 且总上限 2 秒。UI 线程从不等待这两个 `Wait`。

**保留的整洁项**:把 `Dispose` 内部的 `Wait` 改成 `DisposeAsync`,并入 Q-04。

### P-10 没有性能基准工程,吞吐/分配回归靠肉眼 — P2 / M

**证据**:仓库无 BenchmarkDotNet 工程;现有守门是像素回归(`RenderTests`)与计数型断言(`ThemeTokenShadowingUiTests`、`BodyRenderCountForTest`),没有吞吐与分配基准。`plan.md` 里多处「实测 40–57 ms → 1.65 ms」这类数字都是一次性手测。

**建议**:新增 `tests/VelaShell.Benchmarks`(BenchmarkDotNet,`MemoryDiagnoser`):VtParser 吞吐(纯 ASCII / ANSI 密集 / CJK)、`TerminalScreen` 滚动与 reflow、`RenderLine` 分配(headless)、`BufferSearch`、`SessionMetrics.Parse`、`SshTerminalBridge` 合批。不进 CI 门禁,提供 `--job short` 的冒烟脚本,数字记进 `plan.md`。P-01 / P-02 / P-07 / P-08 都以它验收。

---

## 3. 功能优化

### F-01 终端内搜索只有不区分大小写的纯文本 — P1 / S

**证据**:`src/VelaShell.Terminal/BufferSearch.cs:21` `FindAll` 固定 `StringComparison.OrdinalIgnoreCase`;`TerminalTabView.axaml:148-173` 搜索栏只有 输入框 / 上一个 / 下一个 / 关闭。

**建议**:搜索栏加三个切换(区分大小写 `Aa`、全词 `\b`、正则 `.*`),正则用 `RegexOptions.NonBacktracking` + 超时,编译结果按模式串缓存;`BufferSearch` 已经是复用缓冲的逐行扫描,加 `Regex.EnumerateMatches(ReadOnlySpan<char>)` 即可零分配。命中计数「3 / 17」若尚未展示一并加上。

### F-02 备用屏无 Alternate Scroll(滚轮 → 方向键) — P1 / S

**证据**:`VelaTerminalControl.cs:3131` `OnPointerWheelChanged`:未开鼠标追踪时滚轮只滚本地回滚;备用屏没有回滚,于是 `less`、`vim`(未开 mouse)、`man` 里滚轮**什么都不发生**。全仓无 DECSET 1007 处理。

**建议**:`Emulator.IsAlternateScreen && Modes.Mouse == None` 时把滚轮转成 `CSI A` / `CSI B`(应用光标键模式下 `SS3 A/B`),每格一次;实现 `?1007h/l` 让应用能关掉;设置 → 终端加「备用屏滚轮转方向键」(默认开,xterm / Windows Terminal / iTerm2 默认行为)。

### F-03 「键盘优先」产品缺核心键位 — P0 / S

**证据**:`src/VelaShell/Services/KeyboardShortcutService.cs:61-84` 只登记 复制/粘贴/新建/关闭/设置/Tab 前后切换;`MainWindowViewModel.RegisterCommands` 里 `split.horizontal` / `split.vertical` / `edit.clear` 只有命令面板入口;`ShortcutCatalog.cs` 里没有 跳到第 N 个标签、字号加减、窗格移焦、关闭全部。

**问题**:README 把「键盘优先」写在第一段,但分屏、跳标签、缩放都得动鼠标或开面板。

**建议**(键位按主流终端习惯,登记进 `ShortcutCatalog` 由 `ShortcutCatalogTests` 守门,文档同步 `快捷键参考.md`):
- `Ctrl+1…9` 跳到第 N 个标签、`Ctrl+9` 最后一个(macOS `Cmd+数字`);
- `Ctrl+Shift+D` 右侧分屏、`Ctrl+Shift+E` 下方分屏;`Alt+←↑→↓` 窗格移焦;`Ctrl+Shift+X` 关闭当前窗格;
- `Ctrl+=` / `Ctrl+-` / `Ctrl+0` 字号加减与重置(与 Ctrl+滚轮同源 `FontSizeChanged`);
- `Ctrl+Shift+K` 清屏(`edit.clear` 已有);`Ctrl+Shift+W` 关闭全部标签(经 ConfirmBeforeClose)。

### F-04 会话树没有过滤/搜索框 — P1 / S

**证据**:`src/VelaShell.Presentation/ViewModels/SessionTreeViewModel.cs` 无 Filter/Search 属性;`SidebarView.axaml` 无输入框。有几十上百台主机时只能靠命令面板(Ctrl+P)。

**建议**:侧栏树顶部加过滤框(匹配 名称 / 主机 / 用户名 / 标签 / 分组名,可选拼音首字母),输入时自动展开命中分组、清空时恢复折叠状态(`QuickCommandsViewModel` 里 `_expansionBeforeSearch` 已有同样的模式可复用);`Ctrl+Shift+E` 或树上直接打字即聚焦。

### F-05 命令面板无相关度排序、无命中高亮、无最近使用加权 — P1 / S

**证据**:`src/VelaShell/ViewModels/CommandPaletteViewModel.cs:167-190` `Matches` 是布尔(包含 / 子序列),结果按注册顺序分组展示;`Fuzzy` 让 `st` 也能命中「Settings」「Sftp」「Trace Route」等一大串,但排不出先后。

**建议**:打分(完全前缀 > 单词首字母 > 连续子串 > 子序列;每个分数再按最近使用次数/时间加权),结果按分排序、分组内也排序;标题里高亮命中字符(`InlineCollection` 或双 `TextBlock`);记录 MRU 到 `IAppDataStore`。`Groups.Clear()+Add` 的整表重建对 <200 项没问题,不必动。

### F-06 连接配置缺会话级覆盖项;「标签自定义颜色」实际是自动配色 — P1 / M

**证据**
- `src/VelaShell.Core/Models/SessionProfile.cs` 字段只有 认证 / 分组 / 标签 / 跳板 / 认证后命令 / FTP / 插件;没有 编码、终端类型、配色方案、初始目录、keepalive、环境变量、连接后自动开启的隧道;
- `src/VelaShell/Services/ConnectionAccent.cs:31` 标签强调色 = 按 `profileId` 哈希在 8 色里取一个,用户不能选;`plan.md` §12-13 把「会话标签自定义颜色/图标」记为 ✅,与实现不符(见 Q-07)。

**建议**:`SessionProfile` 增加 `TerminalOverrides`(`Encoding` / `TerminalType` / `ColorScheme` / `TabColor` / `StartupDirectory` / `KeepAliveSeconds`,null = 跟随全局)与 `AutoStartTunnelIds`;连接对话框「高级选项」区展示;`plan.md` §37 提到的「五处手写拷贝」借这次统一为 `SessionProfile.Clone()`,以后加字段只改一处。生产环境标红、测试环境标绿是运维最常提的诉求。

### F-07 关闭已连接标签无确认 — P1 / S

**证据**:`MainWindowViewModel.cs:2950` `CloseTerminalTab` → `Layout.CloseDocument` 直接关;只有整窗关闭走 `ConfirmBeforeClose`(`MainWindow.axaml.cs:842`)。

**建议**:设置 → 常规加「关闭已连接标签前确认」(默认开);该标签有传输进行中时强提示;可选进阶:通过 shell integration(OSC 133)判断是否有前台任务,只在有任务时确认。

### F-08 睡眠唤醒 / 网络切换后不主动重连;重连间隔固定无退避 — P1 / S

**证据**:全仓无 `NetworkChange` / `SystemEvents.PowerModeChanged` 订阅;`MainWindowViewModel.cs:2756` `OnTabDisconnected` 用固定 `ReconnectIntervalSeconds` 重试到 `MaxRetries` 为止。

**问题**:合盖再开,所有会话要等 keepalive 超时才知道断了,再按固定间隔重试;网络刚恢复的那一刻反而没人管。

**建议**:订阅 `NetworkChange.NetworkAvailabilityChanged`(跨平台)与 Windows 的 `SystemEvents.PowerModeChanged(Resume)`:恢复后对所有「断开且非用户主动」的标签立即触发一次重连并重置计数;间隔改为指数退避(1s → 2s → 4s … 封顶 `ReconnectIntervalSeconds`)。

### F-09 本地文件面板不监听目录变化 — P2 / S

**证据**:`FileSystemWatcher` 只用在 `ExternalEditSessionManager` 与 `PluginManager`;`LocalFilePaneViewModel.cs` 无自动刷新。远端面板已有 `RefreshSilentlyAsync`(`FileBrowserViewModel.cs:168`)。

**建议**:当前目录挂一个 `FileSystemWatcher`(防抖 300 ms,只看直接子项),变化后静默刷新并保留选择与滚动位置;网络盘/不可用时静默退化。

### F-10 内置远程编辑器缺查找替换、编码选择、自动换行、大文件保护 — P1 / S–M

**证据**:`src/VelaShell/Views/RemoteFileEditorView.axaml.cs`:无 `SearchPanel.Install`;`DetectEncoding(:103)` 只认 BOM,GBK/Shift_JIS 文件按 UTF-8 解码后回存会**破坏原文件**;无文件大小阈值;`RemoteFileEditorView.axaml:102` 只开了行号。

**建议**:安装 AvaloniaEdit `SearchPanel`(Ctrl+F / Ctrl+H);工具栏加 编码下拉(与终端编码列表共用,默认按 BOM → UTF-8 严格解码失败则回退到会话编码)、自动换行开关、行尾(LF/CRLF)显示;超过 5 MB 提示「改用外部编辑器 / 只读打开」。

### F-11 键盘交互式认证(2FA/OTP)无实现 — P2 / M–L

**证据**:全仓无 `KeyboardInteractive` 实现;`InfrastructureServiceCollectionExtensions.cs:300` 刻意整体替换默认凭据列表(为排除 `SshAgentCredentials`)。堡垒机/跳板上 Google Authenticator、Duo 一类的 `keyboard-interactive` 提示目前直接失败。

**建议**:先确认 Tmds.Ssh 0.24 对 `keyboard-interactive` 的支持面;支持的话复用两步验证对话框逐条展示服务端 prompt(密码类掩码、OTP 明文);不支持则在连接失败诊断里给出明确文案而不是超时。ssh-agent 见 `plan.md` §10-A。

---

## 4. UI / 交互优化

### U-01 无障碍与键盘可达性几乎空白 — P1 / M

**证据**:1.4 万行 XAML 里 `AutomationProperties.*` 仅 **14** 处;`KeyboardNavigation.*` / `TabIndex` / `IsTabStop` **0** 处;`ToolTip.Tip` 116 处(说明图标按钮很多,但读屏器读不到);自绘控件(终端、会话树自绘箭头、`DockGroupControl` 标签条、`UsageHeatGrid`)无 `AutomationPeer`。

**建议**
1. 写一个附加行为:未显式设置 `AutomationProperties.Name` 的 `Button` 自动取 `ToolTip.Tip` 文本作 Name——一次性覆盖绝大多数图标按钮;
2. 对话框(连接 / 认证 / 主机指纹 / 设置)设初始焦点与 Tab 顺序,`Esc` 一律关闭;
3. 终端控件提供最小 `AutomationPeer`(Name = 标签标题,Value = 光标所在行文本);
4. `UiThemeCatalogTests` 已守 WCAG AA 对比度,再加一条「所有 `Button`/`ToggleButton` 必须有可读 Name」的 headless 扫描用例。

### U-02 27 处 XAML 颜色字面量 + 3 个 C# 固定 Dracula 配色,亮色主题下失配 — P1 / S–M

**证据**(`AGENTS.md` 明文:XAML 与 C# 里不许出现颜色字面量)
- XAML 27 处:`TerminalTabView.axaml:391-392` `#FFFFFF`(重连按钮文字)、`MainWindow.axaml:143-151` `#66000000` 遮罩、`SessionTreeView.axaml:125/131` 拖放高亮、`ProcessManagerView:50` / `PluginManagerWindow:33` / `PluginPanelWindow:31` / `ResourceMonitorWindow:41` 关闭钮 `#E81123`、`RecordingPlayerView:42` `#FF6B6B`、`ConnectionProfileView:370/593`、`SessionImportView:105`、`SessionTreeView:406` 阴影 等;
- C#:`Converters/SessionStatusToBrushConverter.cs:21-23`(连接状态三色)、`Services/ConnectionAccent.cs:18-28`(标签强调 8 色)、`Services/SyncInputChannels.cs:29-35`(同步输入通道三色)全部写死 Dracula 色值。

**问题**:亮色主题(Alucard / GitHub Light / Rosé Pine Dawn / Sakura)下,树上的状态圆点、标签强调条、同步通道徽章仍是暗色系配色,对比度没有经过 `UiThemeCatalogTests` 把关;`#FFFFFF` 的按钮字在亮色强调色上直接看不清。

**建议**:把这些抽成种子色派生的令牌(`VelaStatusConnected/Connecting/Disconnected`、`VelaAccentPalette0..7`、`VelaSyncChannelA/B/C`、`VelaScrim`、`VelaDangerHover`、`VelaOnAccent`),由 `ThemeTokenApplier` 按主题生成;C# 侧改 `DynamicResource` 查找(`TraceWorldMap.cs:59` 已经有 `Brush("VelaTraceLand", fallback)` 的正确写法)。再加一条测试扫描 `Views/**/*.axaml` 禁止 `#` 字面量(白名单 `AboutPage` 的固定渐变)。

### U-03 侧栏底部用户名写死为 `root` — P0 / XS

**证据**:`src/VelaShell/Views/SidebarView.axaml:243` `<TextBlock Text="root" …/>`,旁边是用户头像图标——设计稿占位符留到了产品里,任何用户看到的都是 `root`。

**建议**:绑定「活动会话的 用户名@主机」(无会话时显示本机用户名或隐藏整行);顺带这一行可作 F-06 标签颜色的展示位。

### U-04 设置窗口一次实例化全部 12 页 — P2 / S

**证据**:`src/VelaShell/Views/SettingsView.axaml:238-249` 12 个页面常驻,用 `IsVisible` 切换;外观页 9 个 `ItemsControl`、快捷键页 8 个、关于页 5 个。

**建议**:`ContentControl` + 按 `SettingsSectionKey` 懒创建并缓存(切过的页保留状态);要动画的话用 `TransitioningContentControl`。首开设置窗口的构建成本只剩一页。

### U-05 状态栏信息密度低、不可点击 — P2 / S

**证据**:`StatusBarView.axaml` 只有 CPU/内存/交换/磁盘/网速、延迟、后台任务;无 编码 / 终端类型 / 网格尺寸 / 光标位置 / 选区字符数;指标只有 tooltip,点击无动作。

**建议**:右侧加 `UTF-8 · xterm-256color · 200×50` 三段可点击(编码点击弹菜单热切,当前会话即时生效——`ResolveEncoding` 已有);点 CPU/内存直接打开资源监视器;有选区时显示「已选 N 字符」。

### U-06 终端链接无悬停反馈 — P2 / S

**证据**:`VelaTerminalControl.cs:3031` `OnPointerMoved` 只处理折叠列与鼠标上报;URL/IP 恒定下划线,Ctrl+悬停时光标不变手型、无目标提示。

**建议**:Ctrl 按下且悬停在 `Url` / `IpAddress` 语义段时 `Cursor = Hand`,`ToolTip` 显示完整 URL;IP 段右键给「复制 / 用此地址新建连接 / 路由追踪」。

### U-07 可能变长的列表用了非虚拟化 `ItemsControl` — P2 / S

**证据**:全仓 116 个 `ItemsControl` vs 26 个 `ListBox`;其中项数不受控的:`NotificationPanelView.axaml:168`(通知历史)、`CommandPaletteView.axaml:148-153`(嵌套 ItemsControl,全部已保存会话都在里面)、`ResourceMonitorWindow.axaml:1300/1127/1656`(进程 / GPU 进程 / 连接)。

**建议**:项数可能 > 50 的改 `ListBox`(自带 `VirtualizingStackPanel`)并保留现有样式;命令面板把分组扁平成单列表 + 分组头项。

### U-08 崩溃与错误只写 `Trace`,发布版无日志落盘、无「打开日志」入口 — P0 / S

**证据**:`src/VelaShell/Program.cs:278` `InstallGlobalExceptionGuards` 只 `Trace.WriteLine`;全仓 65 处 `Trace.WriteLine`、无 `ILogger` / 文件监听器;Release 下没有任何 Trace 监听器 → 用户反馈「闪退」时没有任何可取证的东西;18 处静默 `catch {}`。

**建议**:引入最小滚动文件日志(`~/.velashell/logs/velashell-yyyyMMdd.log`,保留 7 天,`TraceListener` 直接挂上去,不必引大日志框架);未处理异常写 `crash-<时间>.txt`,下次启动在消息中心提示「上次异常退出,查看 / 复制日志」;关于页与消息中心加「打开日志目录」;P-05 的启动打点也写进去。

### U-09 连接对话框无即时字段校验 — P2 / S

**证据**:`ConnectionProfileViewModel.cs` 只有提交后的 `ErrorMessage`;`DataValidationErrors` / `INotifyDataErrorInfo` 全仓 3 处。

**建议**:主机 / 端口 / 用户名 / 私钥路径即时校验(空、端口范围、文件存在、私钥可解析),错误内联在字段下方,保存/连接按钮 `CanExecute` 联动;端口沿用 §40 的数字框守门。

### U-10 状态栏文字是主要反馈通道,断线/告警/倒计时互相覆盖 — P2 / M

**证据**:`StatusBar.Status` 一个字符串承载 断线通知、自动重连倒计时、安全告警、复制成功等;后写覆盖先写,没有分级、不可点击、不可回看。

**建议**:统一 toast 组件(与 `FileTransferView` 浮层同风格),`Info / Warning / Error` 三级,可堆叠、可点击(断线 toast 带「立即重连」)、自动收进消息中心;`StatusBar.Status` 退化为只显示最近一条。

---

## 5. 工程质量 / 可维护性

### Q-01 六个 God 类 — P1 / L(分批)

**证据**:`MainWindowViewModel.cs` 4837 行 / 122 个方法 / 构造函数 30 个参数;`FileBrowserViewModel.cs` 3179;`PluginManager.cs` 2603(安装、签名信任、收据、影子目录、激活、心跳自愈、dev 监视全在一个类);`MainWindow.axaml.cs` 1683;`TerminalTabView.axaml.cs` 1317(命令补全的提示判定逻辑都在代码隐藏);AI 插件 `ChatPanelView.axaml.cs` 2861。

**建议**(按方法簇拆协作者,不改行为,每批一个 PR):
- `MainWindowViewModel` → `SessionConnectionCoordinator`(`TryConnect* / Reconnect / Teardown / Handshake`)、`DocumentLifecycle`(SFTP / FTP / 插件文档开关与关闭任务)、`StatusMetricsPoller`(P-03 一起做)、`TerminalSettingsApplier`(`ApplyLiveTerminalSettings` 一族)、`NotificationWiring`;
- `PluginManager` → `PluginInstaller` / `PluginTrustStore` / `PluginActivator` / `PluginDevWatcher`;
- `TerminalTabView` 的 `IsInteractivePrompt` 一族抽成 `SuggestionController`(纯逻辑,可单测)。

### Q-02 `TabBarViewModel` 与 `DockWorkspace` 双模型并存 — P2 / M

**证据**:`MainWindowViewModel.cs` 22 处 `TabBar.*`;`CloseTerminalTab(:2950)` / `CloseActiveTab` 同时处理 `Layout` 与 `TabBar` 两条路径;`TabBar.Tabs.CollectionChanged` 与 `Layout.DocumentClosed` 各自维护会话状态。VelaDock 落地后 `TabBar` 已无 UI,只剩「活动标签」这一份投影。

**建议**:以 `DockWorkspace` 为唯一事实,`ActiveTerminalTab` 直接由 `ActiveDocumentChanged` 派生,`TabBarViewModel` 删除或退化为只读投影;`OnTabsCollectionChanged` 的会话状态合并逻辑(§24 / §39 修过两次的同形 bug)只保留一份。

### Q-03 CI 只有发布流水线,PR 不跑构建/测试 — P0 / S

**证据**:`.github/workflows/` 只有 `release.yml`;`dependabot.yml` 在,但 Dependabot 的 PR 没有任何检查就能合。

**建议**:加 `ci.yml`(push 到 `dev`/`main` + PR):`dotnet build -warnaserror` → `dotnet test`(`--filter "TestCategory!=DockerIntegration&TestCategory!=CrossPlatform"`)→ 上传 trx;矩阵 `windows-latest` + `ubuntu-latest`(headless 测试在 Linux 可跑,顺手覆盖 Wayland 分支的编译);`dotnet format --verify-no-changes` 要等 CRLF/LF 噪音处理掉再加(见 `velashell-line-endings-and-format-noise`)。

### Q-04 33 处 `async void` 事件处理器 + 18 处静默 `catch {}` — P2 / S

**证据**:`async void` 全部是视图代码隐藏的 `_Click` / `OnOpened` / `OnDrop`(列表见审查记录);`MainWindow.axaml.cs:1668` 已有 `SafeFireAndForget`。

**建议**:事件处理器统一 `=> SafeFireAndForget(() => …Async())`,异常进 U-08 的日志;静默 `catch` 至少留一行 Trace,注释说明为什么可以吞。

### Q-05 遗留死代码 — P2 / XS

**证据**:`src/VelaShell.Terminal/ITerminalEmulator.cs:45` `ScrollbackBuffer` 属性,`VelaTerminalControl.cs:708` 实现为 `new(1)` 从不使用;`ScrollbackBuffer.cs` / `TerminalLine.cs` 只被彼此引用。

**建议**:删除,顺手核对 `SearchMatch.cs` 是否同样无人用。

### Q-06 一条偶发失败的测试;AI 插件测试占总时长 70% — P1 / S

**证据**:全量 `dotnet test` 第 1 轮 `VelaShell.Infrastructure.Tests` 失败 1 条,单独重跑 361 通过——并行压力下的时序型用例;`-v q` 不打印用例名,需要 trx 才能定位。`VelaShell.Plugin.Ai.Tests` 559 条跑了 2 分 30 秒(其余 2200 条合计约 1 分钟)。

**建议**:CI(Q-03)固定输出 trx 并对失败用例开 issue;找出 AI 插件测试里真实等待的用例(`Task.Delay` / HTTP 超时)改用可控时钟或 `TimeProvider`;`velashell.runsettings` 已有 60 s 单测超时,加 `[Timeout]` 到已知的慢用例。

### Q-07 `plan.md` 两处与实现不符 — P2 / XS

- §12-13「会话标签自定义颜色/图标 ✅」——实现是 `ConnectionAccent` 按 id 哈希自动配色,用户不可选(见 F-06);
- §10-A「启动时自动检查 / 自动下载 —— 仍未实现」——`CheckUpdatesOnStartup` 已在 `MainWindowViewModel.cs:1591` 经消息中心接线(`AutoDownloadUpdates` 仍无消费者)。

---

## 6. 建议实施顺序

**第一批(1–2 周,低风险高收益,可并行)**
U-03 · U-08 · Q-03 · F-03 · P-02 · P-04 · P-06 · F-02 · F-04 · F-05 · U-02 · Q-05 · Q-07(P-09 已撤回)

**第二批(2–3 周)**
P-01 · P-03 · P-05(先打点)· F-07 · F-08 · F-09 · F-10 · U-01 · U-04 · U-05 · U-06 · U-07 · U-09 · Q-04 · Q-06

**第三批(需要设计或先量化)**
P-07 · P-08 · P-10 · F-06 · F-11 · U-10 · Q-01 · Q-02

每一批做完把结论回写 `plan.md`(那是进展记录的唯一入口),本文只做一次性的审查清单。

---

## 7. 附:`plan.md` 已登记、仍未落地的事项索引(不在上文重复)

| plan.md 位置 | 事项 | 与本文的关系 |
| --- | --- | --- |
| §10-A | 标签栏位置(顶部/底部)、主密码保护、自动加载密钥到 Agent、`AutoDownloadUpdates`、传输失败重试(`TransferMaxRetries` 零消费者,本次复核仍成立) | 独立 |
| §10-B | ed25519 密钥生成、审计日志查看界面、`audit_log` / `conn_history` 无保留策略(本次复核:`SonnetDbRecentConnectionService` 只 `LIMIT` 读,不清理)、选择性导出、运维编排中心 | 保留策略可与 U-08 的日志保留一起做 |
| §10-C | sixel 图形 | 独立 |
| §12-9 | `~/.ssh/config` 导入 | 独立(导入框架已就绪) |
| §12-11 | Anti-idle | 可与 F-08 同一批 |
| §12-15 | 用户自定义关键字高亮规则 | 独立 |
| §12-18 | 触发器 / 自动应答 | 独立 |
| §12-20 | SSH 证书认证 | 与 F-11 同为 Tmds.Ssh 能力面问题,一起评估 |
| §14 | SFTP 双栏与 WinSCP 差距(velashell-docs) | 独立 |
