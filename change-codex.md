# VelaShell 分阶段修改方案（Codex）

日期：2026-09-05  
依据：plan-codex.md  
状态：仅方案，尚未修改代码、项目文件或测试文件。

本文把优化审查拆成可审阅、可回滚的实施阶段。每项都记录拟修改位置、具体改法、修改原因和验收方式。实际执行时应按阶段提交，前一阶段的基线和测试通过后再进入下一阶段。

## 总体原则

1. 先增加测量和回归测试，再改变运行时行为。没有性能基线的项目只记录为候选，不直接声称“已优化”。
2. 每次提交只解决一个可验证的问题，保留现有的会话状态合并、资源字典原子换主题、文件浏览器先加载后提交、插件超时与崩溃退避等纪律。
3. 用户可见文案、设置项、快捷键、插件契约或发布行为变化，必须同时安排 resx、ShortcutCatalog、velashell-docs 中英文镜像的同步。
4. SDK 契约只在 SDK 仓库设计和发布；本仓库不造本地 NuGet 包、不猜版本号。
5. 每阶段记录基线值、修改后值、测试命令、跳过项、剩余风险和回滚点。

## 阶段 0：建立可重复基线

### 0.1 性能基准

拟修改位置：

- 新增 tests 下的 BenchmarkDotNet 项目，并加入解决方案。
- 终端测试夹具：持续输出 100k 行、htop 原地刷新、滚动回看、窗口缩放。
- 文件测试夹具：1k、10k、100k 条目录，记录首屏、全量加载、峰值内存、Gen0。
- 基础设施夹具：SonnetDB 并发读写、插件 RPC、激活/停用和隔离进程重启。
- .github/workflows：增加手动或 nightly 性能作业。

为什么改：终端渲染、目录加载和数据库锁目前只有静态推断，没有 P95 帧时间、输入延迟、内存和分配基线。先测量可避免为了理论热点增加复杂度。

验收：同一命令可重复运行；结果输出 JSON/Markdown；CI 不因机器差异误报，初期只记录趋势。

### 0.2 静态门禁

拟修改位置：Directory.Build.targets、tests/Directory.Build.targets 或 scripts 下新增扫描脚本。

扫描阻塞等待、未传递 CancellationToken 的 I/O、无界 Channel、数值属性绑定到 TextBox、颜色/字号字面量、缺失五份 resx 和未登记快捷键。

为什么改：这些是仓库硬约束，人工审查容易回归。

验收：历史命中有允许列表和原因；新增违规让 CI 失败；扫描不改变运行时代码。

## 阶段 1：P0 取消、关闭和高频队列

### 1.1 文件浏览器导航取消

拟修改位置：

- src/VelaShell/ViewModels/FileBrowserViewModel.cs
- src/VelaShell/ViewModels/LocalFilePaneViewModel.cs
- 必要时修改 src/VelaShell.Core/Sftp/ISftpService.cs 和实现
- FileBrowserViewModelTests、FTP/插件文件系统测试

改动内容：为当前导航增加 CancellationTokenSource；新导航取消并释放旧源，再递增导航版本；将调用方令牌、导航令牌和生命周期令牌链接；静默刷新不能取消显式导航；保留最新结果检查。

为什么改：当前版本号只能防止旧结果覆盖新列表，不能停止旧网络请求，快速导航会浪费通道和服务器资源。

验收：100 次快速导航只提交最后路径；旧请求收到取消；关闭标签、断网和取消没有未观察异常。

### 1.2 终端、桥接和传输指标

拟修改位置：

- src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs
- src/VelaShell.Terminal/SshTerminalBridge.cs
- src/VelaShell.Core/Sftp/TransferManager.cs
- 诊断服务和对应测试

改动内容：记录输出合并数、UI 回调数、绘制行数、绘制耗时、写队列深度、传输队列深度和输入到回显延迟；默认关闭高成本字符串格式化。

