# 03 · 插件模型:包格式、Manifest、生命周期、贡献点

## 1. 包格式 .vpx

`.vpx`(VelaShell Plugin Package)是一个 zip 容器:

```text
my-plugin-1.2.0.vpx
├── plugin.json                  # manifest(必需,UTF-8)
├── plugin.nls.zh-Hans.json      # 文案本地化(可选,五语言各一份)
├── plugin.nls.ja.json / ...
├── bin/
│   ├── MyPlugin.dll             # 入口程序集(manifest.entry 指向)
│   ├── MyPlugin.deps.json       # 依赖清单(ALC 解析依据)
│   └── <第三方依赖>.dll          # 插件自带全部依赖(自包含,不含 SDK 引用的共享契约)
├── assets/                      # 图标、静态资源
│   └── icon.png                 # 128×128,插件管理页与市场展示
├── README.md                    # 详情页展示
├── CHANGELOG.md                 # 可选
└── SIGNATURE                    # 签名文件(见 10-packaging)
```

约束:

- zip 内禁止绝对路径与 `..` 路径段(解包器强制校验,防 zip-slip)。
- `VelaShell.PluginProtocol.dll` / `VelaShell.PluginSdk.dll` **不打进包**:
  由 PluginHost 侧统一提供(共享程序集),保证契约类型同一性。
  打包 CLI 负责剔除并校验。
- 解包后目录只读使用;插件运行期可写目录只有其 `data/<pluginId>/`。

## 2. Manifest 规范(plugin.json)

```jsonc
{
  "$schema": "https://velashell.dev/schemas/plugin-v1.json",
  "id": "acme.image-viewer",          // 必需。<发布者>.<名称>,小写,[a-z0-9.-]
  "version": "1.2.0",                 // 必需。semver
  "displayName": "%displayName%",     // %key% 走 NLS 间接寻址
  "description": "%description%",
  "publisher": "acme",
  "icon": "assets/icon.png",
  "license": "MIT",
  "homepage": "https://github.com/acme/image-viewer",

  "engines": {
    "velaShell": ">=0.2.0",           // 宿主版本区间(semver range)
    "apiLevel": 1                     // 见 §5,握手时协商
  },
  "platforms": ["win-x64", "win-arm64", "osx-arm64", "linux-x64"],  // 省略 = 全平台

  "entry": "bin/MyPlugin.dll",        // 入口程序集;入口类型经 [VelaPlugin] 特性发现
  "hostMode": "isolated",             // isolated(默认,独占进程)| shared(远期保留,v1 忽略)

  "activationEvents": [               // 见 §4
    "onCommand:acme.image-viewer.open",
    "onFileOpen:remote:**/*.{png,jpg,jpeg,gif,webp}"
  ],

  "permissions": [                    // 见 06-permission-system
    "remote.files.read",
    { "id": "fs.local.read", "reason": "%perm.localRead.reason%" }  // 可附申请理由,授权框展示
  ],

  "contributes": { ... }              // 见 §6
}
```

校验规则(安装时强制,`vela-plugin validate` 亦执行同一套):

- JSON Schema 校验(schema 随 SDK 发布并托管);未知顶层字段警告、
  未知 `contributes` 子键拒绝(防拼写错误静默失效)。
- `id` 全局唯一(与已安装集合比对);`entry` 必须存在于包内。
- `permissions` 中出现未知权限 id → 拒绝安装(宿主老、插件新的情形由
  `engines` 区间挡住)。
- 声明 `remote.*` / `fs.*` / `terminal.*` 等危险权限的插件,详情页与
  安装确认页必须逐条展示(Chrome 商店式醒目提示)。

## 3. 入口约定(SDK 侧)

```csharp
[VelaPlugin]                                  // PluginHost 反射发现,一个包恰好一个
public sealed class ImageViewerPlugin : IVelaPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken ct);
    public Task DeactivateAsync(CancellationToken ct);   // 限时(默认 5s),超时进程被终止
}
```

