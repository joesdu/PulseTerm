using VelaShell.Plugin.Ai.Bridge.Channels.DingTalk;
using VelaShell.Plugin.Ai.Bridge.Channels.Feishu;
using VelaShell.Plugin.Ai.Bridge.Channels.Telegram;
using VelaShell.Plugin.Ai.Bridge.Channels.WeCom;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>按配置造渠道实例。凭证缺一半就<b>不造</b>,并在日志里说清楚缺什么。</summary>
internal static class ChannelFactory
{
    /// <summary>造一个渠道;配置不完整或平台还没接就返回 null(由调用方跳过)。</summary>
    public static async Task<IMessageChannel?> CreateAsync(IPluginContext context, BridgeSettingsStore store,
        ChannelConfig config, CancellationToken cancellationToken)
    {
        string? secret = await store.GetSecretAsync(config.Id, "secret", cancellationToken).ConfigureAwait(false);
        switch (config.Kind)
        {
            case ChannelKind.Feishu:
                if (string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(secret))
                {
                    context.Log.Warn($"Bridge: {config.Label} needs an App ID and an App Secret; skipping it.");
                    return null;
                }
                return new FeishuChannel(config, secret, context);

            case ChannelKind.DingTalk:
                if (string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(secret))
                {
                    context.Log.Warn($"Bridge: {config.Label} needs a Client ID (AppKey) and Client Secret; skipping it.");
                    return null;
                }
                return new DingTalkChannel(config, secret, context);

            case ChannelKind.Telegram:
                // Telegram 只有一个令牌,它同时是身份与凭据,所以不看 AppId
                if (string.IsNullOrWhiteSpace(secret))
                {
                    context.Log.Warn($"Bridge: {config.Label} needs a bot token; skipping it.");
                    return null;
                }
                return new TelegramChannel(config, secret, context);

            case ChannelKind.WeCom:
                {
                    // 企微要三份东西:corpsecret、回调 Token、EncodingAESKey。缺哪个说哪个 ——
                    // 三个空框里到底少填了哪一个,不点破的话用户只会看到"连不上"。
                    string? callbackToken = await store.GetSecretAsync(config.Id, "token", cancellationToken).ConfigureAwait(false);
                    string? aesKey = await store.GetSecretAsync(config.Id, "aeskey", cancellationToken).ConfigureAwait(false);
                    List<string> missing = [];
                    if (string.IsNullOrWhiteSpace(config.AppId))
                    {
                        missing.Add("CorpID");
                    }
                    if (string.IsNullOrWhiteSpace(config.AgentId))
                    {
                        missing.Add("AgentID");
                    }
                    if (string.IsNullOrWhiteSpace(secret))
                    {
                        missing.Add("Secret");
                    }
                    if (string.IsNullOrWhiteSpace(callbackToken))
                    {
                        missing.Add("callback Token");
                    }
                    if (string.IsNullOrWhiteSpace(aesKey))
                    {
                        missing.Add("EncodingAESKey");
                    }
                    if (missing.Count > 0)
                    {
                        context.Log.Warn($"Bridge: {config.Label} is missing {string.Join(", ", missing)}; skipping it.");
                        return null;
                    }
                    try
                    {
                        return new WeComChannel(config, secret!, callbackToken!, aesKey!, context);
                    }
                    catch (Exception ex) when (ex is ArgumentException or FormatException)
                    {
                        // EncodingAESKey 填错(长度/字符集不对)在构造时就炸,别让它变成一条重连风暴
                        context.Log.Error($"Bridge: {config.Label} has an unusable EncodingAESKey: {ex.Message}");
                        return null;
                    }
                }

            default:
                context.Log.Warn($"Bridge: channel type {config.Kind} is not wired up yet; skipping {config.Label}.");
                return null;
        }
    }
}