为什么改：现有合批逻辑没有统一证据，指标是判断脏行渲染和有界队列是否值得实现的前提。

验收：指标不改变像素输出、输入顺序和正常内存占用；基准输出 P50/P95 和分配数据。

### 1.3 异步优先关闭

拟修改位置：

- src/VelaShell.Terminal/SshTerminalBridge.cs
- src/VelaShell.Infrastructure/Plugins/Protocols/PluginTerminalShellStream.cs
- src/VelaShell.Infrastructure/Persistence/SonnetDbEngine.cs
- src/VelaShell/App.axaml.cs、src/VelaShell/Program.cs
- 关闭测试

改动内容：增加异步关闭入口，先发取消/完成信号再等待后台任务；应用退出按停止新工作、取消、排空必要写入、释放资源执行；同步 Dispose 只做幂等标记和非阻塞释放；插件终端同步 WriteLine 优先进入有界写队列。

为什么改：现有 Wait、_gate.Wait 和 GetAwaiter().GetResult 可能阻塞 UI，异常情况下会造成关闭卡顿或死锁。

验收：挂起读/写/RPC/数据库操作时关闭标签不等待网络；关闭时间有上限；重复关闭幂等；任务异常可观察。

### 1.4 有界写入和 resize 合并

拟修改位置：

- src/VelaShell.Terminal/SshTerminalBridge.cs
- src/VelaShell.Infrastructure/Plugins/Protocols/PluginTerminalShellStream.cs
- Terminal/Infrastructure 测试

改动内容：用户输入队列与可丢弃的窗口尺寸队列分离；输入有容量和背压，不能丢字节；resize 只保留最新尺寸，由单一发送循环提交；记录峰值和合并次数。

为什么改：SSH 写队列无界，插件 Resize 每次事件都 Task.Run，粘贴、广播和拖拽可能造成任务和内存增长。

验收：1 MB 粘贴不丢字节、不乱序；连续缩放最终尺寸正确；5 秒压力测试队列和线程数有上限。

### 1.5 清理构建警告

拟修改位置：plugins/VelaShell.Plugin.Ai/Ui/ChatPanelView.Editing.cs:46 及对应测试。

改动内容：按 CA1859 确认 DeleteFromAsync 的真实返回语义，改用更具体的返回类型或添加有依据的抑制说明。

为什么改：当前构建有一条性能分析警告，警告会掩盖后续问题。

验收：Debug/Release 无新增警告；AI 插件测试通过；若抑制则写明原因。

## 阶段 2：大目录、数据库和终端性能

### 2.1 大目录分批或分页

拟修改位置：

- src/VelaShell.Core/Sftp/ISftpService.cs 及 SFTP/FTP 实现
- src/VelaShell.Infrastructure/Plugins/Capabilities/RemoteFsCapability.cs
- FileBrowserViewModel、FileBrowserView.axaml
- 协议和 headless UI 测试

改动内容：先确认协议是否支持页游标或流式列举；支持时首屏先提交、分批追加；不支持时提供分批包装、条目上限或继续加载；排序、隐藏文件过滤和选择恢复改成可增量执行；确认 ListBox 没有被布局禁用虚拟化。

为什么改：当前远端条目和 ViewModel 一次性加载，ReplaceAll 会造成大目录内存和 UI 停顿。

验收：1k/10k/100k 有首屏、内存和 GC 数据；最新导航不会被旧批次覆盖；多选、排序、刷新保持正确。

### 2.2 数据库锁和查询策略

拟修改位置：

- SonnetDbEngine.cs
- SonnetDbAppDataStore.cs、SonnetDbSessionRepository.cs
- 审计、录制、插件时序存储
- Infrastructure 并发和关闭测试

改动内容：先确认 SonnetDB 并发边界；把 JSON 序列化移出锁；批量写时序点；引擎允许时按集合拆锁，否则建立单写者队列；增加审计/录制保留、范围查询和归档；退出时排空并报告超时。

