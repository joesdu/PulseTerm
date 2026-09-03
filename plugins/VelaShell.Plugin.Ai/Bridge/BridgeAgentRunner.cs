using System.Text;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Agent.Web;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>一轮跑完的结果。</summary>
/// <param name="Text">给 IM 的最终回复。</param>
/// <param name="Model">用的模型(日志与页脚)。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="ToolCalls">这一轮调了几次工具。</param>
public readonly record struct BridgeTurn(string Text, string Model, TimeSpan Elapsed, int ToolCalls);

/// <summary>
/// 无头的 agent 回合:没有气泡、没有状态行,只有"一段文本进、一段文本出"。
/// </summary>
/// <remarks>
/// <b>为什么不复用聊天面板那条路。</b><c>ChatPanelView.SendAsync</c> 把装配、流式渲染、
/// 审批卡片、插话与压缩缝在一起,每一步都直接写 UI 控件;把它抽成"界面无关"要动的是
/// 那个 2500 行文件的骨架,风险远大于在旁边并排写一条只做桥接需要的路。
/// 真正值钱的零件 —— <see cref="AgentToolbox" />、<see cref="ContextBuilder" />、
/// <see cref="AiSettingsStore" />、<see cref="McpManager" />、<see cref="ChatHistoryStore" /> ——
/// 本来就都是界面无关的,这里直接拿来用,重复的只有编排这一层。
/// </remarks>
public sealed class BridgeAgentRunner(
    IPluginContext context,
    AiSettingsStore store,
    ChatHistoryStore history,
    McpManager mcp,
    ChannelHub? hub = null)
{
    /// <summary>函数调用循环的单轮上限。IM 那头没人盯着,给得比面板保守一点。</summary>
    private const int MaxToolIterations = 20;

    /// <summary>
    /// 跑一轮。<paramref name="progress" /> 每拿到一批增量就被调一次(参数是<b>累计</b>文本),
    /// 由调用方决定是改同一条消息还是攒到最后再发。
    /// </summary>
    /// <param name="conversation">这个聊天的状态(上下文、绑定的机器、正在跑的那一轮)。</param>
    /// <param name="bridge">桥接的全局设置。</param>
    /// <param name="message">触发这一轮的那条消息。</param>
    /// <param name="approve">危险操作的审批回调。</param>
    /// <param name="loc">界面语言。</param>
    /// <param name="progress">增量回调(参数是<b>累计</b>文本)。</param>
    /// <param name="grant">
    /// 这个聊天的授权(范围 / 挡位 / 审批)。<see langword="null" /> = 完全跟随全局且不限范围 ——
    /// 这也是升级前那些聊天的取值,所以老配置的行为逐字不变。
    /// </param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<BridgeTurn> RunAsync(
        BridgeConversation conversation,
        BridgeSettings bridge,
        InboundMessage message,
        Func<ApprovalRequest, Task<bool>> approve,
        Loc loc,
        Action<string>? progress,
        ChatGrant? grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(message);

        long startedAt = Environment.TickCount64;
        // AI 设置**每轮现读**,不吃桥接启动时的那份快照。
        //
        // 踩过:用户在设置窗口登录了订阅制供应商(OpenAI Codex),面板立刻好用,
        // 而桥接手里那份 provider 还是登录之前的形态(Auth 仍是 ApiKey、没有 OAuth 配置),
        // 于是 ResolveCredentialAsync 走了"取 API Key"那条岔路,把一个空 Key 发了出去 ——
        // 群里看到的是 401「Could not parse your authentication token」,
        // 而同一刻聊天面板一切正常,极难往"快照过期"上想。
        //
        // 代价是每轮多读一次 JSON,与一次模型调用比可以忽略;换来的是"面板里改了什么,
        // 桥接下一句就跟上"——换模型、重新登录、改 MCP 配置,都不必再去重启桥接。
        AiSettings ai = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (ResolveModel(ai, bridge) is not { } model)
        {
            return new BridgeTurn(loc["BridgeNoModel"], "", TimeSpan.Zero, 0);
        }
        ChatMode mode = conversation.ModeOverride ?? grant?.Mode ?? bridge.Mode;
        ApprovalMode approval = grant?.Approval ?? bridge.Approval;
        // 不限范围时这里是 null,于是"全部"与"没有范围这回事"走同一条路(见 SessionScope.Resolve)
        ISessionScope? scope = grant?.Scope.Resolve(context);

        // 目标服务器在**回合开始时**解析一次:工具箱要的是同步的 id 提供者,
        // 而"这一轮在哪台机器上干活"本来就不该跑到一半换人。
        // 带上范围:授权之后用户可能把这台机器移出了分组,而绑定还留着。
        SessionInfo? session = conversation.BoundTarget.Length > 0
            ? await SessionTargets.ResolveAsync(context, conversation.BoundTarget, cancellationToken, scope)
                .ConfigureAwait(false)
            : null;

        var toolbox = new AgentToolbox(context)
        {
            SessionIdProvider = () => session?.SessionId,
            ApprovalHandler = approve,
            Approval = approval,
            Scope = scope,
            // 落点就是这条对话所在的聊天。渠道发不了文件时保持 null —— 那样 send_file
            // 压根不注册,而不是摆一个永远失败的工具让模型反复去试。
            FileSender = hub is { } channels && channels.CapabilitiesOf(conversation.ChannelId).MaxFileBytes > 0
                ? (path, token) => channels.SendFileAsync(conversation.ChannelId, conversation.Reply, path, token)
                : null,
            DisabledTools = new HashSet<string>(
                (ai.DisabledBuiltinTools ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase),
            WebSearch = ai.WebSearch
        };

        IChatClient client = await store.CreateClientAsync(model, cancellationToken: cancellationToken).ConfigureAwait(false);
        var options = new ChatOptions
        {
            MaxOutputTokens = model.MaxTokens,
            Temperature = model.Temperature,
            TopP = model.TopP
        };
        AiSettingsStore.ApplyReasoning(options, model);
        // 这一家端点不认的参数在这儿摘掉。机器人这条路与聊天面板发的是<b>同一批模型</b>,
        // 少了这一步,订阅型的私有后端(ChatGPT 的 Codex 后端最典型)会整轮 400,
        // 而且面板里好好的、只有机器人报错 —— 最难想到是这儿漏了。
        AiSettingsStore.ApplyEndpointQuirks(options, model);

        bool nativeSearch = mode != ChatMode.Chat
                            && ai.WebSearch.Enabled
                            && ai.WebSearch.PreferProviderNative
                            && NativeWebSearch.IsSupported(model.Protocol);
        if (mode != ChatMode.Chat)
        {
            mcp.Approval = approval;
            mcp.ApprovalHandler = approve;
            IList<AITool> tools = toolbox.CreateTools(mode, nativeSearch);
            if (mode == ChatMode.Agent && ai.McpServers.Any(s => s.Enabled))
            {
                (List<AITool> mcpTools, List<string> errors) =
                    await mcp.GetToolsAsync(ai.McpServers, cancellationToken).ConfigureAwait(false);
                foreach (AITool tool in mcpTools)
                {
                    tools.Add(tool);
                }
                if (errors.Count > 0)
                {
                    context.Log.Warn($"Bridge: MCP servers reported: {string.Join("; ", errors)}");
                }
            }
            if (nativeSearch)
            {
                NativeWebSearch.Apply(options, model, tools, ai.WebSearch.MaxResults);
            }
            options.Tools = tools;
            client = client.AsBuilder()
                .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = MaxToolIterations)
                .Build();
        }

        conversation.History.Add(new ChatMessage(ChatRole.User, message.Text));
        await PersistAsync(conversation, "user", message.Text, cancellationToken).ConfigureAwait(false);

        RequestContext request = ContextBuilder.Build(
            BuildSystemPrompt(ai, bridge, mode, message, session, nativeSearch),
            conversation.History, model.MaxInputTokens, model.MaxTokens);
        // 有的订阅型端点不收 system 角色(ChatGPT 的 Codex 后端会回
        // 400 {"detail":"System messages are not allowed"})。那时把系统提示词挪到
        // Responses 协议自己的 instructions 字段上 —— 内容一个字不少,只是换了个位置。
        if (!EndpointQuirks.Of(model.Provider).AllowSystemMessages)
        {
            options.Instructions = ContextBuilder.MoveSystemPromptOut(request.Messages);
        }

        var accumulated = new StringBuilder();
        var updates = new List<ChatResponseUpdate>();
        int toolCalls = 0;
        string modelLabel = $"{model.ProviderName} / {model.Name}";
        try
        {
            await foreach (ChatResponseUpdate update in client
                               .GetStreamingResponseAsync(request.Messages, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                updates.Add(update);
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            accumulated.Append(text.Text);
                            progress?.Invoke(accumulated.ToString());
                            break;

                        case FunctionCallContent call:
                            toolCalls++;
                            // 进度里带一句"正在干什么" —— IM 那头看不到工具卡片,
                            // 一分钟没动静会让人以为机器人死了。
                            progress?.Invoke($"{accumulated}\n\n_{loc.F("BridgeRunningTool", call.Name)}_");
                            break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 把<b>哪个模型</b>带进错误里。群里只看到一句 "401 Unauthorized" 时,
            // 人第一反应会去查飞书的凭证 —— 而这条 401 来自模型服务商,两者差着十万八千里。
            // ApiErrorText 顺带把 Anthropic 那种"只有一句 Status Code"的异常展开成服务端原话。
            throw new InvalidOperationException(
                $"{modelLabel}: {ApiErrorText.Describe(ex)}", ex);
        }

        ChatResponse response = updates.ToChatResponse();
        conversation.History.AddMessages(response);
        string reply = response.Text.Trim();
        if (reply.Length > 0)
        {
            await PersistAsync(conversation, "assistant", reply, cancellationToken).ConfigureAwait(false);
        }
        client.Dispose();
        return new BridgeTurn(
            reply.Length > 0 ? reply : loc["BridgeEmptyReply"],
            modelLabel,
            TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt),
            toolCalls);
    }

    /// <summary>桥接用哪个模型:设置里指定的优先,没指定就跟着面板当前那个。</summary>
    private static ResolvedModel? ResolveModel(AiSettings ai, BridgeSettings bridge)
        => ai.FindModel(string.IsNullOrWhiteSpace(bridge.ModelId) ? ai.ActiveModelId : bridge.ModelId)
           ?? ai.ResolveModels().FirstOrDefault();

    /// <summary>
    /// 桥接侧的系统提示词。在面板那套之上补三件 IM 特有的事:
    /// 回复要短、当前是谁在问、这个聊天绑的是哪台机器。
    /// </summary>
    private string BuildSystemPrompt(AiSettings ai, BridgeSettings bridge, ChatMode mode,
        InboundMessage message, SessionInfo? session, bool nativeWebSearch)
    {
        var prompt = new StringBuilder(
            "You are the VelaShell assistant, reachable from a team chat. " +
            "You help with servers, shell commands, log analysis and DevOps questions. " +
            $"Respond in the user's language (UI locale: {context.Host.Locale}). ");
        // IM 不是聊天面板:没有折叠、没有滚动条,一大段 Markdown 在群里就是刷屏。
        prompt.Append("You are replying INTO A CHAT MESSAGE: keep it short (a few lines), plain, and actionable. " +
                      "No headings, no long tables. Put commands in fenced code blocks. " +
                      "If the answer is long, give the conclusion first and offer details on request. ");
        prompt.Append($"The person asking is {message.UserName} ")
              .Append(message.IsGroup ? "in a group chat with other people watching. " : "in a direct message. ");
        // 没绑机器时的出路。Agent 模式下不再一句"你先去连一台"打发人:开会话的契约已经在了,
        // 只是那一步要过两道人(群里审批 + 用户桌面上的宿主确认框),所以话要说清楚,
        // 免得模型开完一台就以为自己拿到了长期权限。计划模式没有 open_session,照旧指路 /use。
        prompt.Append(session is not null
            ? $"This chat is bound to the SSH session {session.Username}@{session.Host}:{session.Port}; tools act on that server. "
            : mode == ChatMode.Agent
                ? "This chat is NOT bound to any connected SSH session. If the machine is saved in VelaShell, "
                  + "call list_saved_sessions and then open_session — it needs approval here AND a confirmation on the user's "
                  + "desktop, so say which machine and why. Otherwise tell the user to run /sessions and /use <user@host:port>. "
                : "This chat is NOT bound to any connected SSH session, so server tools will fail — "
                  + "tell the user to run /sessions and then /use <user@host:port> to bind one. ");
        if (nativeWebSearch)
        {
            prompt.Append(NativeWebSearch.SystemHint).Append(' ');
        }
        prompt.Append(mode switch
        {
            ChatMode.Agent =>
                "You can call tools to inspect the bound session and to change things on it. " +
                "Destructive commands and file writes are put to a human in this chat for approval — " +
                "propose them deliberately, one at a time, and say what they will do. ",
            ChatMode.Plan =>
                "You are in PLAN mode: read-only tools only. Investigate freely, but you cannot change anything. " +
                "Produce a short ordered plan with the exact commands, the risk, and the rollback. ",
            _ => "You have no tools in this mode: answer from the conversation itself. "
        });
        if (!string.IsNullOrWhiteSpace(ai.SystemPrompt))
        {
            prompt.Append("\n\nOperator instructions: ").Append(ai.SystemPrompt.Trim());
        }
        if (!string.IsNullOrWhiteSpace(bridge.ExtraSystemPrompt))
        {
            prompt.Append("\n\n").Append(bridge.ExtraSystemPrompt.Trim());
        }
        return prompt.ToString();
    }

    private async Task PersistAsync(BridgeConversation conversation, string role, string text,
        CancellationToken cancellationToken)
    {
        if (!history.IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        int sequence = conversation.PersistedCount++;
        await history.AppendAsync(conversation.ConversationId, conversation.CreatedAt, sequence, role, text,
            cancellationToken).ConfigureAwait(false);
    }
}
