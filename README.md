# VelaShell

> 一款为运维与开发者打造的现代化跨平台 SSH 终端客户端。

**简体中文** · [English](README.en.md)

VelaShell 是一个使用 .NET 11 与 Avalonia 构建的桌面终端应用，支持 Windows、Linux 与 macOS。它内置自研 VT 终端引擎、SSH/SFTP/FTP 连接、本地终端标签、跳板机（ProxyJump）与网络代理（HTTP / SOCKS5 / 跟随系统）、两步身份验证与主机指纹校验、端口转发隧道、分组会话管理、自研 VelaDock 可拖拽分屏、资源监视与路由追踪、命令面板与十二页设置中心；还能按 Xshell 的调用约定被堡垒机 / SSO 门户外部拉起。另带一套**双模插件系统**（进程内 / 独立进程）与第一方 **AI 助手插件**。全部数据经嵌入式 SonnetDB 加密持久化。旨在为高频远程操作提供**键盘优先、信息密度高、响应迅速**的使用体验。

---

## 🪶 关于「VelaShell」

**读音**：`/ˈveɪlə ʃɛl/` — 读作 **「VAY-la shell」**，中文近似「薇拉·谢尔」。第一个音节 *Vay* 重读。

**含义**：由 **Vela** + **Shell** 两部分组成。

- **Vela（船帆座）** — 拉丁语意为「帆」。船帆座是南天的一个星座，与龙骨座（Carina）、船尾座（Puppis）同源，共同拆分自古希腊神话中「阿尔戈号」（Argo Navis）——伊阿宋与阿尔戈英雄们远航寻找金羊毛所乘的巨船。取其**扬帆远航、驶向未知彼岸**之意。
- **Shell（终端外壳）** — 命令行 shell，也是本软件的核心：一个连接远程主机的终端。

合起来，VelaShell 寓意 **「以终端为帆，乘信号之风驶向远方主机」** —— 一个为远程操作扬帆的 SSH shell。图标即这一理念的浓缩：青绿渐变的圆角方块上，一枚深色 `>_` 命令提示符。

### 速览

| 项目 | 说明 |
|------|------|
| **名称** | VelaShell |
| **读音** | `/ˈveɪlə ʃɛl/`（VAY-la shell·薇拉·谢尔） |
| **类别** | 跨平台 SSH / SFTP / FTP 终端客户端 |
| **当前版本** | `v0.0.1-dev`（活跃开发中，版本号单一来源见 `Directory.Build.props`；发版时由 Release 标签经 `-p:Version` 覆盖） |
| **平台** | Windows 10 / 11 · Linux · macOS（x64 / arm64） |
| **运行时** | .NET 11 + Avalonia 12.1，Self-contained 发布（免装 Runtime） |
| **界面语言** | 简体中文 / English / 繁體中文 / 日本語 / 한국어（五语键集完全一致，由 `LocalizedKeyUsageTests` / `UnusedLocalizedKeyTests` 守住，漏译与孤儿键都会让测试变红） |
| **许可证** | 双许可：[AGPL-3.0](LICENSE) / [商业授权](LICENSE-COMMERCIAL.md) · © 2026 VelaShell 作者及贡献者 |

---

## ✨ 主要特性

### 终端与连接

- **自研 VT 终端引擎**  
  完整实现 DEC ANSI / VT / Xterm 状态机，支持 256 色、真彩色、DEC 线绘字符、主/备屏、滚动区、应用光标键、鼠标协议、CJK 双宽字符与动态编码切换。内置十种终端 profile（vt52/100/102/220/320/340/420/520/xterm/xterm-256color），默认 xterm-256color。终端为自绘 Avalonia 控件，字形、选区与滚动全部自行渲染；选区支持行/块两种模式、`Shift+左键`扩展，以及 `Ctrl+Shift+拖拽`追加的**多段不连续选区**（可一次复制第 1 行 + 第 3 行）。

- **SSH、SFTP 与本地终端**  
  基于 [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh)（全托管、async-first 的 .NET SSH 库）实现 Shell、SFTP 文件传输与端口转发。支持密码与私钥认证，缺少凭据时自动进入两步身份验证流程（用户名 → 认证方式），认证失败可原地重试。另有**本地终端标签**（pwsh / PowerShell / CMD / WSL / Git Bash 自动探测）—— 经 ConPTY 实现，**目前仅 Windows** 可用。

- **跳板机（ProxyJump）**  
  一条会话可引用另一条已保存配置作跳板，支持链式多段跳转（≤5 跳、带环检测）；由 Tmds.Ssh 原生 `SshProxy` 逐跳建链，指纹按各跳逻辑主机分别校验。

- **网络代理**  
  全局代理设置（设置 → 代理）：直连 / 跟随系统 / HTTP CONNECT / SOCKS5，支持代理认证，SOCKS5 可选由代理侧解析 DNS（不泄露目标域名）。作用于**全应用出站流量** —— SSH、FTP，以及云同步与更新检查等 HTTP 请求。

