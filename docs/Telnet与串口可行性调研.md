# Telnet 与串口连接：可行性调研

> 编写日期：2026-07-22　　基准代码：`tmds-ssh` 分支
>
> 用途：为后续实现 Telnet / 串口两种会话类型提供**事实依据与改造清单**。
> 所有代码结论均有 `文件:行号` 依据；联网查证的部分标注了版本与许可证，未查实的一律标注"未核实"。

> **2026-08-17 落地说明（Telnet 已实现，且是插件而非内建）**
>
> 本文第三节整套「协议泛化」改造**没有做，也不需要做**：Telnet 走的是 S3 之后新增的
> 插件协议能力，会话类型仍是既有的 `ConnectionType.Plugin`，因此 3.1 的四处枚举钳制、
> 3.5 的 `SessionProfile` 加字段（五处手写拷贝）全部免掉。实际落地形态：
>
> - SDK 新增**终端协议**能力：`IProtocolTerminal` / `IProtocolTerminalSession`
>   （字节双工 + Resize），与既有的 `IProtocolFileSystem` 并列；新增
>   `ProtocolFeatures.NoCredentials`（收起用户名/口令两栏）。
> - 宿主新增适配层 `PluginTerminalShellStream`：插件会话 → `IShellStreamWrapper`，
>   于是第二节「可直接复用」那张表**全部兑现**（桥、VT 引擎、ZMODEM、会话日志、重连状态机）。
> - 分派点只加了一处判断（注册表里挂的是终端还是文件系统），3.2 的两个分派点各一行。
> - 3.4 的守卫大多已被既有条件覆盖（隧道已挡 `Plugin`，任务管理器/文件面板按
>   `SessionId == Guid.Empty` 自动灰）；真正改的只有 `RebindFileBrowser`——
>   它此前会让 Telnet 标签继续显示上一个 SSH 会话的文件面板（正是 3.4 记录的那个 bug）。
> - 3.6 的「本地回显」没有做进终端层:由 Telnet 插件在自己的读管道里注入回显解决,
>   宿主终端层零改动。
> - 实现:`plugins/VelaShell.Plugin.Telnet`(RFC 854 协商 / NAWS / TERMINAL-TYPE /
>   8 位透明,零第三方依赖,与 §4.2 的选型结论一致)。
>
> **串口尚未实现**,连接页仍是禁用占位。它将复用同一套终端协议能力做成
> `velashell.serial` 插件——第四、五节关于 `System.IO.Ports`、三平台端口枚举、
> `Close()` 死锁与打包的结论**依然有效**,只是那些代码落在插件里而不是宿主里。

---

## 一、结论先行

**好消息：这个项目的终端栈已经为此做好了准备，而且不是巧合——作者早就预留了。**

`src/VelaShell.Core/ZModem/Abstractions/IByteDuplex.cs` 的注释原文：

> 与具体传输（SSH Shell、本地 ConPTY、**未来的串口 / Telnet**）解耦……

而 `ConnectionProfileView.axaml:173-197` 的协议标签页里，**Telnet 与串口的入口已经占好位**（当前 `IsEnabled="False"`），
`Profile_Serial` 这个本地化键**在五个 resx 里都已存在**。

**关键判断：`IShellStreamWrapper` 抽象足够通用，不需要改签名。**

它只有"字节双工 + 一个尺寸通知"，**没有任何 SSH 类型、没有 pty 参数、不做编码转换**。
SSH 特有的 pty 参数全在工厂方法 `ISshClientWrapper.CreateShellStreamAsync` 上，不在流接口上。
最强证据是：**本地终端（ConPTY）已经走通了这条路**，`MainWindowViewModel.cs:663-772` 整套流程零 SSH 依赖。

**真正的工作量不在传输层，而在"协议泛化"** —— 全仓有一批地方隐含假设了"非本地终端 = SSH"。

---

## 二、可直接复用（零改动）

| 组件 | 位置 |
|---|---|
| `IShellStreamWrapper` 契约本身 | `Core/Ssh/IShellStreamWrapper.cs` |
| 终端桥（读循环合批、EOF→Closed、回显抑制） | `Terminal/SshTerminalBridge.cs` |
| VT 引擎与自绘控件全套 | `Terminal/Emulation/*`、`Rendering/VelaTerminalControl.cs` |
| **ZModem 全链路** | `Core/ZModem/*`、`Terminal/ZModem/*` |
| 标签生命周期（Attach/Detach/重连状态机/断开覆盖层） | `ViewModels/TerminalTabViewModel.cs` |
| 会话日志与会话录制 | 挂 `Bridge.DataReceived` |
| 终端搜索、导出缓冲区、同步输入、命令补全 | 全部基于模拟器，与传输无关 |
| SonnetDB 持久化管线 | `Infrastructure/Persistence/*` |

