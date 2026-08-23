using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Agent.Web;

/// <summary>
/// 供应商自带的服务端检索工具(检索跑在模型那一侧,不经本机)。
/// </summary>
/// <remarks>
/// <para>
/// 两家吃得下,而且吃法不一样:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>OpenAI Responses</b> —— M.E.AI 有现成的 <see cref="HostedWebSearchTool" /> 抽象,
/// 往 <c>ChatOptions.Tools</c> 里一放,适配器自己翻译成内置的 <c>web_search</c>。
/// </item>
/// <item>
/// <b>Anthropic Messages</b> —— 适配器不认 <see cref="HostedWebSearchTool" />
/// (它连 reasoning 都不映射,见 <c>AiSettingsStore.ApplyReasoning</c> 的实测记录),
/// 只能经 <c>RawRepresentationFactory</c> 把 <c>web_search_20250305</c> 直接塞进请求体。
/// </item>
/// </list>
/// <para>
/// 其余协议(OpenAI Chat Completions、Ollama、多数中转站)没有这回事,
/// <see cref="IsSupported" /> 返回 false,调用方回落到插件自带的 <c>web_search</c>。
/// </para>
/// <para>
/// <b>实测记录(2026-08-23,Anthropic SDK 12.42.0,反射 + 序列化核对)</b>:
/// <c>WebSearchTool20250305</c> 的 <c>name</c> / <c>type</c> 由 SDK 自己填
/// (<c>"web_search"</c> / <c>"web_search_20250305"</c>),只需要给 <c>MaxUses</c>;
/// <c>ToolUnion</c> 有到它的隐式转换;<c>MessageCreateParams.Tools</c> 是
/// <c>IReadOnlyList&lt;ToolUnion&gt;</c> 且 <b>init-only</b> —— 所以下面只能整个重建一份,
/// 不能在原对象上追加。
/// </para>
/// </remarks>
internal static class NativeWebSearch
{
    /// <summary>这个协议有没有自带的服务端检索。</summary>
    public static bool IsSupported(ChatProtocol protocol)
        => protocol is ChatProtocol.OpenAiResponses or ChatProtocol.AnthropicMessages;

    /// <summary>
    /// 把原生检索挂到这一轮请求上。
    /// </summary>
    /// <param name="options">这一轮的请求选项。</param>
    /// <param name="model">当前模型(要它的协议、模型名与输出上限)。</param>
    /// <param name="tools">这一轮的工具列表(OpenAI 那条路往里加一件)。</param>
    /// <param name="maxUses">一轮里最多让模型搜几次 —— 服务端检索是按次计费的,不设上限等于开着水龙头。</param>
    /// <remarks>
    /// <b>必须在 <c>ApplyReasoning</c> 之后调用</b>:Anthropic 那条路是在已有的
    /// <c>RawRepresentationFactory</c> 上叠一层,先叠就会被后设的思考配置整个盖掉。
    /// </remarks>
    public static void Apply(ChatOptions options, ResolvedModel model, IList<AITool> tools, int maxUses)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tools);
        maxUses = Math.Clamp(maxUses, 1, 20);

        if (model.Protocol == ChatProtocol.OpenAiResponses)
        {
            tools.Add(new HostedWebSearchTool());
            return;
        }
        if (model.Protocol != ChatProtocol.AnthropicMessages)
        {
            return;
        }

        Func<IChatClient, object?>? inner = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            // 思考那一层(如果开着)先算出来,把它已经填好的字段原样搬过来 ——
            // MessageCreateParams 的属性是 init-only,追加不了,只能重建。
            MessageCreateParams basis = inner?.Invoke(client) as MessageCreateParams
                                        ?? new MessageCreateParams
                                        {
                                            Messages = [],
                                            MaxTokens = model.MaxTokens,
                                            Model = model.Model
                                        };
            return new MessageCreateParams
            {
                Messages = basis.Messages,
                MaxTokens = basis.MaxTokens,
                Model = basis.Model,
                Thinking = basis.Thinking,
                Tools = [new WebSearchTool20250305 { MaxUses = maxUses }]
            };
        };
    }

    /// <summary>
    /// 说明文字里提一句原生检索已经开着 —— 模型看不到自己被挂了什么服务端工具,
    /// 不提的话它会以为没有联网能力,张口就是"我无法访问互联网"。
    /// </summary>
    public const string SystemHint =
        "You have a provider-side web search tool available: use it to look things up when the answer "
        + "may have changed since your training cutoff. Cite the URLs you relied on.";
}
