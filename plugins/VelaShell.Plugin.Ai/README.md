# VelaShell.Plugin.Ai —— AI 助手插件

多提供商 AI 助手:在 VelaShell 内与大模型流式对话。三种对话模式 ——
**对话**(纯问答)/ **计划**(只给只读工具)/ **Agent**(经审批调用工具:
读终端输出、执行远程命令、读远程文件、写终端),还可接入用户自定义的
**MCP 服务器**扩展工具集。

## 支持的接入

| 提供商 | 线协议 | 说明 |
| --- | --- | --- |
| OpenAI | Responses(流式)或 Chat Completions | 官方 API,二选一 |
| Anthropic Claude | Anthropic Messages(流式) | 官方 API |
| xAI Grok | OpenAI Chat Completions 兼容 | `https://api.x.ai/v1` |
| Ollama(本地自部署) | OpenAI Chat Completions 兼容 | `http://localhost:11434/v1`,无需 Key |
| 第三方中转站 | OpenAI 兼容 或 Anthropic 兼容 | 自填 Base URL + API Key |

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
(Windows 上为 DPAPI 包裹的本地密钥),绝不明文落盘。解出来的密钥在进程内按接入缓存,
写入/删除时失效 —— 否则每发一条消息都要走一遍"读库 + DPAPI 解包"。

每个接入还可单独配:采样参数(temperature / top_p / 停止序列,留空 = 不发该参数)、
**专用系统提示词**(盖过全局那份)、以及三档**单价**(输入 / 输出 / 缓存命中,每百万 token)。
填了单价,用量的悬停提示里就会给出这段会话的花费估算;留 0 就不估。

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
| 接入配置/开关/系统提示词(`AiSettings` 整体 JSON) | `Storage` | 文档 `velashell.ai\|kv\|settings` |
| 各接入的 API Key(DPAPI 加密后入库) | `Secrets` | 文档 `velashell.ai\|secret\|apikey:<providerId>` |
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

同一处还负责**把相邻同角色的消息并成一条**。起因是个实测出来的坑:用户按停之后,那条没有得到
回复的 user 消息会留在历史里,下一轮就是两条挨着的 user;抓包确认 Anthropic 适配器**不会**
替你合并、原样发两条,而该协议要求角色交替。收尾处另有一道:半截回复照样进历史(用户看得见,
模型也该知道自己说过什么),一个字都没吐则把那条 user 撤回来。

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
- 目标会话由面板顶部的会话下拉框选定。
- 审批卡上还有第三个按钮 **「本次会话总是允许」**,但**只对可重复、语义稳定的操作开放**:
  `run_command` 的记忆键到命令名为止(`sudo` 会带上后面那个词),同一次排查里
  `ls`/`cat`/`systemctl` 就不必点十几次;**写远程文件、往终端敲字不给这个选项** ——
  每次目标都不同,记住等于放弃把关。记忆只在内存里,换会话或关面板即失效,不写进配置。

## MCP 服务器(自定义)

在**「配置工具」窗口的上半部分**(`Ui/McpServersView`)添加任意多个 MCP 服务器
—— 刻意不放在设置页:配好一台服务器,下一步必然是挑它的哪些工具给模型用,
而那份勾选列表就在同一个窗口的正下方,加/存/测试都会当场重建它。两种连接方式:

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
  `run_command` 相同的审批卡**,审批方式同样生效(「只读自动」只覆盖 `run_command`,
  MCP 的非只读工具照问)。
- **「测试」顺带把工具库拉下来**:连都连上了,再让人去下面点一次"更新工具库"是多余的。
  测试成功即把结果写进 `KnownTools` + `ToolsRefreshedAt`,下方分组立刻长出那些勾选项。
  等待期间用户可能已切走选择,回填前要核对 id。面板关闭时全部断开。

### 配置工具(独立窗口)

顶栏的 **配置工具** 按钮开一个独立窗口(`Ui/ToolPickerView`),上半是 MCP 服务器编辑(见上),
下半按来源分组列出全部工具 —— 内置七个 + 每台 MCP 服务器一组 —— 逐个勾选是否暴露给模型。
有些 MCP 一口气给几十个工具,全塞给模型既占上下文又容易被误调。每台服务器旁边一枚
**更新工具库**:连上去把它现在提供的工具重新拉一遍(`McpManager.RefreshToolsAsync`),
结果连同刷新时刻存进配置,下次开窗口不必再连。

勾选状态**存的是"没勾的那些"**(`McpServerConfig.DisabledTools` / `AiSettings.DisabledBuiltinTools`):
服务器以后新增了工具,默认就是可用的,而不是因为"不在已保存的白名单里"被静默屏蔽掉。

## 设置窗口

设置(⚙)与配置工具都是**独立窗口**(`Ui/PluginDialog` + `Ui/ChatPanelView.Dialogs.cs`),
不再占用面板中间那块与聊天流三选一。理由:面板常常只有三成宽(侧栏),设置页那些
两列三列的行在那个宽度里铺不开;改设置时也不该看不见对话。窗口用 `Show(owner)` 而非
`ShowDialog` —— 模态会把整个 VelaShell 锁住,而改设置的时候人往往正想回去看一眼终端。
标题栏用系统的(自绘窗体是宿主的事,插件不该也无法复用),内容区配色全走 Vela 令牌,
明暗主题跟着宿主走。面板关闭时两个窗口一并带走。

设置窗口的 **保存 / 测试 / 删除 落在表单最末、靠右**,上方一道分隔线跟配置项断开,
跟着内容滚(状态提示在同一行左侧)。两点取舍:①原先这排按钮夹在接入字段与全局设置
(系统提示词、上下文压缩、侧栏宽度)之间,而「保存」其实把下面那几项一并存了 ——
摆在中间就像它们不归它管,所以必须排在全部字段之后;②不钉成常驻横栏 ——
那会一直占着高度,而这页大多数时候在读、在填。

因此设置窗口**不要底部的关闭栏**(`PluginDialog` 的 `closeText` 留空即整条不出现):
表单末尾已经有操作行,底下再压一条只有"关闭"的横栏是白占地方,关窗用系统标题栏的 × 即可。
「配置工具」窗口没有自己的操作行(勾一下就存),所以那边仍保留「确定」。

## 测试

[`tests/VelaShell.Plugin.Ai.Tests`](../../tests/VelaShell.Plugin.Ai.Tests):基于
`VelaShell.PluginSdk.Testing` 替身,覆盖工具箱审批闸门与能力桥接语义、设置/机密存取、
MCP 配置解析、`@` 文件引用语法、会话历史的时序持久化,以及聊天面板的 headless 装载
(XAML 真装载一次,顺带守住"宿主令牌缺席时也能装载"这条隔离进程首帧的前提)。
