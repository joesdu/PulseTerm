# VelaShell.Terminal

> 自研 VT 终端引擎 + Avalonia 自绘渲染控件。

`VelaShell.Terminal` 是 VelaShell 的心脏：一个从零实现的、不依赖任何第三方终端控件的终端仿真器与渲染层。它将远端字节流解析为屏幕状态，再由自定义 Avalonia `Control` 直接绘制字形、选区、滚动与光标。

## 🧬 架构分层

```text
远端字节流 ──► VtParser ──► TerminalEmulator ──► TerminalScreen (主/备屏 + 滚动区)
                (状态机)      (IVtActions)          │
                                                   ├─ ScrollbackBuffer  (回滚缓冲)
                                                   └─ VelaTerminalControl (Avalonia 自绘渲染)
用户输入 ──► InputEncoder / MouseEncoder ──► 编码为终端序列 ──► 远端
```

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Emulation/VtParser.cs` | DEC ANSI / VT / Xterm **状态机**：解析 CSI / OSC / DCS / ESC 转义序列，驱动 `IVtActions`。 |
| `Emulation/TerminalEmulator.cs` | 仿真核心：实现 `IVtActions`，维护光标、字符集、模式（`TerminalModes`）、主/备屏切换与滚动区。 |
| `Emulation/TerminalScreen.cs` `TerminalRow.cs` `TerminalCell.cs` | 屏幕/行/单元格数据结构，`CellFlags` 描述粗体、下划线、反显等属性。 |
| `Emulation/TerminalColor.cs` `TerminalPalette.cs` | 256 色 / 真彩色调色板与 SGR 颜色解析。 |
| `Emulation/CharWidth.cs` `Charsets.cs` | CJK 双宽字符宽度计算、DEC 线绘字符集映射。 |
| `Emulation/CombiningPool.cs` | 组合标记（重音/变音符）的进程级驻留池：单元格只存 `int` 索引，于是 `TerminalCell` 不含任何托管引用 —— 数百万格的回滚缓冲不再被 GC 逐格扫描，每格还省 4 字节。 |
| `Emulation/TerminalType.cs` | 向远端宣示的仿真档位（VT52 … xterm-256color）：决定 `TERM` 字符串、DA 应答与启用的功能集。 |
| `Emulation/InputEncoder.cs` `MouseEncoder.cs` | 键盘（含应用光标键模式）与鼠标协议（X10 / SGR 等）的输入编码。 |
| `Emulation/Utf8Sink.cs` | 动态编码切换与 UTF-8 增量解码。 |
| `Rendering/VelaTerminalControl.cs` | **自绘渲染控件**（热点文件）：直接绘制字形、选区、光标、滚动条；处理输入事件与命中测试。 |
| `Rendering/TerminalSelectionMath.cs` | 选区几何的纯计算：线性选区与矩形块选（Alt+拖拽）共用的归一化/逐行列区间规则，以及一段选区的表示 `SelectionSpan`（不连续多段选区 = 一串它）。行为对齐 Windows Terminal —— 块选与否在**按下那一刻**由 Alt 决定，拖拽途中改按不影响。 |
| `Rendering/GutterLayout.cs` `GutterFoldModel.cs` | 侧栏（行号 / 时间戳）布局与折叠模型。 |
| `Rendering/TerminalPaletteOverrides.cs` | 主题层对调色板的运行时覆盖。 |
| `Input/TerminalKeyRouter.cs` | 一次按键的动作归类（编码为字节 / 命中快捷键 / IME 组字中间态不得编码 / 交还基类），把路由决策从控件里剥离出来单测。 |
| `LocalEcho.cs` | 本地回显策略：把「即将发往主机的键入字节」翻译成「应当喂回终端显示的字节」。用于对端不回显的链路（Telnet 半双工、串口）或主机以 SRM 复位显式要求；SSH 默认关闭，否则远端回显叠加本地回显会出双字符。 |
| `ScrollbackBuffer.cs` `TerminalLine.cs` | 回滚历史缓冲与逻辑行。 |
| `BufferSearch.cs` `SearchMatch.cs` | 缓冲区文本搜索与匹配高亮。 |
| `Semantics/SemanticMatcher.cs` | 语义识别（如 URL / 路径检测）。 |
| `Input/TerminalInputTracker.cs` | 输入焦点与按键跟踪。 |
| `EchoSuppressor.cs` | 本地回显抑制（避免输入被双重显示）。 |
| `SshTerminalBridge.cs` | 将 Core 的 SSH Shell 抽象桥接到终端引擎的 I/O。 |
| `ZModem/ZModemDetector.cs` | 在输出字节流中嗅探 ZMODEM 引导序列（`ZRQINIT` → 本地接收，`ZRINIT` → 本地发送）。 |
| `ZModem/ZModemTerminalRouter.cs` | 插在「桥的读循环 → 终端喂入」之间的路由器：常态透传并监视引导，命中后切入会话态把字节改喂 ZMODEM 引擎，期间终端停喂，会话结束自动复位。 |
| `ZModem/ShellStreamByteDuplex.cs` | 把 `IShellStreamWrapper` 适配为 Core ZMODEM 引擎所需的 `IByteDuplex`。 |
| `ITerminalEmulator.cs` | 仿真器对外接口。 |

## 🔑 核心思路

- **自绘而非复用**：不依赖已废弃或功能受限的第三方终端控件，直接用 Avalonia 底层绘图 API 渲染，完全掌控性能与视觉细节。
- **状态机驱动**：`VtParser` 严格按 DEC/Xterm 规范实现转义序列状态机，仿真逻辑（`TerminalEmulator`）与解析逻辑解耦，便于逐条对照规范测试。
- **主/备屏与滚动区**：完整支持全屏应用（vim、htop）所需的备用屏幕、滚动区域与光标保存/恢复语义。
- **正确的宽字符处理**：`CharWidth` 保证 CJK 与 Emoji 的双宽对齐，避免终端错位。
- **ZMODEM 旁路而非分叉**：`ZModem/` 只做「检测 + 路由」，协议本身在 `VelaShell.Core/ZModem/`，且路由器设计为传输无关（SSH / ConPTY / 未来的串口·Telnet 通用），VT 引擎对 ZMODEM 一无所知。

## 🔗 依赖关系

- **引用**：`VelaShell.Core`（SSH Shell 抽象、ZMODEM 协议引擎）、`Avalonia`（仅渲染控件用）。
- **被引用**：`VelaShell.Presentation`、`VelaShell`（App）。
- `InternalsVisibleTo` 暴露给 [`tests/VelaShell.Terminal.Tests`](../../tests/VelaShell.Terminal.Tests)，可对 `internal` 引擎细节做白盒测试。

> 编译需 `AllowUnsafeBlocks`（渲染热路径使用指针以减少分配）。