`IPluginContext` 是插件拿到一切能力的唯一入口:

```csharp
public interface IPluginContext
{
    string PluginId { get; }
    string DataDirectory { get; }            // data/<pluginId>/ 绝对路径
    ActivationReason Activation { get; }     // 因哪个激活事件被拉起(含参数)
    IRemoteFs RemoteFs { get; }              // 各能力代理,见 07
    ITerminal Terminal { get; }
    ISessions Sessions { get; }
    ILocalFs LocalFs { get; }
    IUi Ui { get; }
    IStorage Storage { get; }
    ISecrets Secrets { get; }
    IPluginSettings Settings { get; }
    IEvents Events { get; }
    IAudio Audio { get; }
    IAi Ai { get; }
    ILogger Log { get; }
    CancellationToken Shutdown { get; }      // 宿主要求停机时触发
}
```

## 4. 激活事件

| 事件 | 触发时机 | 参数 |
| --- | --- | --- |
| `onStartup` | 主程序启动完成后(延迟批次,不阻塞启动) | — |
| `onCommand:<commandId>` | 用户执行该命令(命令面板/菜单/快捷键) | commandId, 命令参数 |
| `onView:<viewId>` | 用户首次展开该插件贡献的侧栏视图 | viewId |
| `onSessionConnect` | 任一会话连接成功 | sessionId, 主机指纹信息(脱敏) |
| `onSessionConnect:<hostPattern>` | 匹配主机名/标签的会话连接 | 同上 |
| `onFileOpen:<selector>` | 用户在 SFTP/本地面板"打开方式"命中 glob;`remote:`/`local:` 前缀区分来源 | 文件路径、来源、sessionId |
| `onTransferComplete` | 一次 SFTP/ZMODEM 传输完成 | 传输摘要 |
| `onSchedule:<cron>` | cron 表达式命中(自动化,见 11) | 计划时间 |
| `onUri:<scheme>` | `velashell://<pluginId>/...` 深链 | uri |

规则:激活事件只决定**何时拉起**;拉起后的权限仍由权限系统独立控制
(能被 `onSessionConnect` 唤醒 ≠ 能读会话数据)。`onStartup` 在插件
详情页显著标注(常驻型插件,用户应知情)。

## 5. 版本与兼容承诺

- **apiLevel(整数)**:`PluginProtocol` 的兼容代际。宿主对同一 apiLevel
  承诺:只增不改不删(方法、DTO 字段、权限 id、贡献点 schema)。破坏性
  变更 → apiLevel+1,宿主同时支持相邻两级(N 与 N-1)至少 6 个月。
- **SDK semver**:同 apiLevel 内的功能性演进(新能力、新贡献点)以
  minor 版本发布;插件按需升级。
- 握手协商(见 05):宿主与插件各报 apiLevel,取交集失败则拒绝激活并
  在插件管理页给出明确的"需升级宿主/插件"提示。

## 6. 贡献点(contributes)

贡献点是**纯声明**:安装后无需拉起插件进程即可注册占位(菜单项、命令、
视图容器出现在 UI 中),用户交互时才触发激活。全部 schema 定义在
`PluginProtocol`,渲染细节见 [08-ui-extensions.md](08-ui-extensions.md)。

