# VelaShell.Plugin.Ai —— AI 助手插件

多提供商 AI 助手:在 VelaShell 内与大模型流式对话。三种对话模式 ——
**对话**(纯问答)/ **计划**(只给只读工具)/ **Agent**(经审批调用工具:
读终端输出、执行远程命令、读远程文件、写终端),还可接入用户自定义的
**MCP 服务器**扩展工具集。

## 支持的接入

接入方式有两种,在**「连接供应商」**那一页(设置窗口左下角「新增供应商」)里选:

* **订阅登录** —— 点一下就开浏览器,授权完自己跳回来,凭据加密存好、模型一并配好,**全程零输入**。
* **填 API Key** —— 点一下只问一把 Key,别的都不问。

**这一页的规矩只有一条:能自动的绝不问用户,只问程序确实不知道的那几样。**
名称、模型 id、基地址、协议目录里都有出厂值,一律收进「高级设置」——
走中转站要改的人找得到,其余人一眼看不见。于是每一行点下去是这样:

| 这一行的处境 | 点下去会怎样 |
| --- | --- |
| 订阅登录,参数齐 | **立刻开浏览器**,展开区里一个输入框都没有 |
| 订阅登录,VelaShell 还没注册 OAuth 应用 | 只问一次**客户端 id**,旁边给出「打开注册页」 |
| 订阅登录,地址按资源分配(Azure) | 只问**地址 + 客户端 id** |
| 填 Key | 只问**一把 Key** |
| 本地自部署(Ollama) | 什么都不问,直接加 |

内置目录(`Configuration/ProviderCatalog.cs`)节选:

| 提供商 | 线协议 | 接入方式 |
| --- | --- | --- |
| OpenAI Codex(ChatGPT 订阅) | OpenAI Responses | **一键登录**(PKCE)—— ⚠ **实验性**,见下 |
| Anthropic Claude(Claude 订阅) | Anthropic Messages | **一键登录**(PKCE,固定回调 `localhost:53692`)—— ⚠ **实验性** |
| GitHub Copilot | OpenAI Chat Completions 兼容 | **一键登录**(设备码 → 换会话令牌)—— ⚠ **实验性** |
| OpenRouter | OpenAI Chat Completions 兼容 | **一键登录**(PKCE,换回一把长期 Key) |
| Hugging Face | OpenAI Chat Completions 兼容 | **一键登录**(PKCE 公共应用,scope `inference-api`)—— 待填客户端 id |
| Azure OpenAI | OpenAI 兼容 | **登录**(Entra ID 设备码)—— 待填租户与客户端 id |
| 自定义(OAuth 登录) | 二选一 | **登录**,端点 / 客户端 id / scope 全部自填 |
| OpenAI | Responses(流式)或 Chat Completions | 填 Key |
| Anthropic Claude | Anthropic Messages(流式) | 填 Key |
| xAI Grok / Google Gemini / DeepSeek / Moonshot / Z.AI / 通义 / Together / Fireworks / Groq / Mistral | OpenAI Chat Completions 兼容 | 填 Key |
| Ollama(本地自部署) | OpenAI Chat Completions 兼容 | `http://localhost:11434/v1`,无需 Key |
| 第三方中转站 | OpenAI 兼容 或 Anthropic 兼容 | 自填 Base URL + API Key |

### 模型规格从哪儿来

接上一家之后,模型 id、**上下文窗口**与**三档单价**会自动填好,数据来自开源的
**models.dev**(`github.com/sst/models.dev`,`Configuration/ModelsDevCatalog.cs`)。

**为什么不是去问供应商自己的 `/v1/models`**(做过一版,已撤):那条接口只给一串 id,
给不出窗口和单价 —— 而那几项恰恰是本插件里最难填、**填错了又不报错**的东西
(窗口填错,输入框下方的上下文占比就是错的;单价填错,花费估算跟着错)。
何况订阅型的私有后端(ChatGPT 的 Codex 后端)根本没有那条接口。models.dev 两个问题一起解决。

| 细节 | 做法 |
| --- | --- |
| 体积 | 原始 `api.json` 约 4.3 MB / 200+ 家;下载后当场转成精简索引(七个字段)再落盘,约五分之一 |
| 缓存 | 插件私有数据目录,7 天算新鲜;卸载插件时随之清除 |
| 拉不到 | 退回已有缓存;没缓存就退回目录里的出厂示例。**不拦住"已经连上了"这件事** |
| 空响应 | 不覆盖好缓存(对方改版时别把能用的那份弄丢) |
| 只填不抹 | 上游没给的项**保持用户已填的值** —— 抹成 0 比不填更糟 |
| 型号换代 | 出厂示例不在清单里时,选**同前缀里最新的那个**(`gpt-5-codex` → `gpt-5.3-codex`),而不是字母序第一个 |

> 目录里的模型 id 只是**出厂示例**,用于还没拉到规格库时打底;拉到之后以规格库为准。
> 本目录 id 与 models.dev 的 id 对不上的有好几家(`moonshot`→`moonshotai`、`zhipu`→`zhipuai`、
> `qwen`→`alibaba`、`together`→`togetherai`、`fireworks`→`fireworks-ai`),
> 映射集中在 `ProviderCatalog.ModelsDevIds` 一处 —— 散着写对不上时是**静默**拉不到模型,最难查。

### ⚠ OpenAI Codex:实验性接入

这一条与目录里其余任何一条都不同,单独说清楚:

* **借的是 Codex 命令行工具的公共客户端 id**,不是 VelaShell 自己注册的。等于本程序以 Codex
  客户端的身份去换取用户的 ChatGPT 订阅权益。
* **打的是 `chatgpt.com/backend-api/codex`**,那是 ChatGPT 的产品后端,不是
  `api.openai.com/v1` 那种公开承诺稳定的 API。URL、请求头、账号 id 规则、响应格式都可能变。
* 因此目录里给它标了 `Experimental = true`,界面上行名后面挂一枚「实验性」小标,
  展开时先给一段说明。**它可能在任何一天失效**,是否符合 OpenAI 条款需使用者自行确认。

技术上它复用了本插件已有的全套机制,只是把几处差异写成了配置:

| 差异 | 配置项 |
| --- | --- |
| 回调必须是 `http://localhost:1455/auth/callback`(端口固定,且 `localhost` 与 `127.0.0.1` **不通用**) | `RedirectPort` / `RedirectPath` / `RedirectHost` |
| 授权请求要带 `id_token_add_organizations` 等参数 | `ExtraAuthorizeParams` |
| 账号 id 藏在 `id_token` 的命名空间 claim 里 | `AccountIdClaim` |
| 每条请求要带 `chatgpt-account-id` | `ExtraHeaders`(值里 `{account_id}` 会被替换) |

> **想换回官方路子时**:OpenAI 有一条官方的 **Codex App Server**(`codex app-server`,stdio 上的
> JSON-RPC),登录 / 令牌 / 刷新全部由官方组件管、不用借客户端 id。届时删掉这一条目录记录、
> 换成 App Server 的子进程适配器即可 —— 其余接入不受任何影响。