为什么改：单个 _gate 串行化所有集合和时序工作，全扫描和反序列化发生在锁内，数据增长会互相阻塞。

验收：并发读配置、写审计、刷录制和插件 KV 有 P95 等待时间、吞吐和一致性数据；关键配置不因取消丢失。

### 2.3 终端脏行优化（基准证明需要时）

拟修改位置：

- src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs
- TerminalScreen.cs、TerminalEmulator.cs
- Terminal 渲染和像素测试

改动内容：根据屏幕变更记录合并脏行/脏区；光标、选区、幽灵文本、搜索高亮继续走叠加层；滚动、全屏清除、主题/字体切换和缩放保留全量失效路径；热路径不创建临时字符串和对象。

为什么改：终端可能全屏重绘，但已有 GlyphRun 和合批优化，必须先证明脏行收益，避免引入错位和缓存失效。

验收：P95 帧时间、输入延迟和 Gen0 改善；像素、滚动、选择、折叠、主题和字体测试全通过；收益不足则回滚实现而保留指标。

## 阶段 3：生命周期、连接和传输边界

### 3.1 抽取 SessionRegistry

拟修改位置：

- 新建 src/VelaShell.Presentation/Services/SessionRegistry.cs
- MainWindowViewModel.cs
- SessionTreeViewModel.cs
- 会话状态相关测试

改动内容：统一登记终端和文档会话的 id、配置 id、状态、创建/关闭时间和终态；集中 Connected > Connecting > Error > Disconnected 规则；关闭后忽略迟到事件；提供可选生命周期诊断事件。

为什么改：MainWindowViewModel 已经同时维护文档册、标签订阅和状态合并，继续增加协议分支会扩大竞态和泄漏风险。

验收：同一配置同时打开终端、SFTP、FTP、插件文档并交错关闭时树状态正确；1000 次开关/重连后订阅和会话可释放。

### 3.2 抽取 ConnectionCoordinator

拟修改位置：

- ConnectionWorkflowService.cs
- MainWindowViewModel.cs
- ConnectionProfileViewModel.cs
- ConnectionProfileView.axaml
- 连接错误和取消测试

改动内容：统一 DNS、代理、连接、认证、通道、目录初始化阶段；阶段事件带配置 id、会话 id、取消令牌和 correlation id；连接窗口显示阶段并可取消；错误统一分类。

为什么改：当前表单只有 IsBusy，用户看不到连接卡在哪一步，不同协议分支会继续扩张。

验收：每个阶段可取消且不留下孤立会话；失败卡支持重试、编辑和复制脱敏诊断；连接状态与会话树一致。

### 3.3 抽取 TransferCoordinator

拟修改位置：

- 新建 src/VelaShell.Core/Sftp/TransferCoordinator.cs
- FileBrowserViewModel.cs
- FileTransferViewModel.cs
- TransferManager.cs
- 传输测试和 UI 测试

改动内容：把计划、并发、冲突、断点、重试和进度汇总移出文件浏览器；增加暂停/恢复、批量冲突决策、失败批量重试、动态限速、流式哈希和可选队列快照；大文件统一流式读写。

为什么改：文件浏览器承担过多传输业务，策略修改会影响导航和 UI。

验收：单文件、多文件、目录、冲突、取消、重试和断点行为保持；吞吐、内存、进度和失败保留有基线。

## 阶段 4：协议能力和错误模型

### 4.1 能力描述符先评审后改 SDK

拟修改位置：

- src/VelaShell.Infrastructure/Plugins/Protocols/
- ConnectionProfileViewModel.cs
- 协议注册和表单模型
- SDK 仓库对应契约，另起 PR
- SDK 发布后才修改 src/Directory.Packages.props

改动内容：为终端、远程文件、默认路径、认证后命令、隧道和动态字段定义版本化能力；宿主根据能力生成表单和命令可用性；旧插件能力缺失时提供兼容默认值。

