# 15 · 生态构想:插件清单、简化项与增强项

> 状态:**提案**(2026-07-24 评估产出)。本文档的每一项被采纳后,应回写
> 进 01–14 的对应文档并在此标记;未采纳的保留在此作为决策记录。

## 1. 高价值插件构想清单(按定位排序)

评估维度:价值(对运维工作台定位的贴合度)/ 复杂度 / 依赖的能力。
**加粗**者需要新扩展点(见 §2),其余用 apiLevel 1 已有能力即可实现。

### 第一梯队(直接命中日常运维痛点)

| 插件 | 说明 | 依赖能力 | 复杂度 |
| --- | --- | --- | --- |
| 数据库客户端 | 经 SSH 隧道连 MySQL/PostgreSQL/Redis,VelaUI 表格浏览 + SQL 编辑;运维"顺手查一下库"高频场景 | sessions.read、network(隧道由宿主既有隧道功能提供)、VelaUI | 高 |
| Docker/Compose 面板 | 容器列表/日志/启停/exec 进入,基于远端 docker CLI 输出解析 | remote.exec、VelaUI、终端输出通道 | 中 |
| systemd 服务管理 | 服务列表/状态/启停/开机自启/journal 尾部 | remote.exec、VelaUI | 低 |
| **命令片段库 / Runbook** | 带变量占位的常用命令库;Runbook=文档化操作手册,逐步一键执行进终端 | terminal.write、storage;**建议升级为贡献点**(§2.5) | 低 |
| 多机日志聚合 | 对多台服务器 tail -f,合流带主机前缀高亮展示,错误模式聚合 | remote.exec(流式)、终端输出通道 | 中 |
| 进程管理器 | htop 式面板(ps 解析),排序/搜索/kill | remote.exec、VelaUI | 低 |
| 磁盘占用分析 | du 输出渲染 ncdu 式树图/矩形图,下钻+删除 | remote.exec、remote.files.write、VelaUI | 中 |
| 证书巡检 | 检查各主机 TLS 证书有效期,临期通知;天然适配自动化 cron | remote.exec、notifications、automation | 低 |
| crontab 编辑器 | 可视化编辑远端 crontab,表达式解释与下次运行预览 | remote.exec、VelaUI | 低 |

### 第二梯队(增强粘性/团队场景)

| 插件 | 说明 | 依赖能力 | 复杂度 |
| --- | --- | --- | --- |
| 会话录制回放 | asciinema 式录制 terminal.read 流,本地回放/导出;审计与教学两用 | terminal.read、fs.local.write、图像/终端表面 | 中 |
| IM 通知集成 | 钉钉/飞书/Slack/Telegram webhook 作为自动化动作("部署完成→发飞书") | network、automation 动作贡献 | 低 |
| 网络工具箱 | ping/mtr/traceroute/端口探测/DNS 查询,本机或经远端主机发起 | remote.exec、VelaUI | 低 |
| 文件同步/对比 | 本地目录 vs 远程目录 diff 视图,选择性同步(rsync 语义) | remote.files.*、fs.local.*、VelaUI | 高 |
| Nginx 配置助手 | 站点列表、配置校验(nginx -t)、reload、访问日志摘要 | remote.exec、remote.files.* | 中 |
| K8s 面板 | kubectl 封装:pod 列表/日志/exec/describe | remote.exec、VelaUI | 高 |
| man/tldr 查询 | 终端选中命令 → 侧栏显示 tldr/man 摘要 | terminal.read(选区)、network | 低 |
| **主题/配色包** | 导入 iTerm2 等社区配色;冷启动期最易吸引社区的贡献形式 | **需主题贡献点**(§2.5) | 低 |

### 已在设计内的(对照)

图片查看器(S1)、MP3 播放器(S2)、AI 助手(S3)、日志高亮(S4)、
自动化(S5)、服务器仪表盘(S6)——本清单是它们的延伸,不重复列。

## 2. 建议新增的扩展点(按投入产出排序)

### 2.1 虚拟文件系统提供方(VFS Provider)★ 强烈建议列入 apiLevel 2 头牌

插件注册一种"远程文件系统类型"(S3/OSS/WebDAV/FTP/SMB/k8s Pod),
宿主把它呈现在**现有文件面板**里:双栏浏览、传输队列、断点续传、
右键菜单全部复用。

```jsonc
"contributes": { "fsProviders": [ { "scheme": "s3", "displayName": "Amazon S3",
                                    "connectionForm": { ...VelaUI 表单 schema... } } ] }
```

- 宿主 → 插件反向 RPC:`fs/list`、`fs/stat`、`fs/read`、`fs/write` 等
  (即 07 §2 IRemoteFs 的镜像,方向反转);
- 为什么值得:一个扩展点撬动整个"连接一切存储"生态,且是竞品
  (WinSCP/Termius)做不到的开放性;
- 前置:文件面板需抽象出"文件源"接口(目前绑定 SFTP)——这是宿主侧
  重构,建议在 M3 做 remoteFs 桥接时就预留接口,成本最低。

### 2.2 Shell 集成事件(OSC 133)★ 建议宿主侧尽早做,插件系统白得燃料

终端子系统识别 OSC 133 命令边界序列(需在远端 shell 注入集成脚本,
提供一键安装),向插件事件系统新增:

```text
CommandStarted { sessionId, commandLine }        // 需 terminal.read
CommandFinished { sessionId, exitCode, duration }
CwdChanged { sessionId, cwd }
```

价值:自动化从"盲目定时"升级为"命令失败即触发";AI 助手拿到结构化
的"刚才执行了什么、退出码多少"而不是猜字节流;审计粒度也随之提升。
该特性对宿主终端自身也有价值(命令导航/重跑),建议作为终端子系统
特性立项,插件系统只消费其事件。