> **ZModem 白捡**：只要新传输实现 `IShellStreamWrapper` 并经 `AttachTransport` 挂载，ZModem 自动可用。
> 但有两个前置条件，不满足会**静默损坏传输**：
> - **Telnet**：0xFF 必须做 IAC 双写/还原，且**绝不能对整个输出流做 CRLF 改写**
> - **串口**：必须 8 数据位、**无 XON/XOFF 软流控**（软流控会吃掉 0x11/0x13）

---

## 三、必须改造的地方（协议泛化）

### 3.1 四处硬编码的枚举钳制（**不改会静默丢数据**）

`ConnectionType` 目前只有 `SSH=0` / `SFTP=1`，且有四处三元钳制把未知值改写成 SSH：

| 位置 | 代码 |
|---|---|
| `Core/Models/SessionProfile.cs:10` | `value == ConnectionType.SFTP ? SFTP : SSH` |
| `Core/Models/RecentConnectionEntry.cs:13` | 同上 |
| `Infrastructure/Persistence/SonnetDbRecentConnectionService.cs:127-130` | `ParseConnectionType` |
| `ViewModels/ConnectionProfileViewModel.cs:158-160` | VM 层再钳一次 |

这四处正是"历史数据兼容策略"的实现（未知值 → SSH）。
**改法**：三元换成 `Enum.IsDefined` 白名单，语义不变但可扩展。

> 兼容性测试 `ModelSerializationTests.cs:86-105`、`:235-254` 断言"缺失键→SSH、值 99→SSH"，
> 加 `Telnet=2/Serial=3` 后仍然通过（99 依然未知），但需补各自的往返测试。
>
> **注意单向降级风险**：旧版本读到 `connectionType: 2` 会当成 SSH 并可能覆写保存。
> 这是既有策略的固有代价，不是新引入的问题。

### 3.2 两个主分派点（最关键）

- **`MainWindowViewModel.cs:2112-2116`** — `TryConnectProfileAsync` 开头只判 SFTP，其余全走 SSH。**这是新协议要插分支的地方。**
- **`MainWindowViewModel.cs:1779-1791`** — `ReconnectTabAsync` 非 `LocalShell` 即走 SSH 工作流。
  **Telnet/串口标签带 Profile，会误入 SSH 重连路径**，必须加分支。

### 3.3 校验谓词会挡死串口

- `ConnectionProfileViewModel.cs:126-138` — `canExecute` 强制 `Host` 与 `Username` 非空、`Port` 1-65535
- `ConnectionWorkflowService.ValidateProfile:239-256` — 同样强制

**串口三者都不适用，保存/连接按钮会永远灰着。** 需按 ConnectionType 分派校验谓词。

### 3.4 其余需要加守卫的 SSH 专属功能

| 位置 | 问题 |
|---|---|
| `MainWindowViewModel.cs:1026-1029` | 隧道面板已挡 SFTP，需同样挡 Telnet/Serial |
| `MainWindowViewModel.cs:1117-1126` | Ping 延迟用 `Profile.Host` —— 串口无 Host |
| `MainWindowViewModel.cs:785-808` | `RebindFileBrowser` 用 `LocalShell is not null` 判"无 SFTP"。**新类型会显示上一个 SSH 会话的文件内容**（注释里记录过这个 bug） |
| `MainWindowViewModel.cs:1744-1751` | `ResourceMonitor` 依赖 SSH `SessionId` |
| `MainWindowViewModel.cs:1656-1658` | 状态栏硬编码 `$"SSH • {name}"` |
| `SessionTreeNodeViewModel.cs:22-31` | `IsSshProfile`/`CanOpenSftp` 判据需扩展 |

### 3.5 配置模型的加法

`ConnectionInfo` 是 `required` + `init` 的**纯 SSH 传输参数**，**不要扩展它** ——
Telnet/串口不应经 `ConnectionWorkflowService` 走 SSH 握手。