- **Xshell 兼容登录（外部拉起）**  
  可按 Xshell（及 SecureCRT / PuTTY）的调用约定被第三方安全客户端拉起：堡垒机 / SSO 门户网页上点「用终端打开」，一次性口令直接交给 VelaShell 完成登录，用户全程不接触密码。含 URL 协议注册与单实例转发；威胁模型与凭据处置见 [`velashell-docs zh/host/Xshell兼容登录.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/Xshell兼容登录.md)。

- **ZMODEM（rz / sz）/ XMODEM（rx / sx）/ YMODEM（rb / sb）**  
  终端内直接收发文件，三种协议均为自研引擎、收发双向、传输无关（SSH / 本地 ConPTY 通用）。ZMODEM **自动接管**：从输出流中识别引导序列后接管通道，结束自动复位回终端；XMODEM / YMODEM 在链路上没有可识别的引导序列，只能从**命令面板**（Ctrl+P → 「文件传输」）手动发起——先在远端敲好 `sb`/`rb`，再点对应命令。YMODEM 支持批量与 YMODEM-G 流式变体。排障可置 `VELASHELL_TRANSFER_TRACE=1`（旧名 `VELASHELL_ZMODEM_TRACE=1` 仍可用）打开协议帧跟踪。

- **FTP / FTPS**  
  连接配置可选 FTP 类型：支持显式 / 隐式 FTPS 与明文 FTP、匿名登录、被动/主动模式；服务器证书未通过校验时给出 SHA-256 指纹交由用户确认，信任后按指纹固定。基于 [FluentFTP](https://github.com/robinrodricks/FluentFTP)（MIT），自带连接池以支持并发传输（FTP 一条控制连接同时只能跑一条命令），并复用与 SFTP 完全相同的双栏文件面板与传输栈。设计与取舍见 [`velashell-docs zh/host/FTP客户端可行性调研.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/FTP客户端可行性调研.md)。

- **独立 SFTP 标签与远程文件编辑**  
  连接配置可选 SSH 或 SFTP 类型；SFTP 标签在停靠工作区内以独立文档呈现，支持本地/远程双栏浏览与拖拽互传、断点续传与传输队列。远程文件可在内置编辑器中打开（AvaloniaEdit，按扩展名自动语法高亮，另自建 Shell/YAML/INI/Log/Dockerfile 五种运维常用定义并统一换肤），保存即回传；也可交给外部编辑器并监听落盘回传。

- **主机密钥信任**  
  首次连接默认 TOFU 自动记录指纹，可切换为人工确认（永久信任 / 仅本次信任 / 取消）；指纹变化立即拒绝连接，防御中间人攻击；SSH 与 SFTP 通道均校验；设置中可查看与删除已信任主机（支持截图防泄露的地址脱敏）。

- **端口转发隧道**  
  本地转发（`-L`）、远程转发（`-R`）与动态 SOCKS5 转发（`-D`）统一管理。

### 工作区与运维工具

- **自研 VelaDock 可拖拽分屏**  
  完全自研、零第三方依赖的停靠框架（已替换 Dock.Avalonia）：标签页、边缘五区分屏、跨组并入与标签重排，支持多终端并行操作。

- **会话管理与导入**  
  资源管理器按分组维护连接配置（新建/编辑/删除/双击直连）；侧边栏「最近连接」展示 名称-分组 与相对时间，重启不丢失，双击即可重连；支持从 **WinSCP** 与 **Xshell** 导入既有会话。

- **资源监视器**  
  远端主机的 CPU（总览/逐核/时间分布/主频/上下文切换）、内存（含 cache/buffers/swap）、磁盘（设备、挂载点、文件系统、容量）、网络连接与进程列表，图表实时刷新。