```jsonc
"contributes": {
  "commands": [
    { "id": "acme.image-viewer.open", "title": "%cmd.open%", "icon": "assets/open.svg",
      "category": "Image Viewer" }
  ],
  "menus": {
    "sftp/item/context": [                  // 挂载点:SFTP 面板文件右键菜单
      { "command": "acme.image-viewer.open", "when": "isFile && ext =~ png|jpg" }
    ],
    "commandPalette": [ ... ],
    "terminal/context": [ ... ],
    "session/context": [ ... ]
  },
  "views": [
    { "id": "acme.player.panel", "name": "%view.player%", "location": "sidebar",
      "icon": "assets/note.svg" }
  ],
  "documents": [                            // 可承载 VelaUI/图像表面的文档页类型(挂进 VelaDock)
    { "type": "acme.image-viewer.preview", "title": "%doc.preview%" }
  ],
  "statusBar": [
    { "id": "acme.player.status", "alignment": "right", "priority": 90 }
  ],
  "settings": [                             // 生成设置页(宿主渲染),值经 Settings 能力读写
    { "key": "acme.player.volume", "type": "number", "default": 80,
      "minimum": 0, "maximum": 100, "title": "%setting.volume%" }
  ],
  "keybindings": [
    { "command": "acme.player.playPause", "key": "ctrl+alt+p", "when": "..." }
  ],
  "automation": {                           // 见 11
    "triggers": [ { "id": "acme.watchdog.onHighLoad", "title": "%trig.highLoad%" } ],
    "actions":  [ { "id": "acme.watchdog.restartService", "title": "%act.restart%" } ]
  }
}
```

`when` 子句:与 VSCode 类似的上下文表达式(受限文法:标识符、比较、
`&& || !`、正则匹配 `=~`),由宿主求值;上下文键(如 `isFile`、`ext`、
`sessionConnected`)清单随 apiLevel 冻结在 `PluginProtocol`。

## 7. 插件生命周期(完整状态机)

```text
                    ┌────────────────────────────────────────────────┐
                    ▼                                                │
 [Discovered] → 校验(manifest/签名/engines/platform) ─失败→ [Incompatible/Invalid](管理页标注原因)
      │通过
      ▼
 [Installed] ←──────────────(Deactivate 完成,进程退出)──────────── [Deactivating]
      │ 激活事件命中(且未被禁用)                                        ▲
      ▼                                                              │空闲回收/禁用/停机/卸载/升级
 [Starting]:拉起 PluginHost → 握手 → Activate(ct)                     │
      │成功                    │失败/超时(默认 10s)                     │
      ▼                       ▼                                      │
 [Active] ────────────────► [Crashed]:进程退出/心跳丢失/协议错误        │
      │                        │退避重启:1s→5s→30s,窗口期内≤3次        │
      └────────────────────────┤超限                                  │
                               ▼                                     │
                           [Faulted]:不再自动重启;管理页红标+日志入口;用户可手动重启
 [Disabled]:用户禁用;贡献点占位移除;不响应激活事件
 [Removed]:卸载;进程停止→贡献点移除→授权记录删除→询问是否保留 data/ 目录
```

补充语义:

- **禁用/卸载即时生效**(G4):先撤贡献点(UI 立即消失),再走
  Deactivating;进程 5s 不退则强杀。
- **升级** = 新版本装入 `installed/<id>/<newVer>/` → 旧进程 Deactivate →
  原子切换当前版本指针 → 按需重新激活。失败自动回滚指针。
- **空闲回收**:插件可在 manifest 声明 `"idlePolicy": "recyclable"`,
  连续 N 分钟(默认 15)无 RPC 往来且无活跃 UI 表面时,宿主发
  Deactivate 回收进程;下次激活事件再拉起。常驻型(如自动化守护)不声明即常驻。

## 8. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| M-1 | `plugin.json` JSON Schema 定稿 + 校验器实现(含 zip-slip、NLS 解析)| A-4 | 3d |
| M-2 | 激活事件路由器:事件源接入(命令/视图/会话/文件打开)+ 匹配引擎(glob、host pattern)| M-1 | 3d |
| M-3 | 生命周期状态机实现(纯逻辑层,先以假进程句柄单测覆盖全部迁移)| M-1 | 3d |
| M-4 | 贡献点注册表 + `when` 表达式求值器(文法冻结 + 单测)| M-1 | 4d |
| M-5 | 安装/卸载/升级事务(目录布局、版本指针、回滚)| M-3 | 3d |
| M-6 | 五语言 NLS 管线(`%key%` 解析、语言切换热更新)| M-4 | 2d |

验收:用手写的最小 manifest(无真实进程)驱动状态机与贡献点注册表跑通
全部迁移路径;错误 manifest 的 20+ 种坏例全部给出可读的拒绝原因。
