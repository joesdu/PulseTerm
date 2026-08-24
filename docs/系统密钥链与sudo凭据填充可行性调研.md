# 系统密钥链托管凭据 + sudo 提示自动填充：可行性调研

> 编写日期：2026-08-25　　基准代码：`dev` @ `7c43cdd`
>
> 用途：回答两个问题 —— ①能否把凭据交给 Windows / Linux / macOS 的系统密钥链保管；
> ②能否在终端出现 `[sudo] password for xxx:` 时选一条凭据直接送进去，免去弱网下手敲密码。
> 仓库内的结论均带 `文件:行号` 依据；三平台原生 API 的细节标注为**未核实**，须在实现期实测确认
> （本仓既有惯例：适配 API 以实测为准，不凭记忆写死）。

## 0. 结论先说

两件事都能做，但**它们是两件独立的事**，价值和风险都不一样，建议分期：

| | 做什么 | 收益 | 风险 |
|---|---|---|---|
| **阶段零** | 只落抽象层(`ICredentialVault` / `IMasterKeyStore`)+ 文件回退实现 | 平台实现变成可插拔的空位，三平台互不阻塞 | 极低。**零行为变化、零平台代码** |
| **阶段一** | 把现有的**本地主密钥**交给系统密钥链保管 | 抹平 Linux/macOS 上"密钥明文躺在文件里"的短板 | 低。有回退路径，用户无感 |
| **阶段二** | 凭据库 + 密码提示处浮出**不抢焦点的提示条**，点开选凭据填入(也可手动输入) | 正面解决弱网下敲密码出错 | 中。浮层不得成为焦点陷阱；误判提示时不能自动发送 |

阶段一是纯后端替换，阶段二才是用户能看见的功能。反过来做也行（阶段二不依赖阶段一），
但先做阶段一意味着阶段二存的 sudo 密码一落地就是密钥链级别的保护。

## 1. 现状盘点（本仓已有什么）

**已经有一套完整的静态加密，缺的只是主密钥的存放位置。**

- `ISecretProtector`（`src/VelaShell.Core/Data/ISecretProtector.cs`）只有 `Protect`/`Unprotect`
  两个方法，是全仓敏感字段的唯一收口：会话密码与私钥口令（`SonnetDbSessionRepository.cs:13`）、
  云同步的 GitHub PAT 与端到端口令（`SyncModels.cs:34,37`）、插件机密（`SonnetDbPluginDataStore.cs:16`）
  都从这里过。
- 唯一实现 `AesSecretProtector`（`src/VelaShell.Infrastructure/Persistence/AesSecretProtector.cs`）：
  AES-256-GCM，密文格式 `enc1:base64(nonce‖tag‖ciphertext)`，主密钥是 32 字节随机数，
  存在本地密钥文件里。**平台差异就在这里**（`:115-126`）：
  - Windows：`ProtectedData.Protect(key, null, CurrentUser)` —— DPAPI 包裹后落盘，已经是"密钥链级别"；
  - Linux / macOS：**32 字节明文写文件**，只靠 `chmod 0600`。
- 结论：Windows 上这件事其实已经做完了；**真正的缺口在 Linux 与 macOS**。

**密码提示的识别器已经有了，还带测试。**

- `TerminalTabView.IsSecretPrompt(line, typed)`（`src/VelaShell/Views/TerminalTabView.axaml.cs:337`）：
  剥掉行尾已回显的输入后，按"冒号（半/全角）结尾 + 密码类关键词"判定，关键词含
  `password / passphrase / passwd / 密码 / 口令 / verification code / 验证码 / 认证码`（`:340-366`）。
- 现有测试 `tests/VelaShell.Tests/ViewModels/SecretPromptDetectionTests.cs` 直接覆盖了
  `[sudo] password for pi:`、`root@192.168.1.1's password:`、`Enter passphrase for key ...:`、`密码：`。
