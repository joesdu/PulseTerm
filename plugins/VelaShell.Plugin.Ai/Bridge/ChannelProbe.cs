using System.Net.Http.Json;
using System.Text.Json;
using VelaShell.Plugin.Ai.Bridge.Channels.Feishu;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>一次凭证体检的结果。</summary>
/// <param name="Ok">凭证能用。</param>
/// <param name="Summary">给用户看的一句话:成了是机器人叫什么,不成是卡在哪一步。</param>
/// <param name="InviteUrl">
/// 把机器人加进群的链接(拿得到时)。设置页把它渲染成二维码 ——
/// 手机扫一下直接跳过去,省掉在手机上搜应用名这一步。
/// </param>
public readonly record struct ChannelProbeResult(bool Ok, string Summary, string? InviteUrl);

/// <summary>
/// 保存之前先把凭证拿去试一次。
/// </summary>
/// <remarks>
/// <b>为什么值得单独做一个。</b>接飞书最常见的两种翻车不是密钥填错,而是
/// 「事件订阅没改成长连接」与「改完没发布版本」—— 这两种情况下密钥完全正确,
/// 桥接却一条消息都收不到。原来只能保存、重连、翻日志去猜;现在填完点一下就知道卡在哪。
/// <para>
/// 探测<b>只读</b>:换一次令牌、查一次自身信息、问一次长连接接入点。不发消息、不改任何配置。
/// </para>
/// </remarks>
internal static class ChannelProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>按渠道类型做对应的体检。</summary>
    public static async Task<ChannelProbeResult> TestAsync(ChannelConfig config, string secret,
        string encodingAesKey, IPluginLogger log, CancellationToken cancellationToken)
    {
        try
        {
            return config.Kind switch
            {
                ChannelKind.Feishu => await FeishuAsync(config, secret, log, cancellationToken).ConfigureAwait(false),
                ChannelKind.DingTalk => await DingTalkAsync(config, secret, cancellationToken).ConfigureAwait(false),
                ChannelKind.Telegram => await TelegramAsync(secret, cancellationToken).ConfigureAwait(false),
                _ => await WeComAsync(config, secret, encodingAesKey, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 体检本身失败也是一种结果,不该把设置页炸掉
            return new ChannelProbeResult(false, ex.Message, null);
        }
    }

    private static async Task<ChannelProbeResult> FeishuAsync(ChannelConfig config, string secret,
        IPluginLogger log, CancellationToken cancellationToken)
    {
        using var api = new FeishuApi(config.AppId, secret, config.International, log);
        await api.TokenAsync(cancellationToken).ConfigureAwait(false);

        // 走到这里说明 AppID/Secret 是对的。接下来问长连接接入点 ——
        // 这一步失败几乎总是「事件订阅没选长连接」或「改了没发版本」,而不是凭证问题。
        try
        {
            await api.EndpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ChannelProbeResult(false,
                $"credentials OK, but the long-connection endpoint was refused: {ex.Message}", null);
        }
        string? botOpenId = await api.BotOpenIdAsync(cancellationToken).ConfigureAwait(false);
        // 飞书<b>没有</b>"扫码把机器人加进群"的链接。applink 的 client/app/open 是打开小程序页面用的,
        // 纯机器人应用根本没有那个页面 —— 拿它生成二维码,扫出来是"此页面无效"(实测)。
        // 官方路径只有客户端里的 群设置 → 群机器人 → 添加机器人,所以这里不给链接,
        // 由界面直接把这句话写出来。给一个扫了不能用的码,比不给码更糟。
        return new ChannelProbeResult(true,
            botOpenId is { Length: > 0 } ? $"connected (bot {botOpenId})" : "connected",
            null);
    }

    private static async Task<ChannelProbeResult> DingTalkAsync(ChannelConfig config, string secret,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.PostAsJsonAsync(
            "https://api.dingtalk.com/v1.0/oauth2/accessToken",
            new { appKey = config.AppId, appSecret = secret }, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("accessToken", out _)
            ? new ChannelProbeResult(true, "credentials OK", null)
            : new ChannelProbeResult(false, Describe(document.RootElement, "message", "code"), null);
    }

    private static async Task<ChannelProbeResult> TelegramAsync(string token, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http
            .GetAsync($"https://api.telegram.org/bot{token}/getMe", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            return new ChannelProbeResult(false, Describe(root, "description", "error_code"), null);
        }
        string username = root.TryGetProperty("result", out JsonElement result)
                          && result.TryGetProperty("username", out JsonElement name)
            ? name.GetString() ?? ""
            : "";
        // startgroup=true 让扫码的人直接进「把这个机器人加进哪个群」的选择界面
        return new ChannelProbeResult(true, $"connected (@{username})",
            username.Length > 0 ? $"https://t.me/{username}?startgroup=true" : null);
    }

    private static async Task<ChannelProbeResult> WeComAsync(ChannelConfig config, string secret,
        string encodingAesKey, CancellationToken cancellationToken)
    {
        // AESKey 是回调那一侧的东西,换令牌用不到它;但填错了回调必然全军覆没,
        // 所以在这里顺手验一次格式 —— 比等到平台第一次推消息时才发现要早得多。
        if (encodingAesKey.Length > 0)
        {
            try
            {
                Channels.WeCom.WeComCrypto.ParseKey(encodingAesKey);
            }
            catch (ArgumentException ex)
            {
                return new ChannelProbeResult(false, $"EncodingAESKey is unusable: {ex.Message}", null);
            }
        }
        string url = $"https://qyapi.weixin.qq.com/cgi-bin/gettoken?corpid={Uri.EscapeDataString(config.AppId)}"
                     + $"&corpsecret={Uri.EscapeDataString(secret)}";
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("access_token", out _)
            ? new ChannelProbeResult(true, "credentials OK", null)
            : new ChannelProbeResult(false, Describe(document.RootElement, "errmsg", "errcode"), null);
    }

    private static string Describe(JsonElement root, string messageField, string codeField)
    {
        string message = root.TryGetProperty(messageField, out JsonElement m) ? m.GetString() ?? "" : "";
        string code = root.TryGetProperty(codeField, out JsonElement c) ? c.ToString() : "";
        return message.Length > 0 ? $"{message} ({code})" : $"rejected ({code})";
    }
}