符合现有兼容策略的做法是在 `SessionProfile` 上加两个**可空嵌套对象**（缺失即 null，旧数据零影响）：

```csharp
public SerialSettings? Serial { get; set; }   // PortName/BaudRate/DataBits/StopBits/Parity/Handshake/DTR/RTS/换行归一化
public TelnetSettings? Telnet { get; set; }   // TerminalTypeOverride/PreferBinary/EnterMode(CRLF|CRNUL|CR)/LocalEcho
```

理由：与现有 `JumpHostProfileId` 的手法一致；不污染 SSH 路径的平铺字段；
避免用 `Host`/`Port` 承载 `COM3`/`115200` 这种语义走私。

**代价**：`SessionProfile` 全仓是**逐字段手写拷贝**（无 `with`/克隆方法），新增字段必须五处同步：
`SonnetDbSessionRepository.cs:131-151`、`ConnectionWorkflowService.cs:113-131`、
`SessionTreeViewModel.cs:340-355`、`ConnectionProfileViewModel.cs:521-542`、`MainWindowViewModel.cs:2333-2341`。

### 3.6 缺失能力：本地回显

**全仓没有任何本地回显能力**（grep `LocalEcho` 零命中），`VelaTerminalControl.WriteInput:379` 只发不回显。
Telnet 行模式与"无回显串口设备"都需要它。这是唯一一处需要在终端层**新增**能力的地方。

---

## 四、技术选型

### 4.1 串口

**推荐：`System.IO.Ports` 10.0.10（MIT，2026-07-14）**，与仓库许可策略一致。
跨平台已确认支持 Windows/Linux/macOS（RID 原生包覆盖齐全）。

**备选**：`RJCP.SerialPortStream` 3.0.5（**MS-PL**，显式 target net10.0，Windows/Linux 扎实，macOS 较弱）。
⚠️ MS-PL 是弱 copyleft，本仓库有 `LICENSE-COMMERCIAL.md`，**走商业授权前需确认合规**。
`SerialPort.Net` 已停维护（2021），不建议。

#### 已确认的坑（均为 dotnet/runtime 仍 open 的 issue）

| 问题 | Issue | 影响 |
|---|---|---|
| `DataReceived` 不可靠 | #106631 | 115200 波特下丢字节。**别用事件，用阻塞 `Read()` 包 `Task.Run()`** |
| `Close()` 死锁 | #20362 | 硬件流控 CTS 卡住时 `Close()` 永久阻塞 |
| `Close()` 竞态 NRE | #44952 | |
| **`BaseStream.ReadAsync` 在 Windows 上不响应 CancellationToken** | #30850 | 微软自己提的，Future 里程碑，从未修。Unix 响应 —— **平台不对称** |

> **本项目白捡的便宜**：`SshTerminalBridge.Dispose:50-61` 本来就是
> "**先 Dispose 流唤醒读取、令牌只做兜底**"的设计，天然绕开 #30850。
> 且 `TerminalTabViewModel.DetachTransport:673` 已是 `Task.Run(bridge.Dispose)`，避开了 #20362 的 UI 线程死锁。
> **但任何新增的同步 Dispose 路径都会重新引爆这两条。**

#### 端口枚举（三平台做法不同）

- **Windows**：`SerialPort.GetPortNames()` 读注册表。官方文档明确警告"返回顺序未指定"
  → **必须自己按数字排序**（否则 COM2/COM10 字符串排序错乱）。
  友好名（"USB Serial Port (COM3)"）要走 WMI `Win32_PnPEntity` 过滤 `Caption` 含 `"(COM"`。
  ⚠️ `System.Management` 是 Windows-only，必须 `OperatingSystem.IsWindows()` 守卫（CA1416）。
- **Linux**：枚举 `/dev/ttyS*`（真 UART）、`/dev/ttyUSB*`（FTDI/CP210x/CH340）、`/dev/ttyACM*`（Arduino）。
  用 `/sys/class/tty/*/device/driver` 过掉 8250 驱动注册的幽灵 `ttyS*`。
  *未核实*：从 sysfs 取 USB 友好名的确切路径字面值。
- **macOS**：`/dev/tty.*` 是 dial-in，**会阻塞等待 DCD 载波**；`/dev/cu.*` 是 call-out，立即打开。
  **终端类应用必须用 `/dev/cu.*`**，否则连一个没接线的适配器就永久挂起。
  *未核实*：Apple 官方文档的明确表述（现依据 pySerial 源码 + 第三方技术参考）。