### 客户端 id 从哪儿来

**两种来路,目录里分得很清楚,界面上也分得很清楚。**

**一、VelaShell 自己注册的 OAuth 应用。** 这是常规路子:客户端 id 集中在
`ProviderCatalog.ClientIds`,**拿到一个填一个,填完那一行当场变成"点一下即登"**,不改任何逻辑;
还空着的那几家(Hugging Face、Google),界面上会说明情况并给出注册入口,
愿意自己注册的用户当场就能填(只填这一次)。

申请时按**公共客户端**(native / desktop,不要密钥)登记,回调地址填
`http://127.0.0.1/callback` 与 `http://localhost/callback` 两条 —— 遵循 RFC 8252 §7.3 的服务端
(Hugging Face 明确支持)只比对 scheme/host/path,端口在请求时任意,正好对上本程序
"每次随机取一个空闲环回端口"的做法。

**二、借各家官方 CLI / 编辑器插件公开的客户端身份。** OpenAI Codex、Anthropic Claude、
GitHub Copilot 这三条走的是这一路 —— 因为**没有第二条路**:这三家的订阅权益不通过公开 API 发放,
也不受理第三方注册应用去换取它。要让付了 ChatGPT Plus / Claude Pro / Copilot 的用户用上手里的额度,
只能以官方客户端的身份去要。

代价说在明处:**对方随时可以改**,改了这一条就当场失效。所以这三条在目录里都标了
`Experimental = true`,界面上行名后挂一枚「实验性」小标,展开时先给一段说明 ——
让用户自己判断要不要用,而不是替他决定。是否符合各家条款,需使用者自行确认。

> 这两类的边界就是 `ProviderCatalogEntry.Experimental` 这一个布尔值。
> 哪天某家开了正规的第三方注册,把 id 换掉、标记去掉即可,不动任何逻辑。

### 订阅登录怎么走

四套流程都实现了(`Auth/`),选哪套是供应商的配置(`OAuthConfig.Flow`)而不是代码分支:

| 流程 | 什么时候用 | 实现 |
| --- | --- | --- |
| 授权码 + PKCE(RFC 7636) | 桌面默认。起一个**环回端口**接住浏览器打回来的 `code` | `LoopbackRedirectListener` + `OAuthClient` |
| 设备授权码(RFC 8628) | 浏览器和程序不在同一台机器上(远程桌面 / 转发),或企业不给回调地址 | `OAuthClient.StartDeviceCodeAsync` / `PollDeviceCodeAsync` |
| OpenRouter PKCE | 同上第一条,但换回来的是一把**长期 Key** 而不是 access token | `OAuthClient.ExchangeForApiKeyAsync` |
| GitHub Copilot 两段式 | 设备码换到长期 token 后,还要拿它**再换**一枚短命会话令牌 | `OAuthClient.ExchangeForSessionAsync` |

**Copilot 那一段单独说。** 它的续期不是标准的 `refresh_token` 授权,而是**重做一次交换** ——
长期 token 留在 `RefreshToken` 位上,会话令牌到期就拿它再换一枚。另外交换端点会校验
**调用方是不是一个编辑器**:只带 `Authorization` 会被 403,而那个 403 的正文里一个字都不提缺了什么
(真机上就撞过)。所以 `OAuthConfig.ExchangeHeaders` 单列了一份交换时的请求头
(`Editor-Version` 等)——它与推理时的 `ExtraHeaders` **不是同一组**,混在一起就是那个查不出来的 403。
端点也由服务端在交换响应里下发(`endpoints.api`):企业账户和个人账户不是同一个。

几处值得记下来的:

* **环回监听是裸 `TcpListener`,不是 `HttpListener`。** 后者在 Windows 上走 http.sys,前缀注册要看 URL ACL
  的脸色(某些策略下非管理员直接 `AccessDenied`),而且在部分平台上被标为不支持。要接的只是**一条 GET
  请求行**,自己读一行、回一页反而三个平台行为一致。路径对不上的请求(浏览器顺手要的 `/favicon.ico`
  最常见)回 404 之后**继续等** —— 拿它当结果的话,用户还没点同意就先失败了。
* **`state` 与 PKCE 各管各的。** PKCE 防的是授权码被中途截走(challenge 是 verifier 的 SHA-256,反推不出来),
  `state` 防的是别人往我的回调里塞一个自己的 code。OpenRouter 的回调不带 `state`,那一路显式跳过校验
  而不是假装验过 —— 它靠的是"环回端口只活这一次 + code 与本机 verifier 绑定"。
* **`slow_down` 必须真的加间隔**(RFC 8628 §3.5),否则会一直被限速;`authorization_pending` 则要照原间隔
  继续等,把它当失败的话用户根本来不及点同意。
* **登录成功才把供应商写进设置。** 失败或中途放弃时,列表里不该多出一个连不上的空壳。
* **凭据分两种落法**:换回来的若是短期 access token,存 `oauth:<供应商 id>`,发请求前临近过期就用
  refresh token 换新的(客户端本来就是每条消息现建的,在建之前刷,等于每条消息都拿着新鲜令牌上路,
  不必再往管道里塞一层"401 就重试");若换回来的是一把长期 API Key(OpenRouter 那类),
  就与手填的 Key 走**同一条路**,不需要刷新。

> 要接目录里没有的(包括自建网关),用「自定义(OAuth 登录)」把端点填进去即可 ——
> 加一家 = 加一条数据,不改任何逻辑。

### 端点怪癖:订阅型后端只是标准协议的一个子集

订阅型的私有后端(ChatGPT 的 Codex 后端最典型)**不是**公开 API 的等价物,而是它的一个**受限子集**:
多发一个它不认的字段,整轮 400,而且**一次只肯告诉你一个**。逐个试错的成本极高。

所以这几项做成目录里的**数据**(`EndpointQuirks`),不是按供应商 id 写死的代码分支:

| 怪癖 | 配置项 | Codex 后端的情况 |
| --- | --- | --- |
| 服务端存不存这一轮响应 | `StoreResponses` | 不接受 `store`,带上就 400 |
| 收不收 `system` 角色的消息 | `AllowSystemMessages` | 不收 —— `{"detail":"System messages are not allowed"}`。关掉之后系统提示词改走 Responses 协议自己的 `instructions` 字段 |
| 不认哪些请求参数 | `UnsupportedParameters`(每行一个线上字段名) | `max_output_tokens` / `temperature` / `top_p` / `stop` / `frequency_penalty` / `presence_penalty` / `seed` 全不认 |

**两条踩出来的教训,都上了棘轮:**

* **必须请求时从目录读,不能建供应商时快照进用户的配置。** 一开始是快照的,结果新加的规则
  永远到不了**已经连上的**用户那儿 —— 用户那边一直是同一句 400,而代码里改了三轮。
  现在 `EndpointQuirks.Of(provider)` 每次都回目录取。