为什么改：宿主不应知道越来越多具体协议；能力描述可让新协议复用现有工作流。

验收：现有协议行为不变；旧插件能力缺失时不崩溃；SDK、包版本和文档同步。

### 4.2 统一错误和诊断导出

拟修改位置：

- Core 层新增错误模型
- FileBrowserViewModel、FileTransferViewModel
- Infrastructure Plugins
- ConnectionDiagnosticsView.axaml
- 五份 resx 和测试

改动内容：错误包含 ErrorKind、文案键、原始异常、correlation id 和可执行动作；界面显示简短原因，详细原文折叠并可复制；诊断导出默认脱敏主机、路径、用户名、凭据和插件机密；逐步替换直接展示 Exception.Message 的路径。

为什么改：原始异常不可本地化、不可操作且可能撑坏布局。

验收：分类和本地化完整；导出无凭据；重试/编辑/关闭动作只在适用错误出现；日志仍可定位原始异常。

## 阶段 5：UI 和无障碍

### 5.1 文件浏览器空态与窄窗口

拟修改位置：

- FileBrowserView.axaml、FileBrowserView.axaml.cs
- FileBrowserViewModel.cs
- 五份 Strings resx
- headless UI 测试

改动内容：区分空目录、无权限、未连接和失败；空态提供上传、新建文件夹、刷新、返回上级；长面包屑折叠中间层并保留 Tooltip；窄窗口折叠低优先级列和本地面板；删除增加撤销或明确影响范围的确认。

为什么改：当前空列表没有下一步引导，窄窗口会让列和操作不可用，危险删除缺少恢复路径。

验收：四种空态和本地化通过；窄窗口仍可导航/上传；删除确认不会误点绕过。

### 5.2 会话树、标签和命令面板

拟修改位置：

- SessionTreeViewModel.cs、SidebarView.axaml
- DockGroupControl 及标签控件
- CommandPaletteView.axaml
- 命令注册表、ShortcutCatalog 和 headless UI 测试

改动内容：增加活动/错误/收藏过滤、批量关闭和拖拽预览；状态点提供文本和 AutomationProperties；标签下拉支持键盘选择和关闭；命令面板显示快捷键、禁用原因、最近使用，并在大量结果时虚拟化。

为什么改：状态不应只靠颜色，平铺大量结果增加认知成本，多个入口容易造成标题和快捷键不一致。

验收：键盘可完成过滤、激活、关闭和命令执行；屏幕阅读器可读状态；新增快捷键通过 ShortcutCatalogTests。

### 5.3 传输、通知和诊断面板

拟修改位置：

- FileTransferView.axaml、FileTransferViewModel.cs
- ConnectionDiagnosticsView.axaml
- 资源和 UI 测试

改动内容：按活动/完成/失败分组，显示批次汇总和失败数；失败项保留一键重试；长错误折叠并提供复制；诊断步骤显示阶段、耗时、原因和下一步动作；保留 100 行保护但按组清理完成项。

为什么改：已有取消和重试，但长批次仍是平面列表，用户难以判断失败数量和处理优先级。

验收：1、100、5000 文件批次都能快速找到活动/失败项；失败不会因清理消失；悬停查看时不自动隐藏。

### 5.4 焦点、AutomationProperties 和 Reduced Motion

拟修改位置：

- Themes 公共按钮、输入框、浮层样式
- 主要交互视图 XAML
- AppSettings 外观/辅助选项
- 五份 resx 和 headless 焦点测试

改动内容：补齐会话树、标签、图标按钮、状态点和错误提示的本地化 AutomationProperties；统一 Esc 关闭、焦点回收、Tab 顺序和键盘焦点环；增加 Reduced Motion；继续遵守 DESIGN.md 的 DynamicResource 颜色字号纪律。

为什么改：业务控件的无障碍名称覆盖不足，动画没有统一的低动态入口。