- **进程管理器 / 路由追踪 / 连接诊断**  
  远端进程查看与终止；`traceroute` 可视化（含地理信息，设计见 [`velashell-docs zh/host/路由追踪设计.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/路由追踪设计.md)）；连接失败时的分步诊断。

- **快捷命令与命令面板**  
  常用命令片段一键下发到当前会话；`Ctrl+P` / `Ctrl+K` 呼出命令面板，支持模糊子序列搜索、最近会话、全部已保存会话与全局命令快速跳转。

- **会话录制与回放**  
  开启后自动记录终端输出（SonnetDB 时序存储，随日志保留天数自动清理）；回放中心支持按时间轴回放、拖动定位、1x/2x/…/16x 倍速、跳过空闲片段，并可导出为 asciinema 兼容的 asciicast v2（`.cast`）格式。

- **终端行号 / 时间侧栏**  
  可为终端输出附加行号与时间戳侧栏，两者独立开关、支持快捷键切换，并带折叠标记与空白间隔。

### 插件系统

- **双模插件宿主**  
  插件既可**进程内**装载（可收集 ALC 隔离，UI 直接并入停靠工作区），也可跑在**独立进程** `VelaShell.PluginHost` 里（自研命名管道 RPC，崩溃不波及主程序，带心跳、自愈重启与空闲回收）。装载方式由插件清单声明，两种模式共用同一套 SDK 契约。

- **能力面（Capability APIs）**  
  插件经 `IPluginContext` 访问宿主能力：`Sessions`（会话枚举/状态）、`Terminal`（读输出/写输入）、`RemoteFs`（远端文件读写与目录列举）、`RemoteExec`（远端命令执行）、`Storage` 与 `TimeSeries`（插件私有的文档与时序存储）、`Secrets`（经宿主加密的机密）、`Commands`（注册命令与快捷入口）、`Events`（会话/语言/主题事件）、`Ui`（面板：停靠文档或独立窗口）、`Clipboard`、`Log`。危险能力经权限对话框逐项授权。

- **打包与管理**  
  插件以 `.vpx` 包分发，独立的插件管理窗口可安装/启停/卸载；卸载时其私有数据（SonnetDB 命名空间与数据目录）一并清理。SDK 另提供测试替身（`VelaShell.PluginSdk.Testing`），插件可在 headless 下自测。第三方开发者一条命令即可断点调试（`vela-plugin dev init` → F5），详见 [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)、[命令行手册](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/cli/cli.md)、[SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md)与[打包发布](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/publishing.md)；插件商店：<https://market.easilynet.top>。完整蓝图见 [`velashell-docs zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins)（15 篇设计 + [进度总览](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md)）。

- **AI 助手插件（第一方）**  
  多提供商流式对话：OpenAI Responses / OpenAI Chat Completions 兼容 / Anthropic Messages 三种线协议，覆盖 OpenAI、Anthropic、Grok、Gemini、DeepSeek、Kimi、GLM 与各类中转站。接入走内置**供应商目录**：**点一下就连上** —— 支持登录的直接开浏览器、授权完自己跳回来，凭据加密存好、模型一并配好，全程零输入；其余的只问一把 API Key，名称/地址/模型/协议都有出厂值并收进「高级设置」。**授权码 + PKCE**（环回端口接回调）与**设备码**（浏览器不在本机时）两套标准流程都实现了，令牌临近过期自动续期。已经在付 **ChatGPT Plus / Claude Pro / GitHub Copilot** 的用户可以直接用手里的订阅额度——这几条借的是各家官方 CLI 公开的客户端身份，界面上标了「实验性」并给出说明。模型 id、上下文窗口与单价来自开源的 **models.dev**。**Agent 模式**基于 Microsoft.Extensions.AI 的 `FunctionInvokingChatClient` 工具循环，工具桥接到 sessions / terminal / remoteExec / remoteFs，危险操作面板内逐条审批；可挂接自定义 **MCP 服务器**（stdio / HTTP）扩展工具集。另带**网页检索与抓取**工具（默认走公共 SearXNG 实例，可换成自建），让模型在回答前先查资料。Agent 跑动过程中还能**插话**：新消息进队列即时排上，不必等当前回合结束。对话落插件私有时序库，历史可翻回、可续聊、可删除；输入框 ↑↓ 调历史，`@` 唤出所选会话的远端文件选择器，发送时把文件内容随消息附给模型。输入框本身是带 **Markdown 着色**的编辑器，`@` 引用显示为主题色短名芯片（悬停给全路径），消息气泡按 Markdown 渲染。

### 数据、外观与更新