* **`ResponsesWireTests` 拿真实 OpenAI SDK 打本地桩、抓请求体**,再比对 openai/codex 的
  `ResponsesApiRequest` 字段白名单。以后谁往请求里多加一个 Codex 不收的字段,**会在 CI 红**,
  而不是在用户机器上 400 —— 这一轮就是靠一来一回的 400 才定位到的,不该有第二轮。

**中转站的 SSE 会洗一遍**(`Chat/SseRepairHandler.cs`,挂在 `AnthropicClient.Handlers` 上):
有些中转站按 OpenAI 的习惯在 Anthropic 流末尾补一行 `data: DONE`,而 Anthropic 协议里没有这东西
(它以 `message_stop` 收尾),SDK 又对每个 `data:` 行无条件反序列化 —— 整轮回复会在最后一刻炸成
`'D' is an invalid start of a value`,前面已经流出来的内容全白费。SDK 的 SSE 解析是 internal 够不着,
所以在 HTTP 层把非 JSON 的 `data:` 行滤掉:合法事件一个字节不动地转发并**逐行冲刷**
(攒着批量写会把流式变成"一次到货")。丢掉的都会往外报,但**分两档**:公认的收尾哨兵
(`DONE` / `[DONE]`)每轮都会来,只报头一次且只记 Info —— 既留住"清洗确实生效了"的凭据,
又不至于每轮刷一条 Warning;认不出来的载荷则每次都警告,那可能是中转站塞进来的错误信息,
漏一条就变成一次无声的截断。

**判据取自请求体里的 `"stream":true`,不看响应的 `Content-Type`。** 第一版按
`Content-Type == text/event-stream` 判断,在真实中转站上整个没生效 —— 它给流式响应贴的是
`application/json`(起本地假端点复现确认)。非流式响应则一个字节都不碰:HttpClient 已经把它
缓冲成可重复读的了,换成管道就只能读一次,而 SDK 的校验/解析路径可能读两遍。

Base URL 与 API Key 全部用户自填;API Key 经宿主 **Secrets 能力加密存储**
(Windows 上为 DPAPI 包裹的本地密钥),绝不明文落盘。解出来的密钥在进程内按 Key 归属者(供应商 / 带独立 Key 的模型)缓存,
写入/删除时失效 —— 否则每发一条消息都要走一遍"读库 + DPAPI 解包"。

每个模型还可单独配:采样参数(temperature / top_p / 停止序列,留空 = 不发该参数)、
**专用系统提示词**(盖过全局那份)、以及三档**单价**(输入 / 输出 / 缓存命中,每百万 token)。
填了单价,用量的悬停提示里就会给出这段会话的花费估算;留 0 就不估。

## 供应商 › 模型两层配置

配置分**供应商**(`AiProvider`:一个 Base URL + 默认协议 + 一把共用 API Key)与其下的
**模型**(`AiModelConfig`:模型 id 及容量 / 思考 / 采样 / 单价 / 专用提示词)两层。
中转站场景下一堆模型共用同一个地址与 Key,只是模型名和协议不同 —— 每个模型复制一遍
地址和 Key 既难改又难看,所以拆开。模型级仍可**单独覆盖**三样东西:协议
(`Protocol`,null = 继承供应商默认)、API Key(`HasOwnApiKey`,机密键换成模型自己的 id)、
Base URL(`BaseUrlOverride`)。发请求 / 建客户端 / 算成本的代码只认合并好的扁平视图
`ResolvedModel`(`AiSettings.ResolveModels()` / `FindModel()`),不关心两层结构;
`ActiveModelId` 指向模型 id。

**旧版扁平接入自动迁移**(`Configuration/LegacySettingsMigration.cs`,在 `AiSettingsStore.LoadAsync`
里探测 JSON 形状,折完立即回写):按 Base URL 分组(忽略大小写、尾斜杠与尾部 `/v1`),
同组并成一个供应商;旧接入 id **原样成为模型 id**,所以旧的 `ActiveProviderId` 直接搬成
`ActiveModelId`,聊天面板不用重选;组内第一把 Key 提到供应商名下,同 Key 的模型删掉自己
那份改继承、不同的标 `HasOwnApiKey`;组内地址与供应商地址不完全一致的模型记
`BaseUrlOverride` —— 迁移前后请求打到哪儿一个字节都不变。

## Agent 工具箱

`Agent/AgentToolbox.cs` 把插件能力包成模型可调用的工具。**计划模式只给只读那批**。

| 只读(不走审批) | 会动东西(一律审批) |
| --- | --- |
| `list_sessions` 列出全部会话 | `run_command` 单机执行 |
| `read_terminal` 读终端尾部 | `run_on_sessions` **多机并行执行** |
| `search_terminal` 在滚回里搜(只带回命中行) | `write_remote_file` 整份覆写 |
| `read_remote_file` 读远端小文件 | `patch_remote_file` **只换一段** |
| `list_remote_directory` 列目录 | `make_remote_directory` 建目录(幂等) |
| `stat_remote_path` 查存在/大小/时间 | `rename_remote_path` 改名 / 移动(备份用) |
| `get_working_directory` 取 cwd | `upload_local_file` 本机 → 服务器 |
| `system_overview` 一次取回系统概览 | `download_remote_file` 服务器 → 本机 |
| `web_search` 检索网络(见「网页检索」) | `write_terminal` 敲进用户终端 |
| `web_fetch` 取一个网页转成文本 | |

三条设计约定:

* **每个工具都接受可选的 `session_id`**(不传 = 用户在面板上选中的那台)。这一条是"多主机"的前提 ——
  `list_sessions` 把 id 交给模型、却没有工具肯收的话,模型就只能对着当前选中的那一台干活,
  而同时管着好几台服务器恰恰是 SSH 客户端区别于普通聊天框的地方。认不出的 id 会当场回一句
  "去 list_sessions 拿 id",而不是抛异常 —— 模型看到前者会自己纠正,看到后者往往就地放弃。
* **高频只读动作做成专用工具,而不是让模型拼一条命令交给 `ReadOnlyCommand` 去猜。**
  专用工具结构上不可能有副作用,所以不必打断用户;返回的东西也更小更准。
  `system_overview` 就是这个思路:命令写死在代码里,一次取回内核/发行版/负载/内存/磁盘,
  省掉五轮 `run_command` 和五次审批。
* **改文件优先 `patch_remote_file`。** `write_remote_file` 要求回传整份内容,改一行 nginx.conf
  就要把几百行原样送回来 —— 费 token,而且模型复述长文本时丢内容是真实风险。
  patch 要求 `old_text` 在文件里**恰好出现一次**:多处匹配意味着模型并不确定自己在改哪一处,
  那时挑第一处替换是运维场景里最容易出事故的"聪明"。

## 附件

两条来路,别混:

