# AGENTS.md

> 给 AI 代理与新加入者的操作约定。**动手之前先读完本文件,以及它指向的文档。**

## 一、开工前必读:velashell-docs

VelaShell 生态的**全部文档**集中在一个仓库:
**[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)**。
本仓库**不放** `docs/`、`docs-en/` —— 设计手册、开发规范与开发文档都在那边。

**在动任何代码之前**,先把下表中与你要改的部分相关的几篇读掉。跳过这一步直接改,
结果通常是两种:与既有设计冲突,或者重复实现一个已经存在的能力。

| 位置 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | 宿主分层架构与依赖方向、工程化重构蓝图、交互与界面规格、快捷键参考、设置项审计,以及 SFTP / FTP / Telnet / 串口 / Redis / S3 / 系统密钥链等可行性调研 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 01–15(进程模型、IPC 协议、权限系统、UI 扩展、威胁模型、路线图)与[进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) | 插件契约 SDK 参考、SDK 仓库的发版流程 |
| [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) | `vela-plugin` 命令行手册、CLI 仓库的发版流程 |
| [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | 插件开发指南、打包与发布、模板仓库的发版流程 |

英文镜像在 [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en),与 `zh/` 同构。
[仓库首页](https://github.com/VelaShellLabs/velashell-docs)有按「我想做什么」组织的快速入口表。

## 二、涉及文档的改动一律同步到 velashell-docs

**这是本文件最重要的一条。**

- 本仓库里**不新建** `docs/`、`docs-en/` 或任何成体系的文档目录。要写文档,去 velashell-docs 开 PR。
- 改了代码,而**行为、接口、配置项、命令行、构建流程或版本纪律**与现有文档对不上时,
  必须**同时**在 velashell-docs 提一个 PR 把文档改过来。两个 PR 在正文里互相引用,一起合。
  只改代码不改文档,等于让文档开始骗人 —— 而文档是别人照抄的。
- velashell-docs 的 `zh/` 与 `en/` 是**互为镜像**的两棵树,文件一一对应。改了中文就要改英文,
  反之亦然。漏一边,两棵树就开始漂。
- velashell-docs 内部的互相引用**一律走相对路径**(如 `../templates/dev-guide.md`),
  不要写回 GitHub 绝对 URL —— 文档集中到一个仓库,消掉的正是那种一改路径就断的跨仓库链接。
- **例外**:留在代码仓库里的少数几份文件不适用上述规则,因为它们服务的是「在这个仓库里写代码」
  这件事,搬走只会离使用场景更远。各仓库的例外清单见下面第三节。

## 三、本仓库:VelaShell(宿主主程序)

.NET 11 + Avalonia 12.1 的跨平台终端客户端,以及宿主侧的插件运行时。

### 构建与测试

```bash
dotnet build VelaShell.slnx
dotnet test  VelaShell.slnx
```

### 留在本仓库的文档(不搬去 velashell-docs)

| 文件 | 为什么留在这 |
| --- | --- |
| [`DESIGN.md`](DESIGN.md) | 设计系统的色彩/字体/间距令牌与组件规范。`ButtonThemes.axaml` 的注释与 `DialogButtonStyleTests` **按章节号直接引用它**,搬走等于把代码的参照物挪到另一个仓库 |
| [`plan.md`](plan.md) | 进展记录、已知问题与后续待办,开发跟进以它为准 |
| `README.md` / `README.en.md` / `CONTRIBUTING*.md` / `SECURITY.md` / `PRIVACY.md` | GitHub 仓库门面与流程约定 |
| `src/**/README.md`、`tests/**/README.md` | 各工程自己的目录职责说明,跟着代码走 |

改 UI 相关代码前先读 `DESIGN.md`:**XAML 与 C# 里不许出现颜色字面量**,一律用
`DynamicResource` 绑定令牌。

### 几条会让你踩坑的硬约束

- **界面文案五份 resx 必须齐**(`Strings` / `zh-Hans` / `zh-Hant` / `ja` / `ko`),不许硬编码字符串。
  漏译与孤儿键由 `LocalizedKeyUsageTests` / `UnusedLocalizedKeyTests` 拦住。
- **新增或改动快捷键先改目录**:唯一事实来源是
  `src/VelaShell/ViewModels/ShortcutCatalog.cs`,设置页与文档都从它取数。没登记的话
  `ShortcutCatalogTests` 会失败并打印出可粘贴的 Markdown 行。快捷键文档在
  [`zh/host/快捷键参考.md`](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/host/快捷键参考.md),改了要同步。
- **集成测试按环境早退跳过,而 MSTest 把跳过记为「通过」**。`DockerIntegration` 分类需要
  Docker 与 `docker-compose.test.yml`,`CrossPlatformPublishTests` 需 `VELASHELL_PUBLISH_TESTS=1`。
  要确认它们真跑过,看 `TestContext` 里有没有 `[SKIP]` 行 —— 全绿不等于跑过。
- **headless UI 测试要用带返回值的重载**:`Dispatch(async () => { …; return true; })`。
  `HeadlessUnitTestSession` 没有 `Func<Task>` 重载,写成无返回值会拿到一个从未被等待的
  `Task<Task>`,测试跑到第一个 `await` 就"通过",断言失败全部丢失。
- **插件 SDK 一律走 NuGet 包**,不做工程引用。版本在 `src/Directory.Packages.props`。

### 相关仓库

插件契约 SDK、`vela-plugin` CLI、`dotnet new` 模板、第一方插件与插件商店都在
[VelaShellLabs](https://github.com/VelaShellLabs) 组织下,文档全部在
[velashell-docs](https://github.com/VelaShellLabs/velashell-docs)。
