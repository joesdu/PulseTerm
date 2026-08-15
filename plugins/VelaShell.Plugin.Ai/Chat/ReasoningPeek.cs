using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 从流式增量的原始报文里捞思考文本 —— 兜的是"OpenAI 兼容"这条线上各家自造的字段。
/// </summary>
/// <remarks>
/// <b>为什么需要它(2026-08-15 实测)</b>:Chat Completions 协议里思考内容没有标准字段,
/// 各家各造一个。M.E.AI 的 OpenAI 适配器只认 DeepSeek 那一套 <c>delta.reasoning_content</c>
/// (实测已自动变成 <see cref="TextReasoningContent" />,这条不用管);
/// 而 OpenRouter 一系用 <c>delta.reasoning</c>,适配器不认 —— 那一帧解析出来
/// <b>一个 AIContent 都没有</b>,思考就这么丢了。
///
/// 好在 OpenAI SDK 的模型会把未映射字段原样留着并在 <see cref="ModelReaderWriter" />
/// 回写时吐出来,所以从 <c>RawRepresentation</c> 把原始 JSON 要回来再翻一遍即可。
/// 只在"这一帧什么都没解析出来"时才走这条路,正常帧不付代价。
/// </remarks>
public static class ReasoningPeek
{
    /// <summary>各家用过的键,按常见度排序。</summary>
    private static readonly string[] Keys = ["reasoning", "reasoning_content", "thinking"];

    /// <summary>这一帧是否没有任何值得渲染的内容(那才需要去翻原始报文)。</summary>
    public static bool IsBlank(ChatResponseUpdate update)
    {
        foreach (AIContent content in update.Contents)
        {
            if (content is not UsageContent)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 尝试从增量的原始报文里取思考文本。
    /// 取不到(不是 ClientModel 模型、报文结构不认、字段不在)一律返回 false,不抛。
    /// </summary>
    public static bool TryRead(object? rawRepresentation, out string text)
    {
        text = "";
        if (rawRepresentation is null)
        {
            return false;
        }
        try
        {
            BinaryData json = ModelReaderWriter.Write(rawRepresentation);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            var builder = new System.Text.StringBuilder();
            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out JsonElement delta))
                {
                    Collect(delta, builder);
                }
            }
            text = builder.ToString();
            return text.Length > 0;
        }
        catch (Exception)
        {
            // 原始报文的形状是各家自己的事,认不出来就当没有 —— 这条路本来就是兜底
            return false;
        }
    }

    private static void Collect(JsonElement delta, System.Text.StringBuilder builder)
    {
        foreach (string key in Keys)
        {
            if (!delta.TryGetProperty(key, out JsonElement value))
            {
                continue;
            }
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    builder.Append(value.GetString());
                    break;
                // 有的实现把思考包一层({"text": …} / {"content": …}),顺手也认了
                case JsonValueKind.Object when value.TryGetProperty("text", out JsonElement inner)
                                               && inner.ValueKind == JsonValueKind.String:
                    builder.Append(inner.GetString());
                    break;
                case JsonValueKind.Object when value.TryGetProperty("content", out JsonElement innerContent)
                                               && innerContent.ValueKind == JsonValueKind.String:
                    builder.Append(innerContent.GetString());
                    break;
            }
        }
    }
}