- 它今天的用途是"密码提示行不弹命令补全"，正好是阶段二需要的那个判据，**不必另写一套启发式**。

**注入通道也已经有了，而且天然不广播。**

- `SshTerminalBridge.SendRaw(byte[])`（`src/VelaShell.Terminal/SshTerminalBridge.cs:203`）：
  程序化注入，入统一出站写队列（`:22` 的注释解释了为什么绝不能并发直写通道），
  连接启动命令走的就是它。
- 关键性质：同步输入（多终端一起敲）挂的是 `TerminalEmulator.TypedInput` 事件
  （`src/VelaShell/Services/SyncInputCoordinator.cs:26`），**`SendRaw` 不触发该事件** ——
  所以走注入通道填密码，不会被广播到频道内其他终端。这是结构上就成立的，不是靠额外守卫。
- 会话录制录的是**桥的输出**（`src/VelaShell/Services/SessionRecorder.cs:7`），不录输入；
  sudo 不回显，所以密码不会进录像（例外见 §5）。

## 2. 三平台密钥链：能用什么

> 以下 API 细节均**未核实**，须在实现期以真机实测确认（含返回码、条目上限、权限弹窗时机）。

| 平台 | 机制 | 调用方式 | 主要坑 |
|---|---|---|---|
| **Windows** | Credential Manager（`CredWriteW`/`CredReadW`，`CRED_TYPE_GENERIC`，底层就是 DPAPI） | `LibraryImport("advapi32.dll")` | 单条 blob 有上限（约 2560 字节，未核实）；本仓已在用 DPAPI，收益主要是"能在凭据管理器里看见/撤销" |
| **macOS** | Keychain Services（`SecItemAdd`/`SecItemCopyMatching`/`SecItemUpdate`/`SecItemDelete`，`kSecClassGenericPassword`） | P/Invoke `Security.framework` | **条目 ACL 绑定代码签名**：签名标识一变（本地 ad-hoc 重签、换证书）系统会重新弹授权框。与本仓 `codesign --deep` 的发布形态相关，须实测 |
| **Linux** | Secret Service（`org.freedesktop.secrets`，gnome-keyring / KWallet 提供） | P/Invoke `libsecret-1.so.0`，或走 D-Bus 自己实现 | **不一定存在**：无桌面会话、纯 SSH 登录、精简发行版上没有 keyring 守护进程，或 keyring 处于锁定态。必须有回退 |

三点判断：

1. **没有合适的跨平台 NuGet。** 现成库要么只覆盖 Windows，要么把三平台各绑一遍且依赖沉重。
   本仓的既有取向是自研薄封装（自研插件 RPC 而非引 StreamJsonRpc、移除 DynamicData），
   这里同理：三个平台各一个 ~80-120 行的 P/Invoke 薄层 + 一个统一接口即可。
2. **这会是本仓第一次写非 Windows 的原生互操作。** 现有 `LibraryImport` 全是 `user32/dwmapi/kernel32`
   （`src/VelaShell/Views/Win32WindowChrome.cs` 等），macOS/Linux 侧没有先例，CI 也没有对应的构建门禁
   （`.github/workflows/` 只有 `release.yml`，触发条件是发布/手动）。跨平台验证得靠真机。
3. **Linux 必须允许失败。** "密钥链不可用就拒绝存凭据"会把无桌面环境的用户直接挡在门外；
   正确做法是回退到今天的 AES 密钥文件，并在设置页如实标注当前用的是哪一种。

## 3. 推荐架构（阶段一）

### 3.1 抽象分层：终端只见凭据库，平台差异锁在最底层

**要两道缝，不是一道。** 终端要的是"给我这台主机能用的凭据"，不是"打开系统密钥链" ——
若让终端直接对接密钥链抽象，Linux 上没有 keyring 的那条回退路径就会渗进 UI 层，变成到处写平台分支。