### 4.2 Telnet

**结论：截至 2026-07，没有可直接用作 VT100 全屏终端网络层的成熟 .NET Telnet 库。建议基于 `TcpClient` 自实现。**

| 包 | 版本 / 最后发布 | 许可 | 真协商？ |
|---|---|---|---|
| `PrimS.Telnet` | 0.13.1 / 2024-11 | MIT | **否** —— 定位是"发命令抓响应"的自动化助手 |
| `TelnetNegotiationCore` | 2.5.3 / **2026-07（活跃）** | Apache-2.0 | **是**，但定位 MUD，**自述不提供 VT100 仿真** |
| `TentacleSoftware.Telnet` | 2.1.0-rc1 / 2018 | — | 已废弃 |
| `System.Net.Telnet` | — | — | **不存在** |

RFC 854 协议面很小，且本项目**已有现成的 VT 引擎，只缺协商层**。
为几百行代码引入一个 MUD 导向依赖，性价比不高。

#### 协议常量（已从 RFC 原文逐条确认）

```
IAC=255  WILL=251  WONT=252  DO=253  DONT=254  SB=250  SE=240

IAC 转义：数据流中字面 0xFF 发为 IAC IAC (255 255)，接收方还原为单个 0xFF
子协商帧：IAC SB <option> <params...> IAC SE     （params 内的 0xFF 同样要双写）
```

#### 跑 vt100 全屏程序的最小必需协商集

| 选项 | 码 | RFC | 必需性 |
|---|---|---|---|
| **SUPPRESS-GO-AHEAD** | 3 | 858 | **必需** |
| **ECHO** | 1 | 857 | **必需** |
| **TERMINAL-TYPE** | 24 | 1091 | **必需**（否则远端不知道 TERM） |
| **NAWS** | 31 | 1073 | **必需**（否则 htop/vim 按 80x24 画） |
| BINARY | 0 | 856 | 强烈建议（UTF-8 / ZModem 8 位透明），按方向分别协商 |

**核心机制**：RFC 858 原文明确 ECHO 与 SGA "normally have to be in effect simultaneously
to effect what is commonly understood to be **character at a time echoing**"。
即服务端 `WILL ECHO` + `WILL SGA`、客户端两者都回 `DO` → 进入逐字符 + 远端回显模式，
**这是 vim/htop 能工作的前提**。

> **kludge line mode**：若服务端没同时给出 ECHO+SGA，会退化成本地行缓冲，
> 服务端要等 Enter 才看到输入 —— curses 全屏程序无法工作。
> **这是很好的运行时诊断点**：协商完成后若两者未同时到手，应给用户明确警告。

**NAWS 字节布局**（RFC 1073，两次独立取证确认）：

```
IAC SB 31 <width-hi> <width-lo> <height-hi> <height-lo> IAC SE
```

两个 16 位大端值。RFC 1073 原文："any occurrence of 255 in the subnegotiation must be doubled"
—— **这 4 个载荷字节里等于 0xFF 的都要双写**（如 width=255 → 线上发 `0x00 0xFF 0xFF`）。
**这条极易漏，且只在窗口宽/高恰为 255 或 65280+ 时才暴露。**

**TERMINAL-TYPE**：`SEND=1, IS=0`。服务端 → `IAC SB 24 1 IAC SE`；客户端 → `IAC SB 24 0 'V','T','1','0','0' IAC SE`。

### 4.3 换行处理（与现有 VT 引擎对接）

**现状**：`Terminal/Emulation/InputEncoder.cs:51` —— `Key.Enter => modes.NewLineMode ? "\r\n" : "\r"`，
**默认发裸 `\r`**（LNM 默认关）。入方向裸 CR = 只回车不换行。

**Telnet**：RFC 854 规定 `CR LF` 是 newline，**单纯回车必须用 `CR NUL`**，裸 CR 在 NVT ASCII 里非法。
RFC 1123 §3.3.1 要求客户端可配置，**默认 SHOULD 是 CR LF**。
→ **Telnet 传输层必须自己把出方向的裸 `\r` 改写成 `\r\n`，不能指望 LNM。**