验收：关键流程可只用键盘完成；焦点不落到隐藏控件；无障碍名称有五份翻译；Reduced Motion 下不出现持续过渡。

## 阶段 6：P2 功能候选（前置评审，不立即实现）

1. SSH config 导入：复用导入框架，解析 Host、HostName、Port、User、IdentityFile、ProxyJump，提供冲突预览。
2. Anti-idle：与 SSH keepalive 分开建模，明确用户主动开启、发送内容、录制/审计标记和远端副作用。
3. 用户自定义关键字高亮：规则数量、正则超时、颜色 token、导入导出和失控保护。
4. 触发器/自动应答：输出匹配、敏感输入保护、逐条审批、循环检测和审计。
5. 串口插件与 SSH 证书认证：先完成插件 SDK/Tmds.Ssh 可行性调研，不在宿主加临时协议分支。
6. sixel/图形终端：先评估渲染后端、缓冲、录制和带宽，再决定是否进入核心终端。

每项开始前补充用户场景、协议约束、数据模型、权限/安全影响、性能预算、测试矩阵和 velashell-docs 双语方案。

## 实施顺序和提交边界

1. 提交 A：只增加基准、诊断计数和静态扫描，不改变产品行为。
2. 提交 B：只处理文件/目录请求取消和回归测试。
3. 提交 C：分别提交关闭路径和写/resize 队列，避免难以定位回归。
4. 提交 D：基准确认后再实现大目录和数据库策略。
5. 提交 E：先迁移 SessionRegistry/ConnectionCoordinator，再删除旧分支，每步保留适配层。
6. 提交 F：先服务化传输和错误模型，再增加新功能。
7. 提交 G：UI/无障碍按面板独立提交，资源和测试同步。
8. 提交 H：P2 功能逐项评审，不与性能重构绑定。

## 每阶段固定验收清单

- dotnet build VelaShell.slnx 无新增警告。
- dotnet test VelaShell.slnx 通过，记录跳过项的 [SKIP] 原因。
- 受影响的 headless UI、基础设施和集成测试单独运行。
- 性能改动提供前后 P50/P95、峰值内存、分配和输入延迟。
- 新增文案补五份 resx，快捷键登记 ShortcutCatalog。
- UI 没有颜色/字号字面量，主题继续使用 DynamicResource。
- 插件契约变化在 SDK 仓库有对应 PR 和发布说明。
- 行为、配置、快捷键、构建或 UI 规格变化安排 velashell-docs 中英文同步 PR。
- 记录回滚方式、数据/配置迁移需求和新路径失败时的关闭策略。

## 当前状态

本文件只记录拟议修改和原因。截至生成时，除新增本文件外没有执行上述任何代码、配置、测试或 SDK 变更。


## 实际修改内容（待执行，当前未应用）

以下内容是后续真正提交时要写入代码的具体 diff 蓝图，包含目标文件、成员/方法、测试和行为。当前工作区没有应用这些修改。

### 1. 文件浏览器导航取消

目标：src/VelaShell/ViewModels/FileBrowserViewModel.cs。

新增当前导航 CancellationTokenSource 和导航锁；新增 BeginNavigation、CancelNavigation。每次 NavigateToAsync 开始时取消并释放旧导航源，再创建与调用方令牌和生命周期令牌链接的新源。ListDirectoryAsync 使用该令牌，返回后同时检查导航版本、当前路径和令牌状态。静默刷新使用独立令牌，显式导航优先；驱逐浏览器和关闭标签时取消在飞请求。

新增测试：NavigateToAsync_CancelsPreviousDirectoryRequest、NavigateToAsync_OnlyLatestRequestCommits、RefreshSilentlyAsync_DoesNotOverwriteExplicitNavigation、FileBrowserDetach_CancelsInFlightNavigation。

原因：当前版本号只能丢弃过期结果，不能停止旧网络请求；快速导航会浪费通道和服务器资源。

### 2. 终端指标和关闭流程

