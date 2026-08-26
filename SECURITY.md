# VelaShell Security Policy / 安全策略

**Last updated / 最后更新:2026-08-25**

[English](#english) · [简体中文](#简体中文)

---

## English

VelaShell handles SSH credentials, private keys and live shells on production hosts. Security reports are taken seriously — thank you for looking.

### Supported versions

| Version | Supported |
|---|---|
| Latest GitHub Release | ✅ |
| Anything older | ❌ |

VelaShell is pre-1.0 and under active development. There are **no backports** — fixes ship in the next release. Please reproduce on the latest release (or `dev`) before reporting.

The only official distribution channels are this repository's **GitHub Releases** and the Microsoft Store build. Binaries from anywhere else are not ours and are out of scope.

### Reporting a vulnerability

**Do not open a public issue for a security problem.**

1. **Preferred** — GitHub private vulnerability reporting: repository **Security** tab → *Report a vulnerability*. This keeps the report private until a fix ships.
2. **If that entry point is unavailable** — email **<dygood@outlook.com>** with `[SECURITY]` in the subject line.

Please include:

- Affected version and platform (Windows / Linux / macOS, x64 / arm64)
- Reproduction steps, ideally minimal — a session profile, a terminal escape sequence, a crafted plugin package, a `latest.json`, whatever triggers it
- What an attacker gains: credential disclosure, code execution, file write outside the intended directory, bypass of a permission prompt…
- Any logs or crash dumps **with credentials redacted**

### What to expect

This is a small, largely single-maintainer project. Realistic commitments, not aspirational ones:

| Stage | Target |
|---|---|
| Acknowledgement of your report | within 72 hours |
| Initial assessment (valid / severity / scope) | within 7 days |
| Fix released | depends on severity; critical issues take priority over everything else |
| Coordinated disclosure window | 90 days by default, negotiable |

You will be credited in the release notes and the advisory unless you prefer to stay anonymous. **There is no bug bounty** — no money is on offer, and pretending otherwise would waste your time.

### In scope

- **Credential storage** — the encrypted local store (AES-256-GCM + local key file), the "remember password" path, key material reaching logs, crash dumps or the UI
- **Host key verification** — TOFU recording, fingerprint-change rejection, verification on both SSH and SFTP channels
- **Transport** — SSH, SFTP, FTP/FTPS (including certificate validation and fingerprint pinning), ProxyJump chains
- **Path handling** — SFTP remote paths, ZMODEM / XMODEM / YMODEM receive directories, archive extraction (zip-slip)
- **Terminal engine** — escape sequences that lead to command execution, clipboard exfiltration, or state the user cannot see
- **Plugin system** — Broker permission enforcement, RPC pipe authentication, permission-file tampering, plugin identity spoofing, supply-chain checks on plugin packages
- **Self-update** — `latest.json` handling, SHA-256 verification, the out-of-process swap
- **Anything that lets an unprivileged local process or a remote host read your credentials or run code as you**

### Out of scope (known and accepted)

These are documented limitations, not undiscovered bugs. Reports about them will be closed with a pointer here.

- **A plugin process calling the OS directly** to touch the local filesystem or the network, bypassing the Broker. The plugin trust model states this plainly: v1 guarantees *"a plugin cannot take your credentials, cannot touch your servers without authorization, and cannot crash the host"* — it does **not** guarantee "a malicious plugin cannot read your local disk". OS-level sandboxing is a v2 track. See [`docs/plugins/12-security-threat-model.md`](docs/plugins/12-security-threat-model.md) §1 and §3 (T13).
- **Network egress from plugins** — declaration-based with SDK auditing in v1, no physical interception (T08, documented residual risk).
- **macOS builds are unsigned and un-notarized.** This is stated in the release pipeline and is a known cost of the current distribution setup.
- **Attacks requiring an already-compromised OS user account** — a process running as you can debug your processes; that is the platform's boundary, not ours (T12).
- **The behaviour of third-party plugins** installed from the plugin marketplace. Plugin safety rests on a source-trust model; report malicious plugins to us, but the plugin's own code is its author's responsibility.
- **Vulnerabilities in third-party dependencies** with no VelaShell-specific exploit path — Dependabot already tracks NuGet and Actions daily. If you have found a chain that is exploitable *because of how VelaShell uses* the dependency, that **is** in scope.
- Missing hardening flags, best-practice deviations and scanner output with no demonstrated impact.

### Safe harbour

Good-faith research within this policy will not be pursued legally, and we will not ask your hosting provider to act against you.

Please stay inside these lines: test only against your own machines and your own servers, do not access, modify or destroy data that is not yours, do not run denial-of-service or spam tests, and do not use social engineering against the maintainers or users.

### Security design references

If you want to understand the intended boundaries before probing them:

- [`docs/plugins/12-security-threat-model.md`](docs/plugins/12-security-threat-model.md) — trust model, threat list T01–T15, mitigation status, sandboxing roadmap
- [`docs/plugins/06-permission-system.md`](docs/plugins/06-permission-system.md) — permission enforcement and the Broker
- [`docs/plugins/05-ipc-protocol.md`](docs/plugins/05-ipc-protocol.md) — RPC channel and identity binding
- [`PRIVACY.md`](PRIVACY.md) — what data the application stores and where it goes

---

## 简体中文

VelaShell 经手的是 SSH 凭据、私钥,以及生产主机上的实时 shell。安全报告我们会认真对待 —— 感谢你花时间来看。

### 支持的版本

| 版本 | 是否支持 |
|---|---|
| GitHub Releases 上的最新版 | ✅ |
| 更早的版本 | ❌ |

VelaShell 尚未到 1.0,处于活跃开发中。**不做向后移植** —— 修复随下一个版本发布。报告前请先在最新发行版(或 `dev` 分支)上复现。

唯一的官方分发渠道是本仓库的 **GitHub Releases** 与 Microsoft Store 版本。其他渠道的二进制不是我们的,不在范围内。

### 如何报告

**安全问题请不要开公开 Issue。**

1. **首选** —— GitHub 私密漏洞报告:仓库 **Security** 页 → *Report a vulnerability*。修复发布前报告始终保持私密。
2. **若该入口不可用** —— 发邮件到 **<dygood@outlook.com>**,标题带 `[SECURITY]`。

请尽量包含:

- 受影响的版本与平台(Windows / Linux / macOS,x64 / arm64)
- 复现步骤,越小越好 —— 一份会话配置、一段终端转义序列、一个构造的插件包、一份 `latest.json`,能触发就行
- 攻击者能拿到什么:凭据泄露、代码执行、越目录写文件、绕过授权弹窗……
- 相关日志或崩溃转储,**请先把凭据打码**

### 你会得到什么

这是一个基本由单人维护的项目。下面写的是能兑现的承诺,不是场面话:

| 阶段 | 目标 |
|---|---|
| 确认收到你的报告 | 72 小时内 |
| 初步评估(是否成立 / 严重度 / 影响面) | 7 天内 |
| 发布修复 | 视严重程度而定;严重问题优先于一切其他工作 |
| 协调披露窗口 | 默认 90 天,可协商 |

除非你希望匿名,否则会在发行说明与安全公告中署名致谢。**本项目没有漏洞赏金** —— 没有钱可给,含糊其辞只会浪费你的时间。

### 在范围内

- **凭据存储** —— 本地加密存储(AES-256-GCM + 本地密钥文件)、「记住密码」链路、密钥材料进入日志/崩溃转储/界面的情况
- **主机密钥校验** —— TOFU 记录、指纹变更拒绝、SSH 与 SFTP 双通道校验
- **传输层** —— SSH、SFTP、FTP/FTPS(含证书校验与指纹固定)、ProxyJump 链式跳转
- **路径处理** —— SFTP 远程路径、ZMODEM / XMODEM / YMODEM 接收目录、压缩包解包(zip-slip)
- **终端引擎** —— 导致命令执行、剪贴板外泄,或制造用户看不见的状态的转义序列
- **插件系统** —— Broker 权限强制、RPC 管道认证、权限文件篡改、插件身份冒充、插件包供应链校验
- **自更新链路** —— `latest.json` 处理、SHA-256 校验、外置换版进程
- **任何能让本机低权限进程或远程主机读到你的凭据、或以你的身份执行代码的问题**

### 不在范围内(已知并接受的限制)

以下是**已记录在案的设计取舍**,不是尚未发现的 bug。相关报告会被关闭并指向这里。

- **插件进程直接调用系统 API** 访问本地文件或网络、绕过 Broker。插件信任模型对此有明确陈述:v1 的承诺是「**插件拿不走你的凭据、碰不了你的服务器(除非授权)、搞不垮你的主程序**」,**不包含**「恶意插件不能读你本地磁盘」。OS 级沙箱是 v2 的路线。见 [`docs/plugins/12-security-threat-model.md`](docs/plugins/12-security-threat-model.md) §1 与 §3(T13)。
- **插件的网络出口** —— v1 是声明制 + SDK 审计,没有物理拦截(T08,已如实标注为残余风险)。
- **macOS 产物未签名、未公证。** 发布流水线里已写明,这是当前分发方案的已知代价。
- **需要攻击者已经控制同一 OS 用户账户**的场景 —— 以你的身份运行的进程可以调试你的进程,这是平台边界而非本项目的边界(T12)。
- **插件商店里第三方插件自身的行为。** 插件安全建立在来源信任模型上;发现恶意插件请告诉我们,但插件自身的代码由其作者负责。
- **第三方依赖的漏洞**,且没有 VelaShell 特有的利用路径 —— Dependabot 已配置为每日检查 NuGet 与 GitHub Actions。若你找到的利用链是**因为 VelaShell 使用该依赖的方式**才成立,那**属于**范围内。
- 缺少加固开关、偏离最佳实践,以及扫描器输出中没有实际影响证明的条目。

### 免责保护

在本策略范围内的善意安全研究,我们不会追究法律责任,也不会要求你的服务提供商对你采取行动。

请守住这条线:只针对你自己的机器和你自己的服务器测试;不访问、修改或破坏不属于你的数据;不做拒绝服务或轰炸测试;不对维护者或用户实施社会工程。

### 安全设计参考

想在动手之前先了解设计上的边界在哪:

- [`docs/plugins/12-security-threat-model.md`](docs/plugins/12-security-threat-model.md) —— 信任模型、威胁清单 T01–T15、缓解状态、沙箱路线
- [`docs/plugins/06-permission-system.md`](docs/plugins/06-permission-system.md) —— 权限强制与 Broker
- [`docs/plugins/05-ipc-protocol.md`](docs/plugins/05-ipc-protocol.md) —— RPC 通道与身份绑定
- [`PRIVACY.md`](PRIVACY.md) —— 应用存了哪些数据、去了哪里