### 2.3 多主机广播执行(会话组)

`remote.exec` 的多目标形态 + 独立权限 `remote.exec.multi`:

```csharp
Task<MultiExecResult> RunOnGroupAsync(string[] sessionIds, string command, ExecOptions o, CancellationToken ct);
```

- 强制 UX:执行前宿主弹确认清单("将在 12 台主机执行:…",逐台勾选);
- 结果聚合(成功/失败分组、输出对比视图交给插件 UI);
- 审计逐台记录。运维刚需,且安全敏感度高,值得一等公民化而不是让
  插件自己 for 循环(绕开"N 台确认"UX)。

### 2.4 凭据提供方(Credential Provider)——谨慎,列 M8 后评估

密码管理器(1Password/Bitwarden/KeePass)在宿主连接时**供给**凭据:
方向是插件→宿主,不违反"宿主凭据永不外流"红线;但要点在于凭据经
PluginHost 进程与 RPC 通道过手,信任面扩大。若做:专用权限
`credentials.provide`(最高敏)、供给的凭据仅在单次连接握手中使用、
不落宿主凭据库、提供方插件要求"已验证发布者"及以上信任档。
**在密码管理器官方 CLI 已很好用的现状下,优先级可后置。**

### 2.5 两个廉价贡献点(建议直接并入 apiLevel 1)

- `contributes.snippets`:命令片段(名称/命令模板/变量/适用 shell),
  进命令面板与终端右键;纯声明,宿主渲染,估 2–3d;
- `contributes.themes`:终端配色方案(声明式色表,复用现有换肤机制),
  估 2–3d。两者都不需要拉起插件进程,是"零代码插件"形态——大幅降低
  社区首次贡献门槛。

## 3. 建议简化项(v1 减法,合计省约 3–4 周)

| # | 简化 | 原设计 | 建议 | 理由/回补时机 |
| --- | --- | --- | --- | --- |
| C1 | 签名体系分两步走 | D-1/D-6 完整"发布者验证+吊销列表" | v1 只做:官方签名 + 自签名(装时黄标强警告);验证/吊销推至有第三方作者时 | 生态冷启动期只有官方插件,吊销无对象;签名**格式**不简化(含吊销字段),只推迟运营侧 |
| C2 | 权限 scope 收窄 UI | B-4 含"范围收窄编辑器"(改路径前缀/换会话) | v1 授权框只提供整权限五档决定 + 会话级 scope;路径前缀编辑器推 v1.1 | scope 数据结构与 Broker 匹配逻辑**保留**(B-2 不减),只推迟编辑 UX |
| C3 | VelaUI 控件白名单瘦身 | 08 §2.2 全量 | 砍 `TabControl` `DatePicker` `TreeView` `Sparkline`(S6 仪表盘场景顺延) | 三个官方示例都用不到;apiLevel 内加法随时补 |
| C4 | 空闲回收推迟 | 03 §7 idlePolicy | v1 不做,进程常驻直至停用 | 纯内存优化,不影响正确性;管理页占用透明展示先顶着 |
| C5 | `onSchedule` 激活事件并入 M7 | 03 §4 列于首批 | 与自动化引擎(T-1)同期交付 | 无规则引擎时 cron 激活没有消费场景 |
| C6 | 音频域降级预案升级为决策 | 07 §9 三候选 spike | 直接采用"Windows(WASAPI)首发,mac/Linux 随 v1.x",spike 范围减半 | 能力协商机制天然支持按平台宣告;MP3 示例仍可交付 |

**不建议简化的**(曾考虑过、明确否决):每插件一进程(隔离是本设计
的存在理由)、审计管线(运维工具的信任基石)、五语言 NLS(违背仓库
既定纪律)、VelaUI 整体(否则 S2/S6 类插件无从谈起,且后补协议成本
远高于首发)。

## 4. 建议增强项

| # | 增强 | 落点 |
| --- | --- | --- |
| E1 | 审计日志导出(JSON/CSV)+ 保留期设置——合规/复盘场景,运维工具应有 | 06 §4,+1d |
| E2 | 插件清单随配置导出/导入(已装插件 id 列表 + 各插件设置),多机同步一致体验 | 10 管理页,+2d |
| E3 | 权限"试运行"模式:装新插件后首个 24h 内所有危险权限即使"总是允许"也逐次通知(可关)——对"先装了再说"的用户兜底 | 06 §2,+1d |
| E4 | 插件商店"按场景"分组(容器/数据库/监控/传输…),对齐 §1 分类 | 10 §4,阶段 B |
| E5 | `contributes.snippets` / `contributes.themes`(见 §2.5) | 03 §6 + U 系列,+5d |

## 5. 采纳追踪

| 项 | 决定 | 回写位置 | 状态 |
| --- | --- | --- | --- |
| §2.1 VFS Provider | 待定 | 07(反向 RPC)、14(M3 预留接口 + apiLevel 2 规划) | 提案 |
| §2.2 OSC 133 | 待定 | 07 §8 事件、终端子系统独立立项 | 提案 |
| §2.3 会话组广播 | 待定 | 06 权限表、07 §3 | 提案 |
| §2.4 凭据提供方 | 待定(倾向后置) | 12 威胁模型先行分析 | 提案 |
| §2.5 snippets/themes 贡献点 | 待定 | 03 §6、08 §1、14(M3/M4) | 提案 |
| §3 C1–C6 简化 | 待定 | 各对应文档与 14 里程碑 | 提案 |
| §4 E1–E5 增强 | 待定 | 各对应文档 | 提案 |
