# 06 · 权限系统

设计目标:让用户对"这个插件能碰什么"有 Android 级别的透明与控制——
声明先行、危险权限首次使用弹窗、范围可收窄、随时可撤销、有据可查。

> 信任模型前提(必读):Broker 是**逻辑层强制点**,它完备地约束"经由
> 宿主能力 API 的一切访问"(SSH 会话、凭据、终端、宿主 UI——这些资源
> 本来就只存在于宿主进程,插件绕不过去);但插件进程本身是普通 OS 进
> 程,**直接的本地文件与网络 OS 调用在 v1 无法被 Broker 物理拦截**。
> 因此:fs.local/network 权限在 v1 的意义是"合规插件的行为契约 + 用户
> 知情",对抗恶意代码依赖签名/来源与 OS 沙箱路线(12)。UI 文案不得
> 夸大 v1 的保护强度。

## 1. 权限清单(apiLevel 1)

### 普通权限(安装即授予,详情页列出)

| id | 授予的能力 |
| --- | --- |
| `ui.contributions` | 注册/更新贡献点、VelaUI 表面、对话框、通知(有频控) |
| `storage.private` | 读写自己的 data 目录与 KV 存储 |
| `settings.own` | 读写自己贡献的设置项 |
| `i18n.read` | 读取当前语言/地区 |
| `sessions.observe.basic` | 会话列表的**脱敏视图**(仅会话显示名与连接状态,无主机名/用户名/端口) |

### 危险权限(首次使用弹窗;粗体为高敏,弹窗带红色警示条)

| id | 授予的能力 | 可收窄范围(scope) |
| --- | --- | --- |
| `sessions.read` | 会话完整元数据(主机、用户、端口、标签)与连接事件 | 按会话 |
| `remote.files.read` | 经宿主会话 SFTP 读:list/stat/read/download | 按会话;按远程路径前缀 |
| **`remote.files.write`** | SFTP 写:upload/write/mkdir/rename/delete | 同上 |
| **`remote.exec`** | 在会话上开独立 exec 通道执行命令(非交互,不进用户终端) | 按会话 |
| `terminal.read` | 订阅终端输出流、读屏幕快照与选区 | 按会话 |
| **`terminal.write`** | 向用户终端注入输入(等同替用户敲键盘) | 按会话 |
| `fs.local.read` | 经宿主 API 读本地文件 | 按目录前缀(授权时用户选目录) |
| **`fs.local.write`** | 经宿主 API 写本地文件 | 同上 |
| `network` | 插件进程访问网络(v1 为声明制,见前提) | 声明域名列表(详情页展示) |
| `clipboard.read` / `clipboard.write` | 剪贴板 | — |
| `secrets` | 在 OS 凭据库中存取**自己命名空间**的机密 | — |
| `audio.playback` | 经宿主音频服务播放 | — |
| `notifications.system` | 发系统级(OS)通知 | — |
| `automation.rules` | 注册自动化触发器/动作、创建规则建议 | — |
| `ai.invoke` | 调用宿主 AI 网关(用户配置的模型与额度) | 按模型档位 |
| **`sessions.create`** | 以已保存的连接配置发起新会话(不可见凭据) | 按连接配置项 |

原则:

- **凭据永不授予**:没有任何权限能读取密码/私钥/API key;
  `sessions.create` 只是"请宿主用 3 号配置连一下",凭据全程在宿主。
- **写强于读、独立授权**:read 与 write 分开;授予 write 不隐含 read
  之外的任何能力。
- 未声明的权限**运行时不可申请**(与 Android 一致):调用直接
  PermissionDenied,不弹窗。想加权限只能发新版本,升级时增量权限
  高亮提示(Chrome 模式)。

## 2. 授权流程与 UX

```text
插件调用能力 → Broker.Demand(pluginId, permId, scope)
  ├─ 已授予(且 scope 覆盖)→ 放行,记审计
  ├─ 已永久拒绝 → 直接 PermissionDenied(不打扰用户)
  ├─ 本会话拒绝 → PermissionDenied
  └─ 未决 → 入队授权提示(同插件并发请求合并;UI 线程逐个弹)
        ┌────────────────────────────────────────────┐
        │  🖼 Image Viewer(acme,已验证)              │
        │  请求权限:读取远程文件                       │
        │  范围:会话 "prod-web-01" 的 /var/www/**     │
        │  插件说明:%perm.reason%(manifest 提供)      │
        │  [仅本次] [仅此会话] [总是允许] [拒绝] [总是拒绝]│
        │  ☐ 收窄范围…(改路径前缀/换会话)              │
        └────────────────────────────────────────────┘
```