* **`@` 引用**读的是**远端服务器**上的文件(走 SFTP,补全远端路径)。
* **拖进面板 / 点工具条左边的 `+`** 是**本机**文件:图片(png/jpg/gif/webp/bmp)作为
  **视觉输入**发给模型,文本文件把内容附在正文之后。单个 5MB、一条消息最多 4 个。

图片进不了纯文本的历史,那里只留一行文件名。

## 数据存储位置

插件不直连数据库 —— 一切持久化走 SDK 能力,宿主统一落 **SonnetDB** 的
`plugin_data` 集合(按插件 id 命名空间隔离,卸载自动清除):

| 数据 | 能力 | SonnetDB 落点 |
| --- | --- | --- |
| 供应商与模型配置/开关/系统提示词(`AiSettings` 整体 JSON) | `Storage` | 文档 `velashell.ai\|kv\|settings` |
| API Key(DPAPI 加密后入库) | `Secrets` | 文档 `velashell.ai\|secret\|apikey:<供应商 id,或带独立 Key 的模型 id>` |
| 订阅登录的令牌组(access / refresh / 过期时刻 / 账号,整组 JSON 后加密) | `Secrets` | 文档 `velashell.ai\|secret\|oauth:<供应商 id>` |
| 历史会话的每条消息 | `TimeSeries` | measurement `chat_messages`(标签 `conv` = 会话 id,时间即消息时刻) |
| 历史会话摘要(标题/起止时刻/条数) | `TimeSeries` | measurement `chat_sessions`,每个会话**一个**点 |

会话摘要刻意把时间戳固定为**会话创建时刻**,于是每次更新都命中「同序列同时间戳 = 覆盖」这条
时序语义,天然只保留最新一份,不必先删后写(最后更新时刻另存 `updated` 字段用于排序)。
时序能力不可用时(headless / 无数据库的宿主)整体降级:写入静默跳过,聊天照常工作,只是不留历史。

## 上下文、历史与消息操作

**上下文快满时自动压缩**(`Chat/ContextCompactor.cs`,主流的滚动摘要做法):
估算用量越过窗口的 **75%** 就先做一次压缩 —— 把早期对话交给模型折成一段**事实摘要**
(目标、已确认的事实、做过什么、已定的结论、还没解决的),**近 6 轮保持原文**,
折完落回窗口的 45% 左右,免得压完一轮又立刻触发。摘要以一条 user 消息的身份排在系统提示词之后。

* **滚动**:每次压缩把<i>上一版摘要</i>连同新折进来的消息一起重写,摘要本身不会越滚越长。
* **切口只落在用户消息上**,否则会把工具调用和它的结果拆开。
* 提示词刻意要 **事实清单而不是叙述**,并要求路径/主机名/报错串**原样保留** ——
  运维排查里真正需要留住的就是这些。
* 压缩**看得见**:消息流里留一道分隔条(点开可读摘要原文),状态行显示"正在压缩早期上下文…"。
* 摘要**随会话持久化**(存在 `chat_meta` 的 -1 号槽位),翻回旧会话不会一进来就重压一次。
  会话被编辑/删除截断过则整个作废重压 —— 宁可多花一次,也不能让模型读到对不上号的摘要。
* 触发时多花一次请求(不带工具、非流式、输出上限 700),用量计入会话累计。
  设置页可关(关掉就退回下面那套裁剪);需要接入里填了"最大输入 tokens"才会生效。

**装不下时的兜底裁剪**(`Chat/RequestContext.cs`,纯函数、可单测):压缩失败或被关掉时,
装配阶段仍会从最早的往后丢,**只在用户消息处下刀**
(从 assistant 或工具结果中间切会留下没有来由的半截上下文,更糟的是把工具调用和它的结果拆开);
末尾若干条永远保住。上下文窗口填 0(未知)时**不裁** —— 宁可让服务端报超长,也不擅自丢东西。
丢了多少条会写进用量的悬停提示,不悄悄发生。

同一处还负责**丢掉落单的工具调用/结果**。这不是洁癖,是两家协议都会直接报错的东西:
OpenAI Responses 回 `400 No tool call found for tool output with call_id …`,
Anthropic 回 `tool_use ids must have corresponding tool_result`。两个方向在正常使用里都会出现 ——
**结果落单**是裁剪顶到 `AlwaysKeep` 硬底线、切点停在半轮中间("只在用户消息处切"拦不住它);
**调用落单**是模型发起调用后用户按了停止,工具从未执行。做法是先扫一遍窗口内的 call/result id,
把落单的那一半从 `Contents` 里摘掉(整条空了就跳过),比整轮丢弃保住的上下文多。

同一处还保证**发出去的第一条是 user**:Anthropic 协议要求 messages 以 user 打头
(system 走独立字段),否则整个请求 400。这和落单工具内容是同一个根 —— 切点本来只落在
用户消息上,但 `AlwaysKeep` 是硬底线,顶到它时会停在半轮中间。摘要在场时不必管:
摘要本身就是一条 user,由它打头。

同一处还负责**把相邻同角色的消息并成一条**。起因是个实测出来的坑:用户按停之后,那条没有得到
回复的 user 消息会留在历史里,下一轮就是两条挨着的 user;抓包确认 Anthropic 适配器**不会**
替你合并、原样发两条,而该协议要求角色交替。收尾处另有一道:半截回复照样进历史(用户看得见,
模型也该知道自己说过什么),一个字都没吐则把那条 user 撤回来。

**回复里的链接点得开**(`Ui/ChatPanelView.Links.cs`,挂 `MarkdownRenderer.LinkClick`):
模型常把服务器上的文件写成 `[名字](/root/xxx.md)`,那既不是网址也不是本机文件,默认点了没反应。
现在按目标形状分三条路:网址交给浏览器,本机绝对路径交给系统默认程序,
**以 `/` 开头的远端绝对路径经 SFTP 下载**(先 `Stat` 确认存在、不是目录、不超 64MB,
再让用户挑存到哪儿 —— 悄悄落到某个目录里人一样找不着)。

**历史存的不只是文本**:思考过程、工具调用、模型名、耗时另存一张 `chat_meta` 表
(按 `conv + seq` 与消息对应)。翻回旧会话时 Agent 做过什么一览无余,导出的 Markdown 里也带着。

> 为什么另开一张表:宿主的 `EnsureMeasurementAsync` 对已存在的 measurement **原样沿用、不迁移**,
> 给旧表加字段对老用户是静默失效。

**消息操作**:用户消息悬停出现「编辑重发」「删除」,最后一条回复上有「重新生成」。
三者共用一条语义 —— **回到某一点,后面的全部作废**(对话有前后依赖,改中间却留着后面的回答
只会得到自相矛盾的记录)。截断会连带重写库里那段会话;重写**沿用原来的 seq**,
幸存消息的附加信息才还挂得上,新消息则从旧的最大序号之后续,不复用可能挂着孤儿元数据的旧号。

