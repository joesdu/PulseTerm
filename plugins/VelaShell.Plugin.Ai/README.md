# VelaShell.Plugin.Ai —— AI 助手插件

多提供商 AI 助手:在 VelaShell 内与大模型流式对话,并可开启 **Agent 模式**
让模型经审批调用工具(读终端输出、执行远程命令、读远程文件、写终端),
还可接入用户自定义的 **MCP 服务器**扩展工具集。

## 支持的接入

| 提供商 | 线协议 | 说明 |
| --- | --- | --- |
| OpenAI | Responses(流式)或 Chat Completions | 官方 API,二选一 |
| Anthropic Claude | Anthropic Messages(流式) | 官方 API |
| xAI Grok | OpenAI Chat Completions 兼容 | `https://api.x.ai/v1` |
| Ollama(本地自部署) | OpenAI Chat Completions 兼容 | `http://localhost:11434/v1`,无需 Key |
| 第三方中转站 | OpenAI 兼容 或 Anthropic 兼容 | 自填 Base URL + API Key |

Base URL 与 API Key 全部用户自填;API Key 经宿主 **Secrets 能力加密存储**
(Windows 上为 DPAPI 包裹的本地密钥),绝不明文落盘。

## 数据存储位置

插件不直连数据库 —— 一切持久化走 SDK 能力,宿主统一落 **SonnetDB** 的
`plugin_data` 集合(按插件 id 命名空间隔离,卸载自动清除):

| 数据 | 能力 | SonnetDB 文档主键 |
| --- | --- | --- |
| 接入配置/开关/系统提示词(`AiSettings` 整体 JSON) | `Storage` | `velashell.ai\|kv\|settings` |
| 各接入的 API Key(DPAPI 加密后入库) | `Secrets` | `velashell.ai\|secret\|apikey:<providerId>` |

## 界面

视觉语言与宿主对齐(DESIGN.md §5.1):主操作用宿主的 `VelaAccentPillButtonTheme`
强调药丸(发送/保存/批准),次操作用 `VelaOutlineButtonTheme` 描边(新会话/测试),
危险操作描边换 `VelaError`(停止/删除/拒绝);输入区仿宿主 input-affix 容器
(聚焦强调描边);图标复用宿主 `Icon.*` lucide 几何;Agent/设置为自带
ControlTheme 的芯片开关(`Ui/AiTheme.axaml`,隔离进程下仅令牌也能退化可用)。
顶栏右侧 **新会话** 按钮:终止当前请求并开始全新对话。

回复以 **Markdown 渲染**(Markdig 解析 + `Ui/MarkdownRenderer` 映射为 Avalonia 控件):
标题/粗斜体/删除线/行内代码/链接(可点击)/围栏代码块(语言标签 + 复制按钮)/
列表(嵌套)/引用/分隔线/管道表格;未覆盖语法回退渲染原文。流式期间按 ≥200ms
节流整段重渲染,收尾定稿。工具调用为**紧凑单行卡片**(状态图标 + 工具名 +
参数摘要),点击展开完整参数与结果;思考过程同款折叠区。

## 技术栈(用户决策 2026-08-10)

统一到 **Microsoft.Extensions.AI** 的 `IChatClient` 抽象:

- OpenAI 两种协议:`OpenAI` 官方 SDK + `Microsoft.Extensions.AI.OpenAI` 适配
  (`GetChatClient().AsIChatClient()` / `GetResponsesClient().AsIChatClient()`);
- Anthropic 协议:`Anthropic` 官方 SDK 内建的 `AsIChatClient()` 适配;
- Agent 循环:`FunctionInvokingChatClient`(`UseFunctionInvocation()`,上限 25 轮);
- 工具经 `AIFunctionFactory` 包装插件 SDK 能力(Sessions / Terminal / RemoteExec / RemoteFs);
- MCP:官方 `ModelContextProtocol.Core` SDK,`McpClientTool` 本身即 `AIFunction`,
  与内置工具同进一个函数调用循环。

依赖随插件目录分发,由插件 ALC 按 deps.json 隔离解析,与宿主互不干扰。

## 命令(Ctrl+P / Ctrl+K)

| 命令 | 说明 |
| --- | --- |
| AI: Open Chat (Tab) | 打开聊天面板(可停靠标签页) |
| AI: Open Chat (Window) | 打开聊天窗口 |
| AI: Explain Terminal Output | 抓取当前会话终端末尾 200 行送模型解释 |

插件按 `onCommand` **惰性激活**:不触发 AI 命令就不装载程序集,启动零开销。

## Agent 模式与安全

- 工具 `run_command`(独立 exec 通道,不进用户终端)与 `write_terminal`
  默认**逐条审批**(面板内 批准/拒绝 卡片);可勾选"自动批准"跳过(有风险)。
- `write_terminal` 之上还有宿主自己的终端回写授权弹窗(四态)。
- `read_terminal` / `list_sessions` / `read_remote_file`(≤256KB)为只读,不需审批。
- 目标会话由面板顶部的会话下拉框选定。

## MCP 服务器(自定义)

设置页底部可添加任意多个 MCP 服务器,两种连接方式:

- **Stdio(本地进程)**:命令(`npx` / `uvx` / 任意可执行文件)+ 单行参数
  (含空格片段用引号)+ 可选工作目录与 `KEY=VALUE` 环境变量;
  Windows 下脚本命令的 cmd 包装由 SDK 处理。
- **HTTP(远端)**:端点 URL + 可选 `Name: Value` 请求头(鉴权令牌等);
  Streamable HTTP / SSE 自动探测。

行为约定(`Agent/McpManager`):

- 仅 **Agent 模式**下连接**启用的**服务器;连接按配置指纹缓存复用,
  失败退避 30s 再重试,单服务器失败不影响其余(错误显示在状态行)。
- 工具名加 `服务器名_` 前缀防冲突(清洗为 `[A-Za-z0-9_-]`,截断至 64)。
- 按 MCP `readOnlyHint` 注解区分:只读工具直接执行;**非只读工具走与
  `run_command` 相同的审批卡**,"自动批准"开关同样生效。
- 设置页"测试"按钮即时连接并列出该服务器的工具;面板关闭时全部断开。

## 测试

`tests/VelaShell.Plugin.Ai.Tests`:基于 `VelaShell.PluginSdk.Testing` 替身,
覆盖工具箱审批闸门、能力桥接语义与设置/机密存取。