目标：src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs、src/VelaShell.Terminal/SshTerminalBridge.cs。

新增输出合并数、UI 回调数、绘制行数、绘制耗时、输入到回显延迟和写队列深度指标；计数不在 cell 热路径分配字符串。SshTerminalBridge 增加 IAsyncDisposable 和 DisposeAsync，先完成输入队列、取消读写、释放流，再异步等待任务；同步 Dispose 不再调用 Wait，只触发幂等关闭。

新增测试：TerminalOutputBurst_ReportsCoalescing、DisposeAsync_DoesNotBlockUiScheduler、DisposeAsync_IsIdempotent、DisposeAsync_WakesBlockedRead、BoundedWriteQueue_PreservesInputOrder。

原因：先取得帧性能和队列基线，再决定是否实现脏行；当前同步 Wait 可能阻塞 UI 或造成死锁。

### 3. SSH 写队列和插件 resize

目标：SshTerminalBridge.cs、src/VelaShell.Infrastructure/Plugins/Protocols/PluginTerminalShellStream.cs。

把用户输入队列改为有容量并提供背压，输入不能丢失；窗口 resize 单独保存最新尺寸，由一个发送循环合并中间尺寸。插件 Resize 不再每次启动 Task.Run；WriteLine 不再使用 GetAwaiter().GetResult，而是进入统一单写队列。Dispose 先完成写入和 resize 队列，再异步关闭插件会话。

新增测试：Resize_CoalescesIntermediateSizes、WriteLine_PreservesOrderingWithWriteAsync、DisposeAsync_CancelsResizeLoop、PluginTerminal_ClosedSessionDoesNotDeadlock。

原因：无界输入队列和每次 resize 创建后台任务会造成任务、线程和内存增长。

### 4. SonnetDB 异步释放和锁边界

目标：src/VelaShell.Infrastructure/Persistence/SonnetDbEngine.cs、SonnetDbAppDataStore.cs、SonnetDbSessionRepository.cs。

SonnetDbEngine 增加 IAsyncDisposable 和 DisposeAsync；同步 Dispose 不再直接阻塞等待 _gate。序列化移到数据库锁外，WriteManyAsync 增加取消检查；只有真正访问 SonnetDB 时才持有锁。退出时排空队列并报告超时。

新增测试：DisposeAsync_WaitsForActiveOperation、OperationAfterDispose_IsRejected、JsonSerialization_DoesNotHoldDatabaseGate、WriteManyAsync_HonorsCancellation。

原因：全局 _gate 当前串行化集合和时序操作，并且同步释放可能阻塞 UI；先缩小锁范围，不直接假设数据库支持并发写。

### 5. 构建警告

目标：plugins/VelaShell.Plugin.Ai/Ui/ChatPanelView.Editing.cs:46。

确认 DeleteFromAsync 是否需要返回删除结果；需要时改为 Task<bool>，不需要时添加局部、有原因的 CA1859 抑制。新增成功、未找到和取消测试。

原因：当前构建有一条 CA1859 警告，零警告门禁才能发现新的问题。

### 6. 大目录分批

目标：ISftpService 及 SFTP/FTP 实现、RemoteFsCapability、FileBrowserViewModel、FileBrowserView.axaml。

先确认协议是否支持页游标或流式列举；支持时新增内部 DirectoryPage/DirectoryBatch，首批先提交、后续批次追加；不支持时提供批量适配、继续加载和条目数量提示。选择恢复按 FullPath 进行，排序和过滤按批次处理；空目录、失败重试和加载更多文案补五份 resx。headless 测试确认 ListBox 没有被布局禁用虚拟化。

新增测试：LargeDirectory_ShowsFirstBatchBeforeCompletion、LargeDirectory_CancelsRemainingBatches、LargeDirectory_PreservesMultiSelection、EmptyDirectory_ShowsNextAction。

原因：当前全量列举和 ReplaceAll 会造成大目录首屏停顿、内存增长和大量 UI 重建。