**串口**：没有任何协议层换行契约，大量嵌入式设备只发裸 CR，
喂进 VT 引擎后表现为**每行覆盖上一行**。
PuTTY/minicom/screen 因此都提供 "Implicit LF in every CR" 开关，**必须新增同类开关**。
*（此段为共识级结论，未逐条抓 PuTTY/minicom 一手文档。）*

---

## 五、主要风险点

### 串口

1. **`Close()` 在硬件流控卡住时永久阻塞**（#20362）—— 在 UI 线程 Dispose 会整窗死锁。
   现有设计已挡住，但**新增的同步 Dispose 路径会重新引爆**。
2. **`PublishSingleFile` + `SelfContained`**（`VelaShell.csproj:15-19`）与 `runtime.native.System.IO.Ports` 的打包 ——
   Linux/macOS 产物可能缺 `libSystem.IO.Ports.Native`，表现为运行时 `PlatformNotSupportedException`，
   **本地开发机（Windows）测不出来**。必须扩 `tests/VelaShell.Tests/Integration/CrossPlatformPublishTests.cs`。
3. USB 转串口热插拔的 IOException 风暴，需按 ConPTY 做法归一化为 EOF（`ConPtyShellStream.ReadAsync:58-69`）。
4. **无法自动化验证**：需要真实硬件或 `socat` / `com0com` 虚拟串口对。

### Telnet

1. **IAC 双写/去双写必须覆盖全部字节路径**。漏做的表现是"平时都好，传大文件或输出含 0xFF 时随机损坏"
   —— **会直接毁掉 ZModem，且极难定位**。
2. **NAWS 载荷内的 0xFF 双写** —— 只有窗口宽/高恰为 255 时才触发，几乎必然被漏掉，典型潜伏 bug。
3. **CRLF 改写的作用域** —— 只能作用于用户 Enter 按键，作用于整个出方向流会污染 ZModem 与粘贴数据。
   这与"传输层只搬字节"的原则直接冲突，而 **`IShellStreamWrapper` 只有一个 `WriteAsync`，无法区分二者**。
   这是唯一一处抽象**可能真需要扩展**的地方。
   替代方案：BINARY 协商成功后一律不改写；未成功时才在传输层做 CR→CRLF，
   并在 ZModem 会话期间由路由器临时置位旁路标志。
4. **服务端不给 ECHO+SGA** 时用户看不到自己的输入 —— 需要本地回显兜底 + 明确提示。
5. **安全语义缺失**：明文协议，无主机密钥、无加密。
   现有 `IHostKeyService` / `SecurityAlertService` / known-hosts 整条链路**不适用**，
   UI 上需要明确的"不加密"标识，避免用户误以为与 SSH 同等安全。

---

## 六、实施建议

### 工作量

| 维度 | 串口 | Telnet |
|---|---|---|
| 新增文件 | 6–8 | 6–8 |
| 修改文件 | ~16（第三节全表 + 5 个 resx） | ~6（大部分与串口共享） |
| 新增测试 | 3–4 | 3–4 |

> **第三节的"协议泛化"改造是两者共用的。先做哪个都要把它做完；
> 第二个功能的成本大约只有第一个的 40%。**

### 建议顺序：**先 Telnet，后串口**

理由：Telnet 纯托管、可用 Docker 起 telnetd 做集成测试（仓库已有 `docker-compose.test.yml` 的先例）；
串口依赖真实硬件，难以自动化验证。

先做 Telnet 能把"协议泛化"那批改造在**可测的前提下**完成，
串口接上时就只剩它自己的传输实现与 UI 表单了。

### 需要新写的文件

**共用**
- 本地回显组件（`VelaShell.Terminal` 内，可选启用）
- 五个 resx 新键（现 938 键，**五语必须同步**）

**Telnet**
- `Core/Models/TelnetSettings.cs`
- `Infrastructure/Telnet/TelnetOptions.cs`（常量表）
- `Infrastructure/Telnet/TelnetNegotiator.cs`（IAC 状态机、选项策略表、子协商编解码）
- `Infrastructure/Telnet/TelnetShellStream.cs`（`IShellStreamWrapper`，`Resize`→NAWS）

**串口**
- `Core/Models/SerialSettings.cs`、`SerialPortInfo.cs` + `ISerialPortEnumerator`
- `Infrastructure/Serial/SerialShellStream.cs`（阻塞 `Read()` + `Task.Run`，异常归一化 EOF，`Resize` 空操作）
- `Infrastructure/Serial/SerialPortEnumerator.cs`（三平台实现）