**长会话不再越聊越卡**:可视树里最多常驻 40 条消息,更早的收进一枚「显示更早的 N 条」横幅
(只是摘下来存着,点一下原样挂回);回放历史分帧建,不再一口气把 UI 线程按住几秒。
没上 ItemsRepeater 虚拟化是权衡后的选择 —— 气泡是命令式构造、流式过程中持续自更新的活控件,
改造成数据模型+模板还要处理容器回收与变高元素下的粘底滚动,风险远大于收益。

## 界面

视觉语言与宿主对齐(DESIGN.md §5.1):主操作用宿主的 `VelaAccentPillButtonTheme`
强调药丸(发送/保存/批准),次操作用 `VelaOutlineButtonTheme` 描边(新会话/测试),
危险操作描边换 `VelaError`(停止/删除/拒绝);输入区仿宿主 input-affix 容器
(聚焦强调描边);图标复用宿主 `Icon.*` lucide 几何;历史为自带
ControlTheme 的芯片开关(`Ui/AiTheme.axaml`,隔离进程下仅令牌也能退化可用)。
顶栏右侧 **新会话** 按钮:终止当前请求并开始全新对话;**设置**(⚙)与**配置工具**各开一个
独立窗口(见下);**历史**开关列出既往会话(存于插件私有
时序库,见上),可按标题搜索、重命名、导出 Markdown(带思考与工具调用,是完整的排查记录)或删除。输入框支持 **↑↓ 调出历史输入**,以及 **`@` 引用服务器文件**
(补全远端路径,发送时把引用展开为真实路径交给模型)。

「AI:打开聊天(标签页)」以 `PanelPlacement.Right` 打开 —— 面板落在标签区**最右侧独立一栏**
(VSCode 里 Copilot 聊天面板的位置),终端留在左边不被顶掉。落位走的是宿主的拖放停靠路径,
与用户手动拖过去等价:随时可以再拖回标签条或换到别的分栏。初始宽度由设置页的
**侧栏宽度(%)**(默认 30,取值 15–85)决定,只影响"打开时多宽" —— 之后拖分割条随时可改,
拖出来的宽度不回写配置。