- **嵌入式 SonnetDB 存储**  
  所有持久化（连接配置、分组、设置、known_hosts、命令片段、连接历史、审计日志、会话录制、插件数据）统一存入本地嵌入式 [SonnetDB](https://github.com/IoTSharp/SonnetDB) 多模型数据库：业务数据用文档集合，最近连接、审计与录制数据块等时间序列数据用时序引擎。连接密码与私钥口令以 **AES-256-GCM** 加密落盘。

- **GitHub Gist 云同步**  
  应用设置、连接配置（含分组与端口转发隧道）与代码片段同步到你自己账号下的私密 Gist，多设备无缝漫游；每次同步即一个可回溯的历史版本，支持任意版本恢复；可选口令端到端加密（PBKDF2 + AES-256-GCM），未启用加密时凭据绝不上传。

- **设置中心**  
  十二个设置页面：常规、外观、终端、代理、密钥管理、快捷键、文件传输、安全审计、代码片段、云同步、关于、支持与捐赠。密钥管理可直接枚举 `~/.ssh` 密钥（类型 + SHA256 指纹）、生成 RSA 密钥对、导入与复制公钥。快捷键页由 `ShortcutCatalog` 单一来源生成，与 [`velashell-docs zh/host/快捷键参考.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md) 同源。

- **深色 / 浅色 / 系统主题**  
  设计 Token 化，无硬编码颜色，支持运行时切换；终端配色未自定义时随主题联动（暗=Dracula / 亮=Solarized Light）。滚动条为 Windows 11 风格两态实现（静止细条，悬停出滑道与箭头）。

- **内置终端字体**  
  随包内置 Cascadia Mono 四款字形（常规 / 粗体 / 斜体 / 粗斜体）作为终端默认字体，三平台字形一致；CJK 走系统回退。

- **实时状态栏**  
  连接状态、延迟、运行时长、终端尺寸、编码、CPU / 内存 / 网速一目了然。

- **桌面集成**  
  单实例（第二次启动会唤起已有窗口）、最小化到托盘、开机自启、硬件加速开关（关闭可省下约 170MB 常驻内存）。

---

## 🖥️ 平台支持

| 平台 | 架构 | 状态 |
|------|------|------|
| Windows 10 / 11 | x64 / arm64 | ✅ 完整支持（便携 zip，应用内自动更新；另有 Microsoft Store MSIX） |
| Linux | x64 / arm64 | ✅ 完整支持（便携 tar.gz） |
| macOS | x64 / arm64 | ✅ 完整支持（tar.gz + `.dmg` 拖装包，未签名/未公证） |

发布方式为 **Self-contained**，目标机器无需预装 .NET Runtime。跨平台发布由 [`scripts/publish-all.ps1`](scripts/publish-all.ps1) 一键产出，详见[发布](#-构建与发布)。

---

## 🚀 快速开始

### 环境要求

- [.NET SDK](https://dotnet.microsoft.com/download) **11.0.0 或更高版本**（`global.json` 锁定，`rollForward: latestFeature`；当前以 `11.0.100-preview.x` 构建）
-（可选）Docker，用于启动本地 SSH 测试服务器

> ⚠️ 仓库已切到 **net11.0**，并开启了 `EnablePreviewFeatures` 与 `runtime-async=on`（见 `Directory.Build.props`）。这意味着构建依赖 .NET 11 预览版 SDK；若你需要 LTS 基线，把 `Directory.Build.props` 的 `<TargetFramework>` 与 `global.json` 一并回退到 net10 即可。

### 克隆与构建

```bash
git clone https://github.com/joesdu/VelaShell
cd VelaShell

# 构建整个解决方案（含插件宿主）
dotnet build

# 或直接构建桌面应用入口项目
dotnet build src/VelaShell/VelaShell.csproj
```

> 干净克隆构建出来只带本仓库自建的 **AI 插件**。想在本机连同 Redis / S3 / Telnet 一起跑，把它们的插件目录铺进 `artifacts/plugins/`（或用 `-p:VelaPluginsStageDir=<目录>` 指别处）—— 构建时会自动镜像到应用输出目录的 `plugins/<插件目录名>/`，F5 即可加载。`dotnet publish` 仅在自建插件与暂存目录**两边都空**时才失败，发行包不接受「插件系统看着在、实则没插件」。
>
> 自 2026-08-22 起**发布流水线不再预装** Redis / S3 / Telnet（不是每个人都要连 Redis、开 S3、拨 Telnet），改由用户按需从[插件商店](https://market.easilynet.top)自行安装。暂存目录纯属本机行为：铺过之后你本机 `dotnet publish` 出来的包会把它们一并打进去，只影响你自己的产物。
>
> **应用正在运行时构建会因文件占用失败**，先关掉应用。

### 运行

```bash
# 开发模式（热重载）
dotnet watch run --project src/VelaShell/VelaShell.csproj

# 发布为 Windows 独立可执行文件
dotnet publish src/VelaShell/VelaShell.csproj -c Release -r win-x64 --self-contained true
```

### 启动测试 SSH 服务器

```bash
docker compose -f docker-compose.test.yml up -d
# 用户名：testuser，密码：testpass
# 端口：2222
```

### 数据与配置位置

| 内容 | 位置 |
|------|------|
| SonnetDB 数据目录（连接/分组/设置/known_hosts/连接历史/审计/录制/插件数据） | `~/.velashell/sonnetdb` |
| 凭据加密密钥（AES-256） | `~/.velashell/secret.key` |
| 用户手动安装的插件（`.vpx`） | `~/.velashell/plugins`（第一方插件仍位于程序目录的 `plugins/`） |
| 宿主自登记（供 `vela-plugin` 定位安装与核对版本） | `~/.velashell/host.json` |
| 插件开发期挂载与影子副本 | `~/.velashell/plugins.dev.txt`、`~/.velashell/dev-shadow/` |
| SSH 密钥对（密钥管理页） | `~/.ssh` |

> 旧版本的 `sessions.json` / `settings.json` 等 JSON 配置会在首次运行时自动导入 SonnetDB 并改名为 `*.migrated.bak`。
> 从旧数据根升级时，应用会先把 `%LocalAppData%/VelaShell` 的全部内容校验迁移到 `~/.velashell`，成功后删除旧目录；若与更早版本已放在 `~/.velashell` 的文件冲突，旧目标文件会保存在 `.migration-backup/localappdata/`。

---

## 📦 构建与发布

```bash
# 一键产出全平台发布包（输出到 publish/）
pwsh scripts/publish-all.ps1
```

产物覆盖 Windows x64/arm64（便携 zip）、macOS 与 Linux x64/arm64（tar.gz），全部为含运行时的自包含发布，解压到任意目录即可运行，无需预装 .NET。包内除主程序外还带着隔离插件的宿主进程 `VelaShell.PluginHost`，以及只放着自建 AI 插件的 `plugins/` 目录（Redis / S3 / Telnet 自 2026-08-22 起不再预装，改由用户从插件商店按需安装）。macOS 的 `.dmg` 拖装包只在 CI 的 macOS runner 上生成（`hdiutil`/`iconutil`/`codesign` 是 macOS 独有工具）；**自动更新永远只取 tar.gz**，dmg 仅供人工安装。

> 从 Microsoft Store 安装的版本（MSIX）更新由商店接管，应用内的更新操作会自动隐藏。商店版装在只读的 `WindowsApps` 下，数据目录被系统重定向到包私有位置，因此**与便携版的配置、会话、密钥互不相通**。

**应用内自动更新**：设置 → 关于 → 检查更新。应用从 GitHub Releases 读取 `latest.json` 清单，下载对应平台压缩包到应用目录下的暂存目录，SHA-256 校验后解包，再由应用退出后才动手的外置换版进程完成换版并重启 —— 那时应用目录里没有任何文件被占用，不会留下删不掉的残骸。该「外置进程」就是暂存目录里解包出来的那份新版应用（Release 为自包含**摊开**发布，解开即可运行），因此无需随包分发额外的更新器。应用装在哪里就更新哪里，不限定安装位置；`~/.velashell` 数据目录与更新流程完全隔离，升级/回滚均不触碰用户数据。换版中途失败会自动还原到旧版本，若流程被意外中断卡住，关于页的「修复更新状态」可一键重置。更新通道（stable / preview）在设置页切换。

**CI/CD**：[`.github/workflows/release.yml`](.github/workflows/release.yml) 在 GitHub 发布 Release 时触发，三平台原生 runner 并行构建（版本号取 Release 标签，`-p:Version` 覆盖，发版无需改代码），汇总 `SHA256SUMS.txt` 与自动更新清单 `latest.json` 后全部附加到该 Release；同一条流水线另打 **MSIX** 供 Microsoft Store 提交（刻意不自签，商店认证通过后由微软用商店证书签名）。

> 早期的 WiX MSI 与 Velopack 安装包已于 `241c2a2` 移除：装进 Program Files 会让应用目录不可写，应用内更新只能退化成"提示手动下载"，与便携发布的自更新模型冲突。

---

## 🏗️ 项目结构

```text
VelaShell/
├── src/
│   ├── VelaShell/                  # 桌面应用入口、DI 组合根、XAML 视图、VelaDock 停靠与全局样式
│   ├── VelaShell.Terminal/         # 自研 VT 终端引擎与 Avalonia 渲染控件
│   ├── VelaShell.Presentation/     # 跨层 ViewModel、工作流与 Presentation DI 模块
│   ├── VelaShell.Controls/         # 复用控件库与主题 Token
│   ├── VelaShell.Core/             # 领域模型、服务契约、持久化抽象与本地化（无 UI 依赖）
│   ├── VelaShell.Infrastructure/   # SSH/SFTP/FTP/隧道实现、SonnetDB 持久化、AES-256 凭据加密、
│   │                               # Gist 同步、插件管理与能力实现
│   └── VelaShell.PluginHost/       # 隔离插件的宿主进程（命名管道 RPC，只依赖 SDK 契约）
├── tests/                          # 7 个 MSTest 项目：单元、集成、UI 与冒烟测试
│   └── fixtures/                   # 插件运行时用例的夹具插件（非示例代码，见其 README）
├── docs/                           # 架构设计、UI 规格、设置审计、插件蓝图与交互说明
├── scripts/publish-all.ps1         # 跨平台一键发布脚本
├── docker-compose.test.yml         # 本地 SSH 测试服务器
├── global.json                     # SDK 版本锁定
├── Directory.Build.props           # 全仓版本与公共 MSBuild 属性
├── src/Directory.Packages.props    # 集中式 NuGet 版本管理
└── VelaShell.slnx                  # Visual Studio 解决方案
```

> 每个源项目与测试项目均带有独立 `README.md`，说明该项目的架构、目录职责与依赖关系。入口项目实际名为 `VelaShell`（历史文档中的 `VelaShell.App` 为旧别名）。

### 🧩 三个仓库各管一摊

插件相关的东西已经拆出去了，现在是五个仓库加一个文档仓库分工：

| 仓库 | 管什么 | 怎么交付到本仓库 |
| --- | --- | --- |
| **joesdu/VelaShell**（本仓库） | 主程序 + 宿主侧插件运行时 + 自建的 AI 插件 | — |
| **[VelaShellLabs/velashell-plugin-sdk](https://github.com/VelaShellLabs/velashell-plugin-sdk)** | 插件契约 SDK | `VelaShell.PluginSdk` / `.Testing` **NuGet 包** |
| **[VelaShellLabs/velashell-plugin-cli](https://github.com/VelaShellLabs/velashell-plugin-cli)** | `vela-plugin` 命令行、`VelaShell.PluginSdk.Build` | **NuGet 包**（插件作者用，本仓库不引用） |
| **[VelaShellLabs/velashell-plugin-templates](https://github.com/VelaShellLabs/velashell-plugin-templates)** | `dotnet new velaplugin` 模板 | **NuGet 包**（插件作者用，本仓库不引用） |
| **[VelaShellLabs/velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)** | Redis / S3 / Telnet / Serial 插件 | `velashell-plugins-<版本>.zip` **Release 资产** |
| **[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)** | 上面所有仓库的**全部文档** | — |

> 拆分是分几步走的：2026-08-21 先把 SDK、工具链与插件一起搬到 `velashell-plugin-toolchain`，
> 2026-08-22 插件独立出来，2026-08-27 工具链仓库按发布节奏再拆成 sdk / cli / templates 三个，
> 2026-08-30 所有文档集中到 `velashell-docs`。老文档里「插件在工具链仓库」「文档在各仓库的
> `docs/`」的说法都已作废。

**AI 插件是例外**：它留在本仓库的 [`plugins/VelaShell.Plugin.Ai/`](plugins/VelaShell.Plugin.Ai)，
随主程序一起构建、一起发布 —— 它与宿主耦合最紧，且那些耦合点都是**编译期**的（借宿主的
AvaloniaEdit 作输入框、必须进程内装载、Avalonia 版本必须与宿主逐字一致），理由见
[`plugins/README.md`](plugins/README.md)。

SDK 契约的版本 pin 在 `src/Directory.Packages.props` 与 `tests/Directory.Packages.props`
（都是具体版本号）。插件二进制本仓库不 pin —— 它们不进发行包，也就没有需要锁的版本。

想在本机连同 Redis / S3 / Telnet 一起跑，把它们的插件目录铺进**暂存目录** `artifacts/plugins/`：
可以解 [VelaShellLabs/velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins) 的 Release 资产
`velashell-plugins-<版本>.zip`（包内布局就是安装包 `plugins/` 那一层），也可以直接指向那边的
构建产物。要改指别处就传 `-p:VelaPluginsStageDir=<目录>`。

> ⚠️ 别把 `velashell-ai` 也铺进来 —— 本仓库自己就产出它，同一个 id 出现两份会被 `PluginManager`
> 判重，后来者标 Invalid，表象是「插件莫名其妙用不了」。

改 SDK 契约则先在工具链仓库发一个（预发布）包，再把
`src/Directory.Packages.props`、`tests/Directory.Packages.props` 与
`plugins/VelaShell.Plugin.Ai/VelaShell.Plugin.Ai.csproj` 里的 `VelaShell.PluginSdk`
版本一起抬上去——本仓库一律走 NuGet 包，不做工程引用。

**写插件请直接读[开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)**；
插件系统的架构蓝图在 [velashell-docs 的 `zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins)，那是宿主侧的设计。

---

## 🧩 架构亮点

- **严格分层**：依赖方向为 `App(VelaShell) → Presentation / Controls / Infrastructure → Core`，Core 层不依赖任何 UI 框架，可独立测试与复用。
- **接口优先**：服务均通过接口注入，便于 Mock 与单元测试。
- **单一组合根**：所有依赖注入注册集中在 [`src/VelaShell/App.axaml.cs`](src/VelaShell/App.axaml.cs)，各层通过 `*ServiceCollectionExtensions` 贡献注册。
- **自绘渲染**：终端通过自定义 Avalonia Control 直接渲染字形、选区与滚动，避免依赖已废弃的第三方终端控件。
- **自研停靠**：VelaDock 的模型层（纯 INPC，可单测）与控件层分离，拖拽/分屏/标签重排全套自研，零第三方停靠依赖。
- **插件隔离**：每个进程内插件一个可收集 `AssemblyLoadContext`，依赖按插件自己的 `deps.json` 解析；只有 SDK 契约与 `Avalonia*` 框架程序集回落到宿主，保证跨边界类型同一。需要更强隔离时改跑独立进程，协议为自研命名管道 RPC。
- **设计 Token 化**：颜色、字体、间距全部通过资源字典管理，支持主题与品牌定制。
- **单引擎持久化**：一个嵌入式 SonnetDB 实例承载文档（配置/业务数据）与时序（连接历史/审计/录制/插件数据）两类模型，接口在 Core、实现在 Infrastructure，退出时统一刷盘；旧版 JSON 配置首次运行自动迁移。
- **安全默认值**：凭据静态加密（AES-256-GCM + 本地密钥文件）、主机指纹 TOFU 校验、「记住密码」可按连接关闭、插件危险能力逐项授权。

---

## 🧪 测试

项目包含覆盖核心模型、VT 引擎、ViewModel、插件系统与集成场景的 MSTest 测试套件（7 个测试项目，含真实双进程插件 e2e 与 headless UI 测试）。

```bash
# 运行全部测试
dotnet test

# 仅运行终端引擎测试
dotnet test tests/VelaShell.Terminal.Tests/

# 详细输出
dotnet test --logger "console;verbosity=detailed"
```

| 测试项目 | 说明 |
|----------|------|
| `VelaShell.Core.Tests` | 领域模型、SFTP 与传输队列、隧道、云同步加密、ZMODEM / XMODEM / YMODEM 协议（期望值按 lrzsz 与 ymodem.txt 手工构造的互操作回归） |
| `VelaShell.Terminal.Tests` | VT 解析、终端仿真、编码、字符宽度、侧栏折叠、以及 ZMODEM 自动接管与 XMODEM / YMODEM 手动接管的路由 |
| `VelaShell.Presentation.Tests` | ViewModel 工作流与命令 |
| `VelaShell.Infrastructure.Tests` | SonnetDB 持久化、凭据加密、ConPTY、SSH 密钥管理、插件管理与跨进程 RPC |
| `VelaShell.Controls.Tests` | 自定义控件行为 |
| `VelaShell.Plugin.Ai.Tests` | AI 插件：工具箱审批闸门、能力桥接、设置与机密存取、会话历史、`@` 引用语法与面板 headless 交互 |
| `VelaShell.Tests` | 窗口级视图模型、身份验证流程、插件面板与主题令牌、集成与冒烟测试 |

> 集成测试按环境**早退跳过**：`SshIntegrationTests` 与 `TransferRealChannelIntegrationTests`（`DockerIntegration` 分类）需要 Docker + `docker-compose.test.yml` 里的 SSH 服务器，ZMODEM 那几条还需要容器内能装上 `lrzsz`；`CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`。
>
> ⚠️ **早退跳过在 MSTest 里记为「通过」**。也就是说前提不满足时，这些用例会安静地全绿而一行都没跑 —— 看测试结果分辨不出来。判据本身也要够诚实：探 TCP 端口是不够的，Docker 的端口代理**永远**接受连接，哪怕后端 sshd 根本握不上手，所以夹具改成缓存一次**真实 SSH 握手**再决定跳不跳。要确认它们真的跑过，看 `TestContext` 里有没有 `[SKIP]` 行。
>
> ⚠️ headless UI 测试请用 `Dispatch(async () => { …; return true; })` 这种**带返回值**的重载：`HeadlessUnitTestSession` 没有 `Func<Task>` 重载，写成无返回值会拿到一个从未被等待的 `Task<Task>`，测试体跑到第一个 `await` 就"通过"，断言失败全部丢失。

---

## 📚 文档

全部文档已于 2026-08-30 集中到 **[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)** ——
本仓库、SDK、CLI、模板四个仓库的 `docs/` 与 `docs-en/` 都在那里，互相之间用相对链接，不再跨仓库写绝对 URL。
中文在 [`zh/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh)，English in [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en)。

| 分区 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | **本仓库的文档**：[分层架构](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/architecture.md)、[工程化重构蓝图](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/架构设计.md)、[交互与界面规格](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/交互与界面规格.md)、[快捷键参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md)、[设置项审计](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/settings-audit.md)，以及 SFTP / FTP / Telnet / 串口 / Redis / S3 / 系统密钥链等可行性调研与实施记录 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 15 篇 + [进度总览](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | [插件开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)、[打包与发布](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/publishing.md) |
| [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) | [`vela-plugin` 手册](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/cli/cli.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) | [SDK 参考](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/sdk/sdk-reference.md) |

留在本仓库的两份，因为它们服务的是"在这个仓库里写代码"这件事：

- [`DESIGN.md`](DESIGN.md) — 设计系统：色彩/字体/间距令牌与组件规范（XAML 注释与单元测试按章节号直接引用它）
- [`plan.md`](plan.md) — 进展记录、已知问题与后续待办（开发跟进以此为准）

---

## 🛠️ 技术栈

- **.NET 11** — 目标运行时（`net11.0`，启用预览特性与 `runtime-async`）
- **Avalonia 12.1** — 跨平台 XAML UI 框架
- **ReactiveUI** — 响应式 MVVM
- **VelaDock（自研）** — 可拖拽分屏与停靠布局，零第三方依赖
- **Tmds.Ssh** — SSH / SFTP / 端口转发 / ProxyJump（全托管 async-first 实现）
- **FluentFTP** — FTP / FTPS 客户端
- **ZMODEM / XMODEM / YMODEM（自研）** — 终端内 rz/sz、rx/sx、rb/sb 收发，协议引擎在 `VelaShell.Core/ZModem/` 与 `VelaShell.Core/XYModem/`，共用契约在 `VelaShell.Core/FileTransfer/`
- **AvaloniaEdit** — 远程文件编辑器与 AI 输入框（语法高亮、内联引用芯片）
- **SonnetDB** — 嵌入式多模型数据库（文档 + 时序），唯一持久化引擎
- **插件运行时（自研）** — 可收集 ALC + 独立宿主进程 + 命名管道 RPC + `.vpx` 打包
- **Microsoft.Extensions.AI / ModelContextProtocol** — AI 插件的统一模型抽象、Agent 工具循环与 MCP 客户端
- **LiveMarkdown.Avalonia** — AI 对话的增量 Markdown 渲染（含 Mermaid / LaTeX / SVG 扩展）
- **自研便携式自更新** — GitHub Releases `latest.json` 清单 + SHA-256 校验 + 退出后由外置进程换版重启（失败自动回滚），不限定安装位置、不触碰用户数据目录（`src/VelaShell/Services/Update/`）
- **MSTest** — 单元测试框架
- **集中式包管理** — `Directory.Packages.props` 统一 NuGet 版本

---

## 🚧 开发状态

项目处于活跃开发阶段。

**已可用**：终端引擎、SSH/SFTP、FTP/FTPS、ZMODEM / XMODEM / YMODEM、本地终端、跳板机、会话管理与导入、身份验证、隧道、持久化、设置中心、云同步、会话录制、资源监视/进程管理/路由追踪，以及**插件系统框架层**（双宿主模式、完整能力面、UI 扩展、心跳自愈与空闲回收、插件私有存储与卸载清理、`.vpx` 装卸、SDK 测试替身与开发文档）与第一方 **AI 助手插件**。

**由插件提供**：Telnet、串口（COM / USB 转串口）、Redis、S3 —— 都不随安装包预装，按需从[插件商店](https://market.easilynet.top)安装（源码在 [VelaShellLabs/velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)）。

**未开放**：证书认证；容器管理插件尚未开始；系统密钥链与 sudo 凭据自动填充仍在调研（见 [`velashell-docs zh/host/系统密钥链与sudo凭据填充可行性调研.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/系统密钥链与sudo凭据填充可行性调研.md)）。部分设置项目前仅持久化、待接线到运行时。

完整完成情况与待办清单见 [`plan.md`](plan.md) §10–§12 与 [`velashell-docs zh/plugins/STATUS.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md)。

---

## 🤝 贡献

欢迎提交 Issue 与 Pull Request。**动手前请先读 [`CONTRIBUTING.md`](CONTRIBUTING.md)** —— 里面写清了环境准备（SDK 为 preview 版、本地只能构建 Debug）、分支与提交约定、测试的两条硬约束,以及多语言与文档的同步要求。

架构上的分层约定与依赖方向见 [`velashell-docs zh/host/architecture.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/architecture.md)；写插件请读 [插件开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)。

发现安全漏洞请**不要**开公开 Issue，按 [`SECURITY.md`](SECURITY.md) 的流程私下报告。

---

## 📄 许可证

本项目采用**双许可(Dual License)**模式：

- **[AGPL-3.0](LICENSE)(默认)**：自由使用、修改与分发，但衍生作品（含通过网络提供服务）**必须以相同许可证开放全部源代码**，并保留版权与捐赠信息。移除本项目信息后闭源售卖属于侵权行为，版权方将依法追究（DMCA 下架 / 诉讼）。
- **[商业授权](LICENSE-COMMERCIAL.md)（付费，按需）**：需要闭源集成、闭源分发或企业合规无法接受 AGPL 时，可联系作者购买商业许可（📧 <dygood@outlook.com>，标题注明「Commercial License」）。

**正版声明**：VelaShell 本体对所有个人与企业**永久免费**，唯一官方发布渠道为本仓库的 GitHub Releases；任何渠道的「收费版 VelaShell」均为盗版。「VelaShell」名称与 Logo 不在开源许可授权范围内，衍生版本不得使用本项目名称与标识宣传或售卖。

向本项目提交贡献即表示同意贡献以 AGPL-3.0 授权，并授予版权方在商业许可下再许可该贡献的权利（详见 [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3）。

---

> VelaShell — 为命令行而生。
