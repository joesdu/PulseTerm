using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Auth;

/// <summary>
/// 登录过程中要报给界面的一步。
/// </summary>
/// <param name="Message">一句话进度(直接显示在按钮旁边)。</param>
/// <param name="Device">
/// 设备码流程走到"请去浏览器输这段码"时带上它;其余步骤为 null。
/// 界面据此把用户码显示出来并给个复制按钮 —— 让人从日志里抄码是不可接受的。
/// </param>
public sealed record LoginProgress(string Message, DeviceCodeGrant? Device = null);

/// <summary>
/// 回调页与进度里要用到的文案。由界面按当前语言给,<see cref="ProviderLogin" /> 自己不认识语言。
/// </summary>
/// <param name="BrowserTitle">浏览器回调页的标题。</param>
/// <param name="BrowserBody">浏览器回调页的正文。</param>
/// <param name="WaitingForBrowser">"已打开浏览器,等你在那边完成授权"。</param>
/// <param name="ExchangingCode">"正在换取凭据"。</param>
/// <param name="EnterUserCode">设备码流程:提示用户去输码(带 <c>{0}</c> = 用户码)。</param>
public sealed record LoginPrompts(
    string BrowserTitle,
    string BrowserBody,
    string WaitingForBrowser,
    string ExchangingCode,
    string EnterUserCode);

/// <summary>
/// 把一次订阅登录从头跑到尾:开浏览器、接回调、换凭据。
/// </summary>
/// <remarks>
/// 开浏览器这件事经构造参数注入而不是直接调 Avalonia 的 <c>Launcher</c>:
/// 一是这个类不该认识界面,二是测试里要能把"用户在浏览器里点了同意"这一步换成假的。
/// </remarks>
/// <param name="oauth">协议实现。</param>
/// <param name="openBrowser">把地址交给系统浏览器打开。</param>
public sealed class ProviderLogin(OAuthClient oauth, Func<Uri, CancellationToken, Task> openBrowser)
{
    /// <summary>
    /// 一次授权码流程的总时限。人去浏览器里登录、过二次验证,五分钟足够宽裕;
    /// 再长的话一个忘掉的登录会把环回端口一直占着。
    /// </summary>
    public static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(5);

    /// <summary>登录,成功则返回换到的凭据。</summary>
    /// <param name="config">供应商的登录参数。</param>
    /// <param name="prompts">界面给的文案。</param>
    /// <param name="progress">进度回调(可空)。</param>
    /// <param name="cancellationToken">取消(用户点了"取消",或窗口关了)。</param>
    public Task<OAuthTokens> SignInAsync(OAuthConfig config, LoginPrompts prompts,
        IProgress<LoginProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Flow is OAuthFlow.DeviceCode or OAuthFlow.GitHubCopilotDevice
            ? DeviceCodeAsync(config, prompts, progress, cancellationToken)
            : BrowserAsync(config, prompts, progress, cancellationToken);
    }

    /// <summary>授权码 + PKCE:起环回端口 → 开浏览器 → 等回调 → 换凭据。</summary>
    private async Task<OAuthTokens> BrowserAsync(OAuthConfig config, LoginPrompts prompts,
        IProgress<LoginProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        var pkce = PkceCodes.Create();
        // 监听要在开浏览器<b>之前</b>起来:授权服务器可能立刻打回(已登录 + 已授权过),
        // 那一下若没人接,用户看到的就是"无法访问此网站"。
        using var listener = new LoopbackRedirectListener(config.RedirectPort, config.RedirectPath, config.RedirectHost);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrowserTimeout);
        Uri authorizeUri = OAuthClient.BuildAuthorizationUrl(config, pkce, listener.RedirectUri);
        await openBrowser(authorizeUri, cancellationToken).ConfigureAwait(false);
        progress?.Report(new LoginProgress(prompts.WaitingForBrowser));

