# VelaShell.Terminal.Tests

> [`VelaShell.Terminal`](../../src/VelaShell.Terminal) VT 引擎与渲染层的单元测试。

以白盒方式（引擎经 `InternalsVisibleTo` 暴露 `internal`）逐条对照 VT/Xterm 规范验证仿真正确性，是全项目最密集的测试集之一。

## 覆盖范围

- **仿真核心** — `Emulation/TerminalEmulatorTests`、`AltScreenCursorTests`（主/备屏与光标）、`ResizePreservationTests`（缩放保留）、`CatOutputNewlineTests`（换行语义）、`Emulation/Osc7WorkingDirectoryTests`（OSC 7 上报的终端 cwd —— 文件浏览器「跟随终端目录」的数据源）。
- **内存布局** — `Emulation/TerminalCellMemoryTests`：单元格必须保持无托管引用（组合标记走驻留池索引），否则数百万格的回滚缓冲会被 GC 逐格扫描。
- **缓冲与滚动** — `ScrollbackBufferTests`、`ScrollOffsetTests`、`BufferSearchTests`。
- **输入编码** — `MouseEncoderTests`、`Input/TerminalInputTrackerTests`、`Input/TerminalKeyRouterTests`（按键动作归类，含 IME 组字中间态不得编码）、`EchoSuppressorTests`（本地回显抑制）、`LocalEchoTests`（对端不回显链路的本地回显策略）。
- **选区** — `BlockSelectionTests`：矩形块选（Alt+拖拽）与线性选区的归一化、命中与逐行列区间。`ShiftExtendSelectionTests`：Shift+点击扩展选区（#266）—— 锚点必须原地不动，含反向扩展、块选模式沿用、无选区时退回新建选区。`MultiRegionSelectionTests`：Ctrl+Shift+拖拽追加的不连续多段选区 —— 复制按文档顺序拼接、段间断行，普通拖拽从头选起。
- **补全幽灵** — `GhostTextRemainderTests`：剩余文本必须每帧从已回显文本现算，逐键推演会抖动。
- **侧栏渲染** — `GutterLayoutTests`、`GutterFoldTests`、`GutterFoldUiTests`、`GutterVisibilityTests`、`LineTimestampTests`。
- **桥接与语义** — `TerminalBridgeTests`（SSH ↔ 引擎）、`SemanticMatcherTests`。
- **ZMODEM 路由** — `ZModemRouterTests`：引导序列检测、会话期间终端停喂、结束后复位回常态。
- **广播输入** — `BroadcastInputEncodingTests`：多终端同步输入的按键编码一致性。

## 运行

```bash
dotnet test tests/VelaShell.Terminal.Tests/
```