```
  终端 / 设置页 / 插件
        │   只认这一层：ICredentialVault
        │   List(scope) / RevealAsync(id) / Save(entry) / Remove(id)
        ▼
  SonnetDB 存条目 + ISecretProtector 加解密   ← 已有，一行不改
        │   主密钥从哪来
        ▼
  IMasterKeyStore     ← 平台差异只存在于这一层
        ├─ WindowsCredentialStore   (advapi32 / DPAPI)
        ├─ MacKeychainStore         (Security.framework)
        ├─ LinuxSecretServiceStore  (libsecret / D-Bus)
        ├─ FileMasterKeyStore       (回退 = 今天的行为)
        └─ InMemoryMasterKeyStore   (测试)
```

**这条路本仓走通过。** `VelaShell.Core.csproj` 里写着：Core 不引用任何具体 SSH 库，
SSH 能力经 `Ssh/` 下的中立抽象访问，具体库只存在于 Infrastructure —— 「从 SSH.NET 迁到
Tmds.Ssh 时 Core 一行未改」。密钥链是同一个动作：接口进 `VelaShell.Core/Data/`
(与 `ISecretProtector` 同址)，实现进 `VelaShell.Infrastructure/Persistence/`，
装配在 `InfrastructureServiceCollectionExtensions.cs:51`。终端与设置页不 `#if`、不问平台。

**契约里必须有的东西**(否则抽象会漏)：

- `IsAvailable` + `BackendName` + 保护级别：Linux 回退到文件时，设置页要能**如实**显示
  当前用的是哪一种，不能假装一样安全。抽象的目的是抹平调用方，不是抹平事实。
- **异步**：keyring 解锁会弹系统对话框、D-Bus 是进程间往返，都可能阻塞数秒。
  所以底层是 `ValueTask<byte[]?> TryLoadAsync()`；但 `ISecretProtector.Protect/Unprotect`
  是同步的且被到处调用(`AesSecretProtector.cs:32` 是个 `Lazy<byte[]>`) ——
  **解法是启动时 await 一次把主密钥解析好再交给保护器**，而不是把 async 传染到所有调用方。
  这一点若不在抽象里定死，第一个在 UI 线程首次触碰密钥的调用就会让启动界面卡住。
- **回退链是抽象的一部分**：`探测可用 → 用之；不可用 → 文件回退 → 记录原因`，
  由一个组合实现负责，平台实现各自只管自己那一种，谁都不需要知道别人存在。
- **迁移是幂等的**：首次发现"密钥链可用但链上无条目"时把文件密钥搬上去，
  并保留文件副本一个版本周期(防回滚丢数据)。

**因此第一步可以完全不碰平台代码**：先落 `ICredentialVault` + `IMasterKeyStore` 两个接口、
`FileMasterKeyStore`(就是今天的行为，Windows 上仍走 DPAPI)、`InMemoryMasterKeyStore`(测试)、
回退链与装配。**零行为变化、零平台代码**，之后三个平台各自补一个类即可，互不阻塞，
也不必等三平台真机都到位才能开始做上面的凭据库与提示条。


### 3.2 只把主密钥交给密钥链，不要逐条凭据入库


```
密钥链条目 1 条(velashell / master-key) ──► 32 字节 AES 主密钥
                                              │
                    SonnetDB 里照旧存 enc1: 密文 ◄┘
```

理由：

- `AesSecretProtector` 与它的全部调用方**一行不改**，只换 `LoadOrCreateKey` 的来源；
- Linux 的 Secret Service 每次读写都是 D-Bus 往返，还可能弹解锁框，
  逐条凭据入库意味着每开一个会话弹一次；主密钥方案是**进程内只读一次**；
- 云同步（`SyncCrypto`）、导入导出、"换机不可解"的既有语义全部不变；
- 撤销点唯一：用户在系统凭据管理器里删掉那一条，全部密文即刻作废。