### 7. 会话注册和连接阶段

目标：新建 src/VelaShell.Presentation/Services/SessionRegistry.cs、ConnectionCoordinator.cs，并调整 MainWindowViewModel.cs、ConnectionWorkflowService.cs、ConnectionProfileViewModel.cs。

SessionRegistry 记录会话 id、配置 id、类型、状态、创建/关闭时间和终态；集中 Connected、Connecting、Error、Disconnected 合并规则；Forget 后忽略迟到事件。ConnectionCoordinator 定义 Resolving、Connecting、Authenticating、OpeningChannel、LoadingDirectory、Connected、Failed、Cancelled 阶段，发布带会话 id、取消令牌和 correlation id 的进度。连接窗口新增 CurrentStage 和取消命令，但保留关闭窗口后由宿主发起连接的现有语义。

新增测试：SessionRegistry_MergesTerminalAndDocumentSessions、SessionRegistry_IgnoresLateEventAfterForget、ConnectionCoordinator_ReportsStagesInOrder、ConnectionCoordinator_CancelDuringAuthenticationClosesSession。

原因：主窗口目前同时维护多套会话状态和协议连接分支，IsBusy 也不能告诉用户连接卡在哪一步。

### 8. 统一错误和诊断导出

目标：Core 新错误模型、FileBrowserViewModel、FileTransferViewModel、Infrastructure Plugins、ConnectionDiagnosticsView.axaml、五份 resx。

新增 ErrorKind 和 UserError，区分认证、超时、网络、权限、协议不支持、插件不可用、取消和未知错误；错误携带文案键、详细信息、correlation id 和可执行动作。界面显示短文案，详细原文折叠并可复制；诊断导出包含版本、平台、会话摘要、最近错误和指标，默认脱敏主机、路径、用户名、凭据和插件机密。

新增测试：ErrorMapper_ClassifiesAuthenticationAndTimeout、DiagnosticExport_RedactsSecrets、ErrorActions_ShowOnlyWhenApplicable。

原因：直接显示 Exception.Message 不可本地化、不可操作且容易撑坏布局。

### 9. UI、无障碍和资源

目标：FileBrowserView.axaml、SidebarView.axaml、DockGroupControl、CommandPaletteView.axaml、FileTransferView.axaml、ConnectionDiagnosticsView.axaml、Themes 公共样式。

文件浏览器增加空目录、无权限、未连接三种空态，提供上传/新建/刷新/连接动作；面包屑折叠中间段；删除确认显示数量和影响范围。会话树增加活动/错误/收藏过滤和批量关闭；状态点、标签、图标按钮、错误区域补本地化 AutomationProperties。命令面板显示快捷键和禁用原因，大量结果虚拟化。传输面板按活动/完成/失败分组并保留失败重试。公共浮层统一 Esc 关闭、焦点恢复、Tab 顺序和 Reduced Motion。

新增测试：FileBrowserEmptyStateUiTests、SessionTreeFilterUiTests、DockTabOverflowKeyboardUiTests、TransferPanelGroupingUiTests、AccessibilityNameCoverageTests、EscapeRestoresFocusUiTests、ReducedMotionDisablesTransitionsUiTests。

原因：这些是用户直接感知的交互变化，必须落实为 XAML、ViewModel、资源和 headless 测试，而不是停留在方案文字中。

### 10. 文档和契约同步

代码真正提交时，新增文案同步 Strings、Strings.zh-Hans、Strings.zh-Hant、Strings.ja、Strings.ko；快捷键先登记 ShortcutCatalog；连接阶段、目录分页、空态、删除撤销和 Reduced Motion 同步 velashell-docs 的 zh/en；插件能力或 RPC 变化先在 SDK 仓库设计和发布，再升级 Directory.Packages.props。

以上是实际修改内容的可执行蓝图。本轮仍未写入任何源代码、配置、测试或 SDK，未执行迁移，也未改变现有运行行为。