弱化文字的取色**有意偏离 DESIGN.md 的默认**(那里写的是"占位符/弱提示用 `VelaTextMuted`"):
面板里这些"淡字"其实全是要读的信息 —— 时刻、模型、token 用量、思考区标题、设置页的说明,
`VelaTextMuted`(#545B76)压在面板底色上只有 ~2.5:1 的对比度,用户实测反馈看不清。
现在:元信息(时刻 · 模型 / 用量)= `VelaTextSecondary` 11px,其余弱化文字与输入占位提示
= `VelaTextTertiary`(占位 12px,其余 10–11px)。**别改回 Muted。**

版式以 GitHub Copilot 的聊天面板为蓝本:

* **决定"这条消息怎么发"的东西挨着输入框** —— 模型选择、**对话模式**与发送按钮同处输入框那个
  描边容器内(`InputToolbar`);容器正下方一条细行(`InputStatusBar`)左边是**审批方式**、
  右边是 token 用量。顶栏只留 SSH 会话选择与历史/新会话/设置/配置工具。
* **对话模式三选一**(`ModeCombo`,对齐 Copilot):**对话**(不给工具,纯问答)/
  **计划**(只给只读工具,先说怎么做)/ **Agent**(全部工具)。计划模式的"只读"是
  `CreateTools(ChatMode.Plan)` 在构建工具列表时就把写工具滤掉了 —— 模型看不到就调不到,
  不是靠提示词自觉。
* **审批方式也是选择项**(`ApprovalCombo`):**每次询问** / **只读自动** / **全部自动**。
  纯对话模式下这一项直接隐藏(没有工具,摆着只会让人以为还有什么能被自动执行);
  这一行高度写死 20,显隐不改变行高,否则整个输入区会跳一下。
* **输入区默认就有多行余量**(编辑器 `MinHeight=66`,到 220 封顶后内部滚动),写长提示词
  不必先把框撑开。
* **用量**:知道上下文窗口(接入配置里的"最大输入 tokens")时显示 `12k/128k · 9%`,
  否则显示累计的 `↑输入 ↓输出`;命中提示词缓存时紧跟着追一段 `· 缓存 80%`(没命中就不占地方)。
  完整明细(本轮上下文、缓存命中数、会话累计、缓存读写累计、思考 tokens、两个上限)
  在悬停提示里 —— 工具条那点宽度经不起铺开。

  **Anthropic 会自动打缓存断点**(`Chat/PromptCache.cs`,接入配置里可关):每轮在
  ①系统提示词末尾、②本轮最后一条消息末尾各打一个 `cache_control` —— 这一轮写缓存,
  下一轮历史只在其后追加,整段前缀命中(`@` 引用整份文件时省的就是这些 token)。
  协议上限 4 个断点,而 `_history` 里的 `AIContent` 是**跨轮复用**的,标记打在对象上,
  所以每轮必须先清再打,否则几轮就撞上限直接报错(有测试连打 8 轮守着)。
  短于最小可缓存长度(约 1024 token)的前缀服务端直接忽略标记,不缓存也不加价。

  命中率 = `CachedInputTokenCount / InputTokenCount`,两家协议同一个式子:OpenAI 的
  `prompt_tokens` 本就含 `cached_tokens`;Anthropic 的 `input_tokens` 原本**不**含缓存,
  但适配器把 `cache_read` 与 `cache_creation` 都并进了 `InputTokenCount`(实测 200+800+120=1120),
  口径正好被抹平。缓存"写入"只有 Anthropic 报(`AdditionalCounts["CacheCreationInputTokens"]`,
  它单独计费),只进悬停明细。
* **思考过程**:模型吐 `TextReasoningContent` 时出现思考卡片,**默认收起**(用户决策)——
  标题一行说明"在想",要看内容自己点开;展开过之后代码就不再插手(收尾也不替他折起来)。
  内容无论展开与否都在持续灌入,展开的瞬间就能看到已到达的部分。
  展开状态下的流式观感靠两点:①节流 80ms(不是 200ms,思考常常只有一两秒);
  ②思考区高度封顶 200,内容超出后**必须自动滚到最新一行**,否则看到的永远是最开头几行,
  明明在流却像卡住。是否要模型输出思考,由接入配置里的"思考过程"档位
  (跟随默认/关闭/低/中/高)控制。两家协议的下发方式不同,差异收在
  `AiSettingsStore.ApplyReasoning` 里:

  | 协议 | 下发方式 |
  | --- | --- |
  | OpenAI(CC / Responses) | `ChatOptions.Reasoning`(适配器翻成 reasoning effort / summary) |
  | Anthropic Messages | `ChatOptions.RawRepresentationFactory` 返回带 `Thinking` 的 `MessageCreateParams` —— **Anthropic 12.40.0 的适配器不认 `ChatOptions.Reasoning`**,只能直接写请求体 |

  **要真的看到思考,档位不能留在"跟随默认"** —— 那等于请求里不带 reasoning 参数,多数模型
  就什么也不返回。选了档位后,OpenAI 兼容协议发的是 `reasoning_effort`(实测抓包确认)。

  **回来的思考文本还有一道兜底**(`Chat/ReasoningPeek.cs`):Chat Completions 协议里思考没有
  标准字段,各家各造一个。实测 M.E.AI 的适配器只认 DeepSeek 那套 `delta.reasoning_content`
  (自动变成 `TextReasoningContent`);OpenRouter 一系的 `delta.reasoning` 它不认,
  那一帧解析出来**一个 AIContent 都没有**,思考就丢了。所以对"空帧"再去 `RawRepresentation`
  的原始 JSON 里翻一遍 `reasoning` / `reasoning_content` / `thinking`(OpenAI SDK 的模型会把
  未映射字段原样留着并在 `ModelReaderWriter` 回写时吐出来)。只有空帧才付这个代价。

  Anthropic 那条路有个实测出来的坑(已抓包核对流式与非流式两条路):`MessageCreateParams` 的
  `required` 成员在 raw 对象里必然有值,而适配器**只覆盖 `Messages`** —— `MaxTokens` 与 `Model`
  以 raw 里的为准,`ChatOptions.MaxOutputTokens` 和 `AsIChatClient(model, maxTokens)` 都被无视。
  所以构造 raw 时必须把真实模型与输出上限一并填进去。思考预算按档位取 2048/4096/16384,
  再夹到协议下限 1024 与"给正文留 1024 余量"之间(协议要求 `max_tokens > budget_tokens`);
  用户把最大输出设得放不下思考时,把这一次请求的上限抬到刚好够,而不是悄悄不思考。
* **回复收尾**:头部补上「助手 · 2 步 · 12.3s」,底部一条细线下是**复制整段回复**按钮与
  「时间 · 模型」。刻意不做点赞/差评,也不显示积分。
* **处理中的输入框跑流光**(`Ui/BorderGlowOverlay.cs`):两枚彗尾沿边框跑圈,盖在输入框上画
  (`IsHitTestVisible=False`,不参与布局),**底下那圈边框的颜色一点不动** —— 焦点态/悬停态照常。

  做法是**用虚线笔描边框**:把"实线段"当成彗尾,逐帧推 `DashStyle.Offset`。
  两枚靠"图案周期 = 半个周长"实现,路径从左上角起笔顺时针走,于是一枚从左上角、
  一枚从右下角出发,天然点对称。

  观感上有三条是反复踩出来的:

  1. 整圈先铺一层 `VelaBorderSecondary` 暗色**轨道**,盖掉底下那圈(聚焦时正是强调色的)边框。
     亮带压在同色边框上会糊成一片。轨道只是画上去的,边框自身的画刷不动,熄灭时无须恢复。
  2. 淡出必须靠**颜色渐变回轨道色**,不能靠透明度。用 alpha 渐隐在深色底上会显出边界,
     多层叠还会出台阶 —— 那是"更丑了"的直接原因。
  3. **就是 1px、没有另铺一层外晕**。对着参考截图逐像素量过:亮线只占一行,上下两行都是纯背景色。
     所谓"光晕"指的是那条平滑的颜色渐变本身 —— 加粗只会变成一条胖模糊的带子(试过,被打回)。
  4. 配方与长度也是量出来的:`轨道 →(96px)→ VelaShellCyan →(80px)→ VelaAccent →(96px)→ 轨道`,
     合计约 272px。代码里按 35% / 30% / 35% 三段表达,与量得的比例一致。

  > 调这类纯观感的东西**别靠脑补**:scratchpad 里有离屏预览的办法(headless + Skia 渲染真实控件到
  > PNG,再逐像素与参考图对数)。前两版都是猜的,都被打回。

  **先试过锥形渐变,不行**:它按<i>角度</i>均匀转,而输入框是扁长方形 —— 同样的角度增量
  落在左右短边上只走几像素、落在上下长边上要跨半个框,光斑于是在两端磨蹭、在长边一闪而过。
  沿路径走才是线速度恒定的,拐角也自然绕过去。
* **输入框上方的建议药丸**(`ChatPanelView.Suggestions.cs`):空会话给三条起手提示(本地文案,
  **不花钱**);一轮答完后额外问一次模型要几条后续提问,点一下直接发。后者每轮多一次很小的请求,
  所以单独有开关(设置页「推荐后续提问」,默认开)。那一问不带工具、不进对话历史、输出上限 120,
  且强制关思考;它的用量计入"会话累计",但**不计入"上一轮上下文"**那个读数 ——
  那个数说的是对话本身占了多少窗口,掺进去会误导。模型很少按格式老实输出,解析按最宽松处理
  (序号/项目符号/引号全洗掉)。

回复以 **Markdown 渲染**,用 [LiveMarkdown.Avalonia](https://github.com/DearVa/LiveMarkdown.Avalonia)
(Apache-2.0;内部仍是 Markdig 解析,但解析放后台线程,并按 `SourceSpan` 脏检查
只更新受影响的节点 —— 流式追加不再重建整段可视树,故本插件不再自建节流)。
覆盖 Markdig `UseAdvancedExtensions()` 全集:标题/强调/行内代码/链接/围栏代码块
(TextMate 语法高亮 + 语言标签 + 复制/换行按钮)/嵌套列表/任务列表/引用/分隔线/
表格,并支持**跨块文本选择**。

皮肤:库自带样式是深色硬编码 + 文档级字号,已在 `ChatPanelView.axaml` 用 Vela 令牌
整体覆盖(主题切换随 `DynamicResource` 自动跟随);代码块头/体底色只在 `CodeBlock`
的 `ControlTemplate` 内引用、选择器够不到,那几个资源键由 `SyncMarkdownSkin()` 写入。
语法高亮配色按明暗切 `DarkPlus`/`LightPlus`。

另外接了三个可选节点扩展(注册见 `Ui/MarkdownSetup.cs`,必须在第一个渲染器构造前完成):

| 扩展 | 语法 | 效果 |
| --- | --- | --- |
| `LiveMarkdown.Avalonia.Mermaid` | ` ```mermaid ` 围栏块 | 24 种图型画成原生 Avalonia 控件,带平移缩放;`Mermaider` 纯托管解析+布局,无浏览器无子进程 |
| `LiveMarkdown.Avalonia.Math` | `$..$` `$$..$$` 及 `\(..\)` `\[..\]` | CSharpMath 排版;后一组反斜杠定界符是多数模型的实际输出,标准 Markdig 认不出 |
| `LiveMarkdown.Avalonia.Svg` | `![](x.svg)` | 给图片管线注册 SVG 解码器(Svg.Controls.Avalonia 后端,非 Skia) |

Mermaid 的配色全部走 `ForegroundColor`/`BorderColor`/`CardBackgroundColor` 三个键,
已由 `SyncMarkdownSkin()` 映射到 Vela 令牌,主题切换自动跟随;**注意 `BorderColor` 被
Mermaid 用作全部线条色,不能挪作背景**,所以代码块标题栏底色改用 `nth-child(1)` 选择器给。
LaTeX 是例外:`MathView` 吃不到类型选择器(基类是泛型,StyleKey 对不上,实测连字面色的
运行期样式都不生效),默认又是黑色,只能由 `ApplyMathColors()` 在每次渲染定稿后就地设。

两项刻意的收口:链接只放行 `http`/`https` 并交宿主浏览器打开;图片加载器**摘掉了
HTTP 处理器**(只留本地文件 / `data:` / 内嵌资源),否则模型回复里的图片 URL 会被
面板直接抓取,等于对任意模型输出开了追踪像素通道。**这也意味着远程 SVG 不会加载** ——
SVG 目前只对 `data:` URI 和本地路径生效;要放开就把 `ConfigureMarkdown()` 里
`HttpAsyncImageLoaderHandler.Shared` 加回处理器数组。

工具调用为**紧凑单行卡片**(状态图标 + 工具名 + 参数摘要),点击展开完整参数与结果;
思考过程同款折叠区(纯文本,不走 Markdown)。

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
  默认**每次询问**(面板内 批准/拒绝 卡片)。审批方式可在输入框下那行改:
  - **每次询问**(默认):每次都问。
  - **只读自动**:仅 `run_command` 且命令**确定无副作用**时免问,写文件/敲终端照问。
    判定见 `Agent/ReadOnlyCommand`,**刻意写得胆小** —— 命令名必须在白名单里,
    整条命令不许出现重定向 / 管道 / 命令分隔符 / 命令替换,也不许带
    `find -delete`、`sed -i`、`curl -o`、`systemctl restart` 这类能把只读命令变成写操作的东西;
    带路径的调用(`./deploy.sh`)一律不认。放过一条该问的命令代价可能是删掉生产数据,
    多问一次只是多点一下鼠标 —— 两者不对称,所以规则宁可过严。
  - **全部自动**:全部免问(有风险)。
- `write_terminal` 之上还有宿主自己的终端回写授权弹窗(四态)。
- `read_terminal` / `list_sessions` / `read_remote_file`(≤256KB)为只读,不需审批。
- `upload_local_file` 把**本机**文件经 SFTP 传到服务器(要审批,单次 ≤32MB)。这条是为 MCP 准备的:
  **MCP 服务器跑在用户自己的机器上**,它产出的文件落在本机(通常是那台服务器配的工作目录),
  既不在 SSH 服务器上、也不在终端的当前目录里。系统提示词里会明说这件事,并把各台 MCP
  服务器的工作目录一并告诉模型 —— 不说清楚它就会把本机产物当成远端路径去汇报(实际发生过:
  xmind MCP 生成的文件被报成 `/root/xxx.xmind`,两边都找不到)。
- 目标会话由面板顶部的会话下拉框选定。
- 审批卡上还有第三个按钮 **「本次会话总是允许」**,但**只对可重复、语义稳定的操作开放**:
  `run_command` 的记忆键到命令名为止(`sudo` 会带上后面那个词),同一次排查里
  `ls`/`cat`/`systemctl` 就不必点十几次;**写远程文件、往终端敲字不给这个选项** ——
  每次目标都不同,记住等于放弃把关。记忆只在内存里,换会话或关面板即失效,不写进配置。

## MCP 服务器(自定义)

MCP 服务器在**自己的窗口**里配(`Ui/McpServersView`),入口是「配置工具」标题行右侧的 **⚙**。
刻意不放在设置页:配好一台服务器,下一步必然是挑它的哪些工具给模型用,那份勾选列表就在
点开它的那个窗口里,加/存/测试都会当场重建它;也刻意不压在勾选列表上面 ——
它是一整套左列表右表单,叠上去会把那一页挤得没法看。两种连接方式:

- **Stdio(本地进程)**:命令(`npx` / `uvx` / 任意可执行文件)+ 单行参数
  (含空格片段用引号)+ 可选工作目录与 `KEY=VALUE` 环境变量;
  Windows 下脚本命令的 cmd 包装由 SDK 处理。
  **工作目录留空 = `~/.velashell/mcp`**(与日志目录 `~/.velashell/logs` 同一棵树,单独一个子目录免得 MCP 产物和宿主文件混放),`~` 前缀按主目录展开,
  相对路径挂在默认目录下,起进程前会把目录建出来(`McpWorkspace`)。以前是"空 = 继承宿主进程的当前目录、
  `~` 原样下传"—— 前者让人不知道 MCP 产出的文件落到了哪儿,后者 `Process.Start` 不认,直接"目录名称无效"起不来。
- **HTTP(远端)**:端点 URL + 可选 `Name: Value` 请求头(鉴权令牌等);
  Streamable HTTP / SSE 自动探测。

行为约定(`Agent/McpManager`):

- 仅 **Agent 模式**下连接**启用的**服务器;连接按配置指纹缓存复用,
  失败退避 30s 再重试,单服务器失败不影响其余(错误显示在状态行)。
- 工具名加 `服务器名_` 前缀防冲突(清洗为 `[A-Za-z0-9_-]`,截断至 64)。
- 按 MCP `readOnlyHint` 注解区分:只读工具直接执行;**非只读工具走与
  `run_command` 相同的审批卡**,审批方式同样生效(「只读自动」只覆盖 `run_command`,
  MCP 的非只读工具照问)。
- **「测试」顺带把工具库拉下来**:连都连上了,再让人去下面点一次"更新工具库"是多余的。
  测试成功即把结果写进 `KnownTools` + `ToolsRefreshedAt`,下方分组立刻长出那些勾选项。
  等待期间用户可能已切走选择,回填前要核对 id。面板关闭时全部断开。

### 配置工具(独立窗口)

顶栏的 **配置工具** 按钮开一个独立窗口(`Ui/ToolPickerView`),按来源分组列出全部工具 ——
内置七个 + 每台 MCP 服务器一组 —— 逐个勾选是否暴露给模型;窗口**标题栏**的 ⚙(`PanelOptions.TitleActions`)通向
MCP 服务器配置窗口(见上),那边改完这边当场重建。每组标题行可点折叠(箭头 + 标题 + "已勾 n/m"),
**MCP 组默认折起、内置组默认展开** —— 一台服务器动辄几十个工具,全展开这页长得没法看;折叠状态
只活在窗口里,重建不丢、关窗不存。
有些 MCP 一口气给几十个工具,全塞给模型既占上下文又容易被误调。每台服务器旁边一枚
**更新工具库**:连上去把它现在提供的工具重新拉一遍(`McpManager.RefreshToolsAsync`),
结果连同刷新时刻存进配置,下次开窗口不必再连。

勾选状态**存的是"没勾的那些"**(`McpServerConfig.DisabledTools` / `AiSettings.DisabledBuiltinTools`):
服务器以后新增了工具,默认就是可用的,而不是因为"不在已保存的白名单里"被静默屏蔽掉。

## 四个配置窗口

设置(⚙)、连接供应商、配置工具、MCP 服务器各是一个**独立窗口**(`Ui/ChatPanelView.Dialogs.cs`),
不再占用面板中间那块与聊天流三选一。理由:面板常常只有三成宽(侧栏),设置页那些
两列三列的行在那个宽度里铺不开;改设置时也不该看不见对话。

**窗体走 SDK 的 `PanelDisplayMode.Window`,插件不自己 `new Window`。** 那样拿到的是宿主的
自绘卡片窗口(透明窗 + 8px 圆角 + 自绘标题栏与缩放抓取区),和链路追踪、资源监视、
任务管理器同一套规格。插件自开原生标题栏的窗口跟整体风格打架;而自绘那套还要配
Win32 的 DWM 调用才不掉圆角、不留启动残影 —— 那是宿主的事,插件够不着也不该重造。
非模态(宿主用 `Show(owner)`):模态会把整个 VelaShell 锁住,而改设置的时候人往往
正想回去看一眼终端。面板关闭时这几个窗口一并带走。

**Esc 关窗**,与宿主其它窗口一致。监听挂在**内容控件**上而不是让宿主对所有插件面板统一处理 ——
聊天面板也能以窗口形态打开,那里 Esc 必须留给输入框,正打着字被关掉窗口很糟。

已经开着时再点一次按钮走 `IPluginPanel.ActivateAsync()` 把窗口带到前面(窗口置前 /
停靠形态选中那个标签),而不是重复开一个,也不是什么都不做 —— 后者看起来就像按钮坏了。

**「连接供应商」为什么另开一扇窗**:这一页是"挑一家",设置页是"调一家",两件事的信息密度
完全不同 —— 目录要留出说明、示例模型和状态灯的地方,挤进设置页那条 220 宽的侧栏里就只剩
一列名字了(那正是它取代的东西:原先左下角那个光秃秃的预设下拉)。窗口关掉时,
还挂着的那次登录(环回监听 / 设备码轮询)一并取消,不留后台空转。

这几个窗口共用一套版式规则(`Ui/DialogStyles.axaml`,谁用谁 `StyleInclude`):
以前这些规则由插件自己的对话框外壳下发,窗体换成宿主的卡片之后就没了着落。
那一套是**照抄宿主设置页**(`src/VelaShell/Views/SettingsView.axaml`)的:分节标题、
`ListBox.nav` 左侧导航、分隔线,连选中态都压成 `VelaBgActive` + 强调色文字 ——
Fluent 默认给的是一整块高饱和蓝,在这套暗色令牌里跳得厉害。勾选框用插件自建的
`AiCheckBoxTheme`,同理。

**内容自己带内边距**(设置页右栏 24/20,另两个窗口 20/16)。宿主的 `PanelContent`
是零内边距的,聊天面板要的就是这个;配置这类表单不补一层就会直接贴着窗口边框。

设置页的版式:左边一条 `VelaBgSidebar` 导航贴着窗口边(左下角跟着卡片走内圆角 7 ——
照抄外框的 8 会让方角背景盖掉那段弧线的描边,看起来像断线),左栏是"供应商 › 模型"两层列表
(供应商行加粗、模型行缩进一档,底部「新增模型」挂在当前选中的供应商下、「新增供应商」走预设下拉),
右边表单随选中的层切换:选中供应商是**供应商**一节(名称 / 基地址 / 默认协议 / 共用 Key),选中模型是
**模型 / 模型能力 / 采样、计费与提示词** 三节。每节一个标题 + 一张描边卡片;
节标题贴着自己那张卡片(上 22 下 8),不然分不清标题归上面还是归下面。

**侧栏宽度没有设置项** —— 它由用户拖分割条决定,拖完就记住,下次打开还是那个宽度
(默认 30%)。让人去填一个百分比,不如直接把他拖出来的结果记下来。
实现:宿主在拖动**结束**时通知一次 `IPluginPanel.PlacementRatioChanged`
(`DockWorkspaceControl` 那时才把 star 值回写成 `Proportion`,所以插件直接落盘、不必防抖),
`AiPlugin.RememberPanelWidthAsync` 夹到 15–85 存进 `AiSettings.PanelWidthPercent`,
下次打开经 `PanelOptions.PlacementRatio` 传回去。值没变就不写库。

> 改这类纯观感的东西**先离屏渲染出来看**,别靠脑补(教训见 `Ui/BorderGlowOverlay.cs` 的注释):
> 建一个引用本插件 + `VelaShell.Controls` 的小程序,`UseSkia().UseHeadless(UseHeadlessDrawing=false)`,
> 把视图挂进 `Window` 后 `CaptureRenderedFrame().Save(png)`,按真实窗口尺寸看版式。
> **调色板要连 `ThemeDictionaries` 一起搭**(`Themes/DarkTheme.axaml` / `LightTheme.axaml`)——
> `VelaError` 这类令牌只在那里面,漏了的话文字回落成默认色、图标压根不描边,预览就会骗人。

设置窗口的 **保存 / 测试 / 删除 落在表单最末、靠右**,上方一道分隔线跟配置项断开,
跟着内容滚(状态提示在同一行左侧)。不钉成常驻横栏 —— 那会一直占着高度,而这页大多数时候在读、在填。

**全局设置**(系统提示词 / 上下文压缩 / 后续提问)不在这页:它原来是最底下一节,得跟着长表单滚到底才找得到,
而且「保存」把它和当前模型一起存,让人分不清归属。现在从模型配置窗口**标题栏、最小化键左侧的 ⚙**
(`PanelOptions.TitleActions`,与主窗体标题栏那排工具按钮同一版式)进 `Ui/GlobalSettingsView`,
自己一个小窗口、自己一个保存键;模型配置窗口关掉时把它一并收走。聊天面板顶栏那枚进模型配置的按钮
因此从齿轮换成了 **brain**(`AiIcon.brain`,内联几何),免得两个齿轮撞含义。

因此设置窗口**不要底部的关闭栏**(`PluginDialog` 的 `closeText` 留空即整条不出现):
表单末尾已经有操作行,底下再压一条只有"关闭"的横栏是白占地方,关窗用系统标题栏的 × 即可。
「配置工具」窗口没有自己的操作行(勾一下就存),所以那边仍保留「确定」。

## 测试

[`tests/VelaShell.Plugin.Ai.Tests`](../../tests/VelaShell.Plugin.Ai.Tests):基于
`VelaShell.PluginSdk.Testing` 替身,覆盖工具箱审批闸门与能力桥接语义、设置/机密存取、
MCP 配置解析、`@` 文件引用语法、会话历史的时序持久化,以及聊天面板的 headless 装载
(XAML 真装载一次,顺带守住"宿主令牌缺席时也能装载"这条隔离进程首帧的前提)。