新增接口大致是 `IMasterKeyStore { bool TryLoad(out byte[] key); void Save(byte[] key); bool IsAvailable { get; } }`，
三平台各一实现 + 一个文件回退实现，装配处在
`src/VelaShell.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs:51`。
迁移：首次启动时若密钥链可用且链上无条目，把现有文件密钥搬上去并留文件副本一个版本周期（防回滚丢数据）。

## 4. 推荐架构（阶段二：sudo 填充）

**凭据库**：一组命名条目 `{标签, 用户名, 密码, 可选作用域(主机/会话/分组)}`，
存 SonnetDB、值过 `ISecretProtector`——与会话密码同一套保护，不另起炉灶。
`SessionProfile` 已有 `Password` / `PrivateKeyPassphrase` / `RememberPassword`
（`src/VelaShell.Core/Models/SessionProfile.cs:38,41,47`），
sudo 密码大多数时候**就是登录密码**，所以凭据库应当能直接引用当前会话的已存密码，而不是逼用户再录一遍。

**交互形态（2026-08-25 与提出者对齐后定稿）**：不自动填，也不弹会打断输入的对话框。
检测到密码提示时，只在终端上浮出一枚**不抢焦点的小提示**；要不要用、用哪一条，全由用户点。

```
  [sudo] password for pi: ▁
  ┌──────────────────────────────┐   ← 光标下一行浮出，不遮当前行
  │ 🔑 选择凭据填入   Ctrl+Shift+K │   点击 / 快捷键 → 展开凭据选择器
  └──────────────────────────────┘
```

1. **提示条**：复用命令补全弹层的定位方式 —— 锚在 `PlacementRect`(光标行)下边缘向下展开
   (`src/VelaShell/Views/TerminalTabView.axaml:168-174`)，底部空间不足时自动翻转到上方。
   **位置天然不冲突**：密码提示行本来就不弹命令补全(`TerminalTabView.axaml.cs:293` 的
   `IsInteractivePromptLine`)，所以此刻光标下方那块地方正好是空的，两个浮层互斥出现。
2. **绝不抢焦点**：`Focusable="False"` 要一路铺到容器、列表与 `ListBoxItem`
   (`TerminalTabView.axaml:191,198`)。这不是洁癖 —— 那里的注释记着一次实测事故：
   焦点落进弹层后，Win+Shift+S 截图覆盖层抢走系统焦点不触发 Deactivated，切回来所有按键
   都进了已被隐藏的弹层窗口，终端表现为"彻底失灵"。提示条是**自动出现**的，因此必须遵守同一条规矩。
3. **点击后才展开选择器**：这一步是用户显式操作，**允许**获得焦点(选择器里要能搜索/输入)，
   关闭时把焦点还给终端 (`_termControl.Focus()`)。列表按作用域排序：当前会话/主机匹配的在前。
4. **手动输入是一等公民**：选择器底部常驻一个"手动输入"入口，用现成的
   `SecurePasswordBox`(`src/VelaShell/Behaviors/SecurePasswordBox.cs`，以 `SecureString` 承载、
   明文不常驻托管字符串)；输入后可选「仅本次」或「存入凭据库」。**这条路必须始终可走** ——
   凭据库为空、密钥链不可用、密码临时改过，都得能就地敲进去。
5. **送出**：`bridge.SendRaw(UTF8(密码 + "\r"))` 一次性入队。发送前再校验一次当前行仍是密码提示行；
   不走剪贴板。
6. **快捷键等价**：`Ctrl+Shift+K`(暂定，已核对不冲突：现有 Ctrl+Shift 组合占用了
   N/C/V/T/F/L/Tab —— `src/VelaShell/Views/MainWindow.axaml:32-39`、
   `KeyboardShortcutService.cs:70`)直接召出选择器，不必摸鼠标；弱网下这条路径最快。
   在非密码提示行按下时也允许召出(用户自己知道要干什么)，只是提示条不会自动冒出来。

