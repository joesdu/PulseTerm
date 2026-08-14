# VelaShell.Plugin.Ai.Tests

> [`plugins/VelaShell.Plugin.Ai`](../../plugins/VelaShell.Plugin.Ai) AI 助手插件的单元测试。

全部基于 [`VelaShell.PluginSdk.Testing`](../../plugin-sdk/VelaShell.PluginSdk.Testing) 的内存替身运行：**不启动宿主、不连数据库、不发一次网络请求**，因此可以在 CI 上无条件跑。这也是插件测试的推荐姿势 —— 插件只依赖 SDK 契约，替身就能把它整个托起来。

## 覆盖范围

| 文件 | 被测对象 |
|------|----------|
| `AgentToolboxTests` | Agent 工具箱：工具形状（名称/参数）、**审批闸门**（`run_command`/`write_terminal` 必须经批准，只读工具直通），以及各工具到插件能力的桥接语义。 |
| `AiSettingsStoreTests` | 设置存储：配置往返、机密与普通配置的隔离（API Key 只进 `Secrets` 能力），以及 OpenAI / Anthropic / OpenAI 兼容三种协议的客户端构造。 |
| `ChatHistoryStoreTests` | 会话历史（落插件私有**时序库**）：摘要唯一性（同序列同时间戳覆盖）、加载顺序、删除、输入框历史回溯。 |
| `McpConfigTests` | MCP 服务器配置：命令参数 / 环境变量 / 请求头的解析，工具名前缀清洗，以及设置往返。 |
| `FileReferenceTests` | 输入框 `@` 文件引用语法：补全 token 识别、目录拆分、发送时的路径提取。 |
| `ChatPanelViewUiTests` | 聊天面板的 headless 装载与交互：XAML 真装载一次（Popup、资源引用这类编译期看不出的问题在此暴露），并验证历史开关与输入框 ↑↓ 回溯。 |
| `ChatPanelHeadlessApp` | UI 测试共用的 headless 宿主。**刻意只装 Fluent、一个 `Vela*` 令牌都不给** —— 于是这套测试顺带守住「宿主令牌缺席时面板照样能装载」（隔离进程首帧、主题令牌还没下发到位时正是这个状态）。 |

## 运行

```bash
dotnet test tests/VelaShell.Plugin.Ai.Tests/
```

> UI 测试跑在 Avalonia headless 会话上，全套共用一条 UI 线程：测试体必须同步（`return Task.CompletedTask`）并在结束前关窗，否则整个套件会卡死。排查用 `--blame-hang-timeout`。