        Dictionary<string, string> callback;
        try
        {
            callback = await listener
                .WaitAsync(prompts.BrowserTitle, prompts.BrowserBody,
                    config.Flow == OAuthFlow.ImplicitFragment, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException("timeout", "Timed out waiting for the browser to come back.");
        }

        if (callback.TryGetValue("error", out string? error) && error.Length > 0)
        {
            throw new OAuthException(error, callback.GetValueOrDefault("error_description") ?? error);
        }
        // state 只在标准流程里存在。OpenRouter 的回调不带它 —— 那一条靠"环回端口只活这一次
        // 且 code 与本机 verifier 绑定"兜底,而不是假装校验过。
        if (config.Flow != OAuthFlow.OpenRouterPkce
            && callback.GetValueOrDefault("state") is var state
            && !string.Equals(state, pkce.State, StringComparison.Ordinal))
        {
            throw new OAuthException("invalid_state", "The callback state did not match; the sign-in was discarded.");
        }
        // 隐式流到这里就结束了:令牌本身就在回调参数里,没有"拿码去换"这一步
        if (config.Flow == OAuthFlow.ImplicitFragment)
        {
            if (!callback.TryGetValue("access_token", out string? token) || token.Length == 0)
            {
                throw new OAuthException("The callback did not carry an access token.");
            }
            return new OAuthTokens
            {
                AccessToken = token,
                Scope = callback.GetValueOrDefault("scope") ?? "",
                // 隐式流没有 refresh token —— 过期就得重登,这一点如实记下来
                ExpiresAt = int.TryParse(callback.GetValueOrDefault("expires_in"), out int seconds) && seconds > 0
                    ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                    : null
            };
        }
        if (!callback.TryGetValue("code", out string? code) || code.Length == 0)
        {
            throw new OAuthException("The callback did not carry an authorization code.");
        }
        progress?.Report(new LoginProgress(prompts.ExchangingCode));
        return await oauth.ExchangeCodeAsync(config, pkce, code, listener.RedirectUri, cancellationToken)
                          .ConfigureAwait(false);
    }

    /// <summary>设备码:换一段用户码 → 显示并开浏览器 → 轮询到用户点了同意。</summary>
    private async Task<OAuthTokens> DeviceCodeAsync(OAuthConfig config, LoginPrompts prompts,
        IProgress<LoginProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        DeviceCodeGrant grant = await oauth.StartDeviceCodeAsync(config, cancellationToken).ConfigureAwait(false);
        progress?.Report(new LoginProgress(string.Format(prompts.EnterUserCode, grant.UserCode), grant));
        // 有 verification_uri_complete 就开它(码已经拼在地址里,用户不用手抄);否则开验证页本身
        if ((WebUri(grant.VerificationUriComplete) ?? WebUri(grant.VerificationUri)) is { } uri)
        {
            await openBrowser(uri, cancellationToken).ConfigureAwait(false);
        }
        OAuthTokens tokens = await oauth.PollDeviceCodeAsync(config, grant, cancellationToken).ConfigureAwait(false);
        if (config.Flow != OAuthFlow.GitHubCopilotDevice)
        {
            return tokens;
        }
        // 两段式:刚拿到的只是长期身份 token,还不能发推理请求 —— 再换一枚会过期的会话令牌
        progress?.Report(new LoginProgress(prompts.ExchangingCode));
        return await oauth.ExchangeForSessionAsync(config, tokens.AccessToken, cancellationToken)
                          .ConfigureAwait(false);
    }

    /// <summary>
    /// 只认 http/https 的绝对地址,其余一律当没有。
    /// </summary>
    /// <remarks>
    /// 这个地址是<b>服务端给的</b>,而我们拿到就交给系统去"打开" —— 不设限的话,
    /// 一个被改过的(或本就恶意的)端点回一条 <c>file://</c>、<c>ms-settings:</c> 之类的地址,
    /// 就等于借本程序的手在用户机器上启动了别的东西。登录页只可能是网页,那就只放网页过去。
    /// </remarks>
    private static Uri? WebUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
           && uri.Scheme is "http" or "https"
            ? uri
            : null;
}
