# 为 VelaShell 贡献代码

感谢你愿意花时间改进 VelaShell。这份文档记录的是这个仓库**实际在用**的约定 —— 照着做,你的 PR 基本不会在流程上卡住。

**简体中文** · [English](CONTRIBUTING.en.md)

---

## 开始之前

| 你想做的事 | 先读 |
|---|---|
| 改任何代码 | [`docs/architecture.md`](docs/architecture.md) —— 分层基线与**依赖方向**,这是本仓最硬的约束 |
| 改界面 / 交互 | [`docs/交互与界面规格.md`](docs/交互与界面规格.md) |
| 改终端引擎 | `docs/architecture.md` 的 Terminal Design Notes、[`docs/终端输入乱序问题分析与架构建议.md`](docs/终端输入乱序问题分析与架构建议.md) |
| 改插件系统 | [`docs/plugins/`](docs/plugins/) 全 15 篇,尤其 [12-security-threat-model.md](docs/plugins/12-security-threat-model.md) |
| 写第三方插件 | 不用改本仓库 —— 见[插件开发指南](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md) |

**动手前先开 Issue。** 尤其是涉及架构、新增依赖、改动终端引擎或插件契约的改动 —— 这些地方的取舍往往有历史原因(很多都写在代码注释里),先聊一句能省下双方大量返工。小修小补(拼写、明显 bug、文档)可以直接提 PR。

---

## 环境准备

| 需要 | 说明 |
|---|---|
| **.NET SDK** | 版本钉在 [`global.json`](global.json),**目前是 preview 版**(`allowPrerelease: true`)。装的 SDK 低于它会直接拒绝还原 |
| **IDE** | Visual Studio 2026 / Rider / VS Code + C# Dev Kit 均可;仓库带 `.editorconfig` 与 `VelaShell.sln.DotSettings` |
| **Docker**(可选) | 只有跑真实 SSH 集成测试时才需要 |

### 强名称密钥:本地用 Debug,别用 Release

`src/VelaShell.snk` **不在仓库里**(`.gitignore` 排除了 `*.snk`),由发布流水线从 secret 注入。签名只在 `Configuration=Release` 时开启,所以:

- ✅ `dotnet build`(默认 Debug)—— 正常
- ❌ `dotnet build -c Release` —— 本地必然失败,找不到密钥文件

还有一层连带影响:`InternalsVisibleTo("VelaShell.Tests")` 只在**非签名构建**里声明(签名程序集的友元声明需要公钥)。也就是说**测试只能跑在 Debug 上**,这不是可选项。

---

## 构建、运行、测试

```bash
# 构建整个解决方案
dotnet build VelaShell.slnx

# 跑起来
dotnet run --project src/VelaShell/VelaShell.csproj

# 全量测试(七个测试项目)
dotnet test VelaShell.slnx

# 只跑某一个项目
dotnet test tests/VelaShell.Tests/
```

`tests/velashell.runsettings` 由 `tests/Directory.Build.targets` 自动应用,**命令行不需要显式带 `--settings`**。它给每个测试设了 60 秒上限,理由见下一节。

### 真实 SSH 集成测试

```bash
docker compose -f docker-compose.test.yml up -d   # 起一个 localhost:2222 的 openssh-server
dotnet test tests/VelaShell.Tests/
```

不起容器时这些用例会自行跳过,不会红。

### headless UI 测试的两条硬约束

`VelaShell.Tests` 里所有 UI 测试**共用同一条** headless UI 线程,工作项顺序执行。因此:

1. **测试体必须同步返回** —— 写 `return Task.CompletedTask;`,**不要写 `async`**。写成 `async` 会绑错重载,导致断言一条都不执行**却全绿**,比失败更危险。
2. **结束前必须关窗。** 有一个工作项没返回,这条线程就被永久占住,其后每个测试的 `Dispatch` 无限期排队 —— 症状是「单独跑秒过,一起跑永远卡住」,而且卡在谁身上只取决于执行顺序。

60 秒超时就是为这个准备的:它把「整个 run 挂死」降级成「那一条失败」,顺带把堵点指出来。真卡住了用 `--blame-hang-timeout` 排查。

**另外:不要在测试里新建 `App` 实例。** 历史上这么干过一次,整个套件死锁。

---

## 分支与提交

### 分支

`dev` 是集成分支,`main` 是发布分支。**请从 `dev` 拉分支,PR 也提到 `dev`。**

分支命名沿用仓库现有习惯 —— `<类型>/<issue 号>-<简短描述>`:

```
feat/227-terminal-padding
fix/226-file-browser-manual-path
```

### 提交信息

本仓库**不用 Conventional Commits**。现有风格是一行中文摘要,说清「这个提交做了什么」:

```
修复自绘标题栏拖动导致的光标异常
重构快捷键参考,统一由 ShortcutCatalog 生成
支持终端控件多段不连续选区功能
```

要点:动词开头、说结果而非过程、一行说完。需要展开的背景放正文,或者更好 —— 放进代码注释里(见下)。

---

## Pull Request

> **本仓库目前没有 PR 门禁 CI。** `.github/workflows/` 下只有发布流水线,它在 Release 发布时才触发。这意味着**没有任何自动检查会替你兜底** —— 合并前的正确性完全依赖你本地跑过。

提 PR 前请自己确认:

- [ ] `dotnet build VelaShell.slnx` 零警告零错误(仓库当前是干净的,别把警告带进来)
- [ ] `dotnet test VelaShell.slnx` 全绿
- [ ] 新增/修改的行为有对应测试
- [ ] 界面文案走了本地化(见下节),没有硬编码字符串
- [ ] 涉及的文档已同步(见下节)

PR 描述里写清**为什么**这么改。「改了什么」看 diff 就知道,「为什么」只有你知道。