弱网下的收益来自**整串一次入队**：现有写队列保证顺序与完整性(`SshTerminalBridge.cs:22` 的注释
解释了为什么不能并发直写)，比人手逐字符敲过去稳得多。所以"一次点击"已经足够，不需要自动。

## 5. 风险与硬约束

1. **提示误判 = 密码进命令行。** `IsSecretPrompt` 是启发式：vim 里打开一个含 `password:` 的
   配置文件、`cat` 一段日志，光标行都可能命中。若自动发送，密码会变成一条 shell 命令 ——
   进屏幕、进 shell history、进会话录制。**缓解**：只在用户显式确认后发送；发送前再校验一次当前行仍是提示行。
2. **不要走剪贴板。** 用剪贴板"粘贴"密码会把它留在系统剪贴板与剪贴板历史（Win+V）里。走 `SendRaw`，不碰剪贴板。
3. **同步输入天然安全，但别改坏。** 见 §1：`SendRaw` 不触发 `TypedInput`。若将来有人把注入改走
   `WriteInput`，密码会被广播到频道内所有终端 —— 这条要写进注释锁住。
4. **录制的例外。** 录的是输出，sudo 不回显所以安全；但 `read`（不带 `-s`）之类会回显的提示，
   密码会进录像。提示条上应当只在"无回显"状态下才建议填充（终端已有回显抑制/`EchoSuppressor` 相关设施可参考）。
5. **macOS 签名变更**会导致密钥链条目重新授权（§2）；发布形态已定为真实 exe + `codesign --deep`，
   换证书那次要预告用户。
6. **失败必须可见。** 密钥链不可用时回退到文件，但设置页要如实写明当前保护级别，不能假装一样安全。

## 6. 顺带评估的替代方案

它们解决的是同一个痛点，可作为文档提示而非替代实现：

- `sudo -A` + `SUDO_ASKPASS`：把取密码交给远端的一个脚本 —— 需要在**每台服务器**上部署，不适合"到处连"的场景；
- `NOPASSWD` sudoers：一劳永逸但降低远端安全等级，属于运维策略而非客户端功能；
- `sudo -S` 从 stdin 喂：要求改写用户的命令（`echo pw | sudo -S ...`），密码会进 shell history，**不推荐**。

结论：客户端侧的"选一条凭据、一次性注入"仍是这三者里最合适的形态。

## 7. 工作量与分期

| 阶段 | 内容 | 粗估 |
|---|---|---|
| 零 | 两个接口 + `FileMasterKeyStore`(今日行为) + `InMemory` + 回退链 + 装配 + 单测 | ~150 行；零行为变化；不需要真机 |
| 一 | 三平台 `IMasterKeyStore` 实现 + 迁移 + 单测 | 每平台 ~100 行 P/Invoke；需真机验证；可分三次交付 |
| 二 A | 凭据库模型/存储/设置页 CRUD + 五语 resx | ~500 行 + 4 个 resx 同步 |
| 二 B | 提示条 + 选择器 + 注入接线 + 误判防护 + 测试 | ~350 行 |

阶段零与阶段一都对用户无感，可独立交付；阶段二 A/B 建议一起发，单独发 A 只是多了个存密码的地方。
值得注意的是：**阶段二不必等阶段一** —— 只要阶段零的抽象在，凭据库落在文件回退上就能先跑起来，
三个平台的实现后补，上层一行不改。

## 8. 待定决策点

1. **凭据库与会话密码的关系**：独立一套，还是"默认复用当前会话已存的密码，另存为可选"？（倾向后者）
2. **每次使用是否要二次确认**（主密码 / Windows Hello / Touch ID）？会显著增加实现面。
3. **Linux 无 keyring 时**：静默回退到文件（倾向），还是在设置页要求用户显式选择？
4. **阶段二是否限定 SSH 会话**，还是本地终端（ConPTY）与插件协议终端一并支持？