- 弹窗是**应用内模态**(主窗口内遮罩对话框,复用现有对话框样式),
  标题带插件名、发布者与签名校验状态;高敏权限加红色警示条与二次
  确认(如 `terminal.write`:"该插件将能向你的终端输入命令")。
- 决定粒度:`仅本次`(单次调用)/`仅此会话`(app 会话内)/
  `总是`(持久化)/`拒绝` / `总是拒绝`。
- **调用方语义**:弹窗期间原调用 await 挂起(带 60s 超时,超时视为
  本次拒绝);插件应把权限失败当正常分支处理,SDK 提供
  `ctx.Permissions.RequestAsync(...)` 允许插件在合适的 UX 时机主动
  预请求(仍走同一 Broker,不可绕过)。
- 防"授权疲劳轰炸":同一插件同一权限被拒后 5 分钟内不再弹(直接拒);
  单插件队列上限 3 个待决提示,溢出直接拒。

## 3. 持久化与管理

`<AppData>/VelaShell/plugins/permissions.json`:

```jsonc
{ "version": 1,
  "grants": [
    { "plugin": "acme.image-viewer",
      "pluginVersion": "1.2.0",          // 记录授权时版本,供增量权限对比
      "permission": "remote.files.read",
      "decision": "allowAlways",         // allowAlways | denyAlways
      "scope": { "sessions": ["prof-a3f2"], "pathPrefixes": ["/var/www/"] },
      "grantedAt": "2026-07-24T10:00:00Z" } ],
  "integrity": "<HMAC,密钥存 OS 凭据库>" }
```

- integrity 校验失败(文件被外部篡改)→ 全部授权作废并提示用户重新
  授权(fail-closed)。
- **设置页 → 插件 → 权限**:双视图(按插件 / 按权限,Android 式),
  每条可改范围、撤销;撤销即时生效(Broker 缓存失效,进行中的流式
  订阅被切断)。
- 卸载插件 → 删除其全部授权;升级且新版本新增危险权限 → 该新权限
  从未决开始,不继承。

## 4. 审计

- 环形缓冲(每插件最近 512 条 + 全局文件日志,默认 7 天):时间、
  权限、scope、结果(granted/denied/prompted)、调用摘要(路径脱敏
  选项)。
- 管理页"最近活动"时间线:如"14:02 读取了 prod-web-01:/var/log/nginx/
  access.log"。这是把 Android 权限使用面板搬过来——**事后可查**与
  事前弹窗同等重要。
- 状态栏常驻指示器:任一插件正在使用 `terminal.*` / `remote.exec` 时
  显示脉冲图标(类似手机的麦克风/摄像头指示点),点击直达审计页。

## 5. Broker 实现要点

- 单例,位于 `Infrastructure/Plugins/Permissions/`;能力服务入口
  **强制**经 `Demand`(能力实现基类里做,新增能力想绕都绕不开——
  分析器规则 + 代码评审清单双保险)。
- 决策缓存:内存字典 + 撤销版本号;热路径(终端输出订阅的每帧)不
  重复查询——订阅建立时校验一次,撤销经版本号使订阅失效。
- scope 匹配:路径前缀经规范化(去 `..`、大小写按远端 FS 语义)后
  比较;会话 scope 绑定连接配置 id 而非易变的 sessionId。

## 6. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| B-1 | 权限模型定稿:id 清单、分级、scope 结构、错误语义(评审关口,涉及 07 全部 API 签名) | P-3 | 2d |
| B-2 | Broker 核心:Demand 管道、决策缓存、撤销版本号、fail-closed;纯逻辑单测 | B-1 | 3d |
| B-3 | 持久化:permissions.json + HMAC 完整性 + 迁移框架 | B-2 | 2d |
| B-4 | 授权对话框(五语言文案)、队列与防轰炸、范围收窄编辑器 | B-2 | 4d |
| B-5 | 设置页权限管理(双视图、撤销、即时生效)| B-3, B-4 | 3d |
| B-6 | 审计管线 + 最近活动时间线 + 状态栏指示器 | B-2 | 3d |
| B-7 | 分析器/评审规则:能力实现必须过 Demand 的静态检查 | B-2 | 1d |

验收:权限矩阵测试——每个危险权限 ×(未声明/未决/单次/会话/总是/拒绝
/撤销后)七种状态的行为全部符合本文档;授权疲劳护栏与完整性 fail-closed
有专项测试。