---

## 代码约定

### 格式

`.editorconfig` 是唯一权威,IDE 会自动应用。几条容易踩的:

- 行尾 **LF**,编码 **UTF-8**,文件末尾留空行
- C# 缩进 4 空格;**XAML / JSON / XML 缩进 2 空格**
- `Nullable` 全仓 enable,`ImplicitUsings` enable,`LangVersion` 为 `preview`(可以用最新语法,比如 `field` 关键字)

### 文档注释是编译期强制的

`GenerateDocumentationFile=true` 全仓开启 —— **公开成员缺 XML 文档注释会出警告**,而我们要求零警告。用中文写,和现有代码保持一致:

```csharp
/// <summary>把一次按键(键 + 修饰键 + 终端状态)分类成应执行的动作。</summary>
/// <param name="key">按下的键。</param>
```

### 注释写「为什么」,不写「是什么」

这是本仓库最鲜明的风格,也是最希望你延续的一条。代码在说「做了什么」,注释要说**当初为什么这么选、不这么写会怎样**。看看现有代码:

```csharp
// IME 组字消耗的按键(挑选中文候选等)绝不能编码:会把散逸的 ESC/方向键/Enter
// 发往 PTY(历史事故:htop 的 F3 搜索里输入中文会杀死 htop)。
```

尤其是**看起来可以简化、实际不能动**的地方,请留一句话说明,否则下一个人(可能就是三个月后的你)会把它「优化」掉。

### 分层依赖方向不可逆

```
App(VelaShell) → Presentation / Controls / Infrastructure / Terminal / Core
Presentation   → Core, Terminal
Infrastructure → Core, Terminal(仅当适配器确实属于这里)
Terminal       → Core
Controls       → Core(可选,仅共享 UI 契约)
```

`Core` 不依赖任何人。往回引用(比如 `Core` 引 `Infrastructure`)的 PR 不会被合并 —— 需要反向数据流时用接口 + 依赖注入。所有 DI 注册集中在 `src/VelaShell/App.axaml.cs` 这一个组合根,各层经自己的 `*ServiceCollectionExtensions` 贡献注册。

---

## 多语言:五份 resx 必须同时改

界面支持简体中文 / English / 繁體中文 / 日本語 / 한국어。资源文件在 `src/VelaShell.Core/Resources/`:

```
Strings.resx          ← 英文,中性资源(基准)
Strings.zh-Hans.resx
Strings.zh-Hant.resx
Strings.ja.resx
Strings.ko.resx
```

**新增一个键就要补五份**,少一份测试就红。三条测试盯着这件事:

| 测试 | 拦什么 |
|---|---|
| `AllCultures_HaveIdenticalKeySets` | 五语言键集必须完全一致(漏译 / 孤儿键双向都算失败) |
| `LocalizedKeyUsageTests` | 代码/XAML 里引用的键必须真实存在 —— 否则界面在**所有语言下**显示英文键名 |
| `UnusedLocalizedKeyTests` | 资源里不许留没人引用的死键 |

界面上任何用户可见的字符串都必须走资源:XAML 用 `{loc:Localize SomeKey}`,代码用 `Strings.Get("SomeKey")`。**这两种写法是被正则扫描的**,用变量传键名扫不到,也就得不到保护。

---

## 文档

`docs/`(中文)与 `docs-en/`(英文)是**互为镜像**的两套。改了一份就要同步另一份,文件名一一对应:

```
docs/交互与界面规格.md  ↔  docs-en/interaction-and-ui-specs.md
docs/architecture.md    ↔  docs-en/architecture.md
```

README 同理:`README.md` ↔ `README.en.md`。

---

## 新增快捷键?先改目录

快捷键有唯一事实来源:[`src/VelaShell/ViewModels/ShortcutCatalog.cs`](src/VelaShell/ViewModels/ShortcutCatalog.cs)。设置 → 快捷键页和 [`docs/快捷键参考.md`](docs/快捷键参考.md) 都从它取数。

加了新键位却没登记,`ShortcutCatalogTests` 会直接失败并**打印出可粘贴的 Markdown 行**。完整规则见 [`docs/快捷键参考.md`](docs/快捷键参考.md) 的「维护约定」。

---

## 这些别改

| 别动 | 原因 |
|---|---|
| `Directory.Build.props` 里的 `Version` | 发版时由 Release 标签经 `-p:Version` 覆盖,手改会和流水线打架 |
| 任何 `*.snk` | 密钥不入库,由 CI 注入 |
| `.github/workflows/release.yml` 的产物布局 | macOS 的 `tar.gz` / `dmg` 分工和 `latest.json` 强耦合,改错会让自更新静默失效 |

依赖升级交给 Dependabot(已配置为每日检查 NuGet 与 GitHub Actions),不用手动提 bump PR。

---

## 安全问题请勿走 Issue

发现安全漏洞不要开公开 Issue,按 [`SECURITY.md`](SECURITY.md) 的流程私下报告。

---

## 许可与贡献者授权

本项目双许可:[AGPL-3.0](LICENSE) / [商业授权](LICENSE-COMMERCIAL.md)。

**提交贡献即表示你同意:**

1. 你的贡献以 **AGPL-3.0** 授权;
2. 你授予项目版权方在**商业许可下再许可(sublicense)**该贡献的权利。

这是一份轻量 CLA —— 没有它,双许可模式没法覆盖社区贡献。详见 [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3。

另请确认:你提交的代码是你自己写的,或你有权以上述条款贡献它。**请勿粘贴来源不明、或与 AGPL-3.0 不兼容的第三方代码。**

---

有任何流程上的疑问,直接开 Issue 问 —— 问题本身往往说明这份文档还不够清楚,我们会一并改。
