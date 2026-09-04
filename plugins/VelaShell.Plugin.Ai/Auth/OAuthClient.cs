using System.Net.Http.Headers;
using System.Text.Json;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Auth;

/// <summary>授权服务器明确回绝了(<c>error</c> / <c>error_description</c>),或回了一份看不懂的载荷。</summary>
public sealed class OAuthException : Exception
{
    /// <summary>带错误码构造。</summary>
    /// <param name="error">OAuth 错误码(<c>invalid_grant</c> 之类);解析不出来时为空串。</param>
    /// <param name="message">给人看的说明。</param>
    public OAuthException(string error, string message) : base(message) => Error = error;

    /// <summary>只有说明、没有标准错误码时。</summary>
    public OAuthException(string message) : base(message) => Error = "";

    /// <summary>OAuth 错误码;轮询设备码时靠它区分"还没批"与"真失败"。</summary>
    public string Error { get; }
}

/// <summary>
/// 设备码流程第一步换回来的东西:一段给人念的用户码 + 一个让人去输的地址。
/// </summary>
/// <param name="DeviceCode">轮询用的设备码(不给人看)。</param>
/// <param name="UserCode">用户码(要显示出来,让人抄进浏览器)。</param>
/// <param name="VerificationUri">验证页地址。</param>
/// <param name="VerificationUriComplete">已经把用户码拼进去的地址;有就直接开它,省得人手抄。</param>
/// <param name="ExpiresIn">这组码多久作废。</param>
/// <param name="Interval">轮询间隔下限(服务端要求)。</param>
public sealed record DeviceCodeGrant(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    TimeSpan ExpiresIn,
    TimeSpan Interval);

/// <summary>
/// OAuth 协议本身 —— 只拼请求、只解响应,不碰界面、不碰存储、不开浏览器。
/// </summary>
/// <remarks>
/// 之所以把协议单独拎出来:授权流程里唯一容易出错的就是这些字段名和错误码分支,
/// 而它们全都可以用一个假的 <see cref="HttpMessageHandler" /> 打桩测掉。
/// 开浏览器、起环回监听、存机密那些副作用都在 <see cref="ProviderLogin" /> 里。
/// </remarks>
/// <param name="http">发请求用的客户端(调用方负责生命周期)。</param>
public sealed class OAuthClient(HttpClient http)
{
    /// <summary>设备码流程换 token 时的 grant_type(RFC 8628 §3.4)。</summary>
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>服务端喊 <c>slow_down</c> 时把轮询间隔加多少(RFC 8628 §3.5 建议 5 秒)。</summary>
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 两次轮询之间怎么等。生产就是 <see cref="Task.Delay(TimeSpan, CancellationToken)" />;
    /// 测试里换成"记下来但不真等",否则光验一次 <c>slow_down</c> 就要挂十几秒。
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// 拼出让用户在浏览器里打开的授权地址。
    /// </summary>
    /// <param name="config">供应商的登录参数。</param>
    /// <param name="pkce">本次握手的 PKCE 三件套。</param>
    /// <param name="redirectUri">环回回调地址(授权完成后浏览器会被打回这里)。</param>
    public static Uri BuildAuthorizationUrl(OAuthConfig config, PkceCodes pkce, string redirectUri)
    {
        ArgumentNullException.ThrowIfNull(config);
        var query = new List<KeyValuePair<string, string>>();
        if (config.Flow == OAuthFlow.OpenRouterPkce)
        {
            // OpenRouter 只认这三个参数:没有 client_id、没有 response_type,回调参数也换了名字
            query.Add(new("callback_url", redirectUri));
            query.Add(new("code_challenge", pkce.Challenge));
            query.Add(new("code_challenge_method", PkceCodes.Method));
        }
        else if (config.Flow == OAuthFlow.ImplicitFragment)
        {
            // 隐式流:直接要令牌,没有授权码,也就<b>没有 PKCE 可用</b>
            // (challenge 是给"换码"那一步验的,这里根本没有那一步)。
            // state 照发 —— 它防的是别人往我的回调里塞东西,与 PKCE 是两回事
            query.Add(new("response_type", "token"));
            query.Add(new("client_id", config.ClientId));
            query.Add(new("redirect_uri", redirectUri));
            query.Add(new("state", pkce.State));
            if (!string.IsNullOrWhiteSpace(config.Scopes))
            {
                query.Add(new("scope", config.Scopes.Trim()));
            }
        }
        else
        {
            query.Add(new("response_type", "code"));
            query.Add(new("client_id", config.ClientId));
            query.Add(new("redirect_uri", redirectUri));
            query.Add(new("state", pkce.State));
            query.Add(new("code_challenge", pkce.Challenge));
            query.Add(new("code_challenge_method", PkceCodes.Method));
            if (!string.IsNullOrWhiteSpace(config.Scopes))
            {
                query.Add(new("scope", config.Scopes.Trim()));
            }
        }
        foreach (KeyValuePair<string, string> extra in ParsePairs(config.ExtraAuthorizeParams))
        {
            query.Add(extra);
        }
        var builder = new UriBuilder(config.AuthorizationUrl);
        // 授权地址本身可能已经带着查询串(带 tenant / project 的自建服务常见),别把它冲掉
        string existing = builder.Query.TrimStart('?');
        string appended = string.Join('&', query.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        builder.Query = existing.Length > 0 ? $"{existing}&{appended}" : appended;
        return builder.Uri;
    }

    /// <summary>拿授权码换凭据。</summary>
    /// <param name="config">供应商的登录参数。</param>
    /// <param name="pkce">发起授权时那一组(要用里面的 verifier)。</param>
    /// <param name="code">回调里收到的授权码。</param>
    /// <param name="redirectUri">发起授权时用的回调地址(协议要求两次一致)。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<OAuthTokens> ExchangeCodeAsync(OAuthConfig config, PkceCodes pkce, string code,
        string redirectUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Flow == OAuthFlow.OpenRouterPkce)
        {
            return await ExchangeForApiKeyAsync(config, pkce, code, cancellationToken).ConfigureAwait(false);
        }
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", pkce.Verifier)
        };
        AddClientCredentials(form, config);
        return ReadTokens(await PostAsync(config.TokenUrl, form, cancellationToken).ConfigureAwait(false), config);
    }

    /// <summary>
    /// OpenRouter 式的"code 换 Key":请求体是 JSON 而不是表单,回来的字段叫 <c>key</c>,
    /// 而且是一把<b>长期</b> API Key —— 没有 refresh token,也不会过期。
    /// </summary>
    private async Task<OAuthTokens> ExchangeForApiKeyAsync(OAuthConfig config, PkceCodes pkce, string code,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenUrl)
        {
            Content = JsonContent.Create(new
            {
                code,
                code_verifier = pkce.Verifier,
                code_challenge_method = PkceCodes.Method
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> payload = Parse(body, response.Content.Headers.ContentType?.MediaType);
        ThrowIfError(payload, response, body);
        if (!payload.TryGetValue("key", out string? key) || string.IsNullOrWhiteSpace(key))
        {
            throw new OAuthException($"The endpoint did not return a key: {Trim(body)}");
        }
        return new OAuthTokens { AccessToken = key };
    }

    /// <summary>
    /// 第二段交换:拿第一段换来的长期 token 去换真正能发推理请求的那枚短命令牌。
    /// </summary>
    /// <remarks>
    /// GitHub Copilot 是这个形状:GitHub 的 token 本身不过期,但它<b>不能直接发推理请求</b>;
    /// 要拿它去 <c>copilot_internal</c> 换一枚几十分钟就过期的令牌,响应里还会告诉你
    /// 该打哪个地址(企业账户与个人账户不是同一个)。
    /// <para>
    /// 因此这一路的"刷新"是<b>重新做一次交换</b>,而不是标准的 <c>refresh_token</c> 授权 ——
    /// 长期 token 存在 <see cref="OAuthTokens.RefreshToken" /> 里,短命的那枚存
    /// <see cref="OAuthTokens.AccessToken" />。
    /// </para>
    /// </remarks>
    /// <param name="config">供应商的登录参数(要用 <see cref="OAuthConfig.ExchangeUrl" />)。</param>
    /// <param name="longLivedToken">第一段拿到的长期 token。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<OAuthTokens> ExchangeForSessionAsync(OAuthConfig config, string longLivedToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.ExchangeUrl))
        {
            throw new OAuthException("This provider has no exchange endpoint configured.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, config.ExchangeUrl);
        // 这一段用的是 GitHub 的 token 方案,不是 Bearer
        request.Headers.TryAddWithoutValidation("Authorization", $"token {longLivedToken}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 交换端点会校验调用方的身份标识(Copilot 那个不带编辑器版本就 403,
        // 而报错里一个字都不提缺了什么)—— 具体带什么由目录数据给
        foreach (KeyValuePair<string, string> header in ParseHeaders(config.ExchangeHeaders))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> payload = Parse(body, response.Content.Headers.ContentType?.MediaType);
        ThrowIfError(payload, response, body);
        if (!payload.TryGetValue("token", out string? session) || session.Length == 0)
        {
            throw new OAuthException($"The exchange endpoint returned no token: {Trim(body)}");
        }
        var tokens = new OAuthTokens
        {
            AccessToken = session,
            // 长期 token 留着 —— 短命的那枚过期后还要靠它再换一次
            RefreshToken = longLivedToken
        };
        // expires_at 是绝对秒级时间戳(与 expires_in 不同,别混)
        if (payload.TryGetValue("expires_at", out string? expiresAt)
            && long.TryParse(expiresAt, out long epoch) && epoch > 0)
        {
            tokens.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        // 响应里会带着这个账户该用的端点(企业账户与个人账户不同)
        tokens.BaseUrl = Empty(ReadEndpoint(body));
        return tokens;
    }

    /// <summary>从交换响应里取 <c>endpoints.api</c>(有就用,没有则沿用配置里的基地址)。</summary>
    private static string? ReadEndpoint(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("endpoints", out JsonElement endpoints)
                   && endpoints.ValueKind == JsonValueKind.Object
                   && endpoints.TryGetProperty("api", out JsonElement api)
                   && api.ValueKind == JsonValueKind.String
                ? api.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>用 refresh token 换一组新的。原来的 refresh token 服务端不重发时继续沿用。</summary>
    /// <param name="config">供应商的登录参数。</param>
    /// <param name="current">当前这组令牌(要用里面的 refresh token)。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<OAuthTokens> RefreshAsync(OAuthConfig config, OAuthTokens current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new OAuthException("This account has no refresh token; sign in again.");
        }
        if (config.Flow == OAuthFlow.GitHubCopilotDevice)
        {
            // 这一路没有 refresh_token 授权:过期的是换来的会话令牌,
            // 而长期身份 token(存在 RefreshToken 里)不过期 —— 拿它重做一次交换即可
            return await ExchangeForSessionAsync(config, current.RefreshToken, cancellationToken)
                       .ConfigureAwait(false);
        }
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", current.RefreshToken)
        };
        if (!string.IsNullOrWhiteSpace(config.Scopes))
        {
            form.Add(new("scope", config.Scopes.Trim()));
        }
        AddClientCredentials(form, config);
        OAuthTokens fresh = ReadTokens(
            await PostAsync(config.TokenUrl, form, cancellationToken).ConfigureAwait(false), config);
        // 多数服务端刷新时不重发 refresh token(RFC 6749 §6 允许),丢了就再也刷不动了
        fresh.RefreshToken ??= current.RefreshToken;
        fresh.Account ??= current.Account;
        // 刷新响应常常不带 id_token,账号 id 就从旧的那份继承 —— 丢了它请求会缺一个必填头
        fresh.AccountId ??= current.AccountId;
        return fresh;
    }

    /// <summary>设备码流程第一步:申请一组用户码。</summary>
    /// <param name="config">供应商的登录参数。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<DeviceCodeGrant> StartDeviceCodeAsync(OAuthConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var form = new List<KeyValuePair<string, string>> { new("client_id", config.ClientId) };
        if (!string.IsNullOrWhiteSpace(config.Scopes))
        {
            form.Add(new("scope", config.Scopes.Trim()));
        }
        // 设备码请求就是这一路的"授权请求",所以额外参数在这儿带上
        // (xAI 要一个 referrer 说明来路;不带也能过,但对方分不清是谁在用)
        foreach (KeyValuePair<string, string> extra in ParsePairs(config.ExtraAuthorizeParams))
        {
            form.Add(extra);
        }
        Dictionary<string, string> payload = await PostAsync(config.DeviceCodeUrl, form, cancellationToken).ConfigureAwait(false);
        if (!payload.TryGetValue("device_code", out string? deviceCode) || deviceCode.Length == 0)
        {
            throw new OAuthException("The endpoint did not return a device_code.");
        }
        payload.TryGetValue("verification_uri", out string? verification);
        // 微软家用的是 verification_url(RFC 定稿前的草案拼法),两个都认
        verification ??= payload.GetValueOrDefault("verification_url") ?? "";
        return new DeviceCodeGrant(
            deviceCode,
            payload.GetValueOrDefault("user_code") ?? "",
            verification,
            payload.GetValueOrDefault("verification_uri_complete"),
            TimeSpan.FromSeconds(Seconds(payload, "expires_in", 900)),
            // RFC 8628 §3.2:没给 interval 就默认 5 秒
            TimeSpan.FromSeconds(Seconds(payload, "interval", 5)));
    }

    /// <summary>
    /// 设备码流程第二步:轮询到用户在浏览器里点了同意为止。
    /// </summary>
    /// <remarks>
    /// 三种"不是错误的错误"必须分开处理,否则要么把正常等待当成失败,要么一直被限速:
    /// <c>authorization_pending</c> 照原间隔继续等;<c>slow_down</c> 把间隔加 5 秒(RFC 8628 §3.5 的要求);
    /// <c>expired_token</c> / <c>access_denied</c> 立刻收摊。
    /// </remarks>
    /// <param name="config">供应商的登录参数。</param>
    /// <param name="grant">第一步换回来的那组码。</param>
    /// <param name="cancellationToken">取消(用户点"取消登录"或关窗口)。</param>
    public async Task<OAuthTokens> PollDeviceCodeAsync(OAuthConfig config, DeviceCodeGrant grant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(grant);
        TimeSpan interval = grant.Interval;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + grant.ExpiresIn;
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", DeviceCodeGrantType),
            new("device_code", grant.DeviceCode)
        };
        AddClientCredentials(form, config);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new OAuthException("expired_token", "The device code expired before the sign-in completed.");
            }
            await Delay(interval, cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> payload;
            try
            {
                payload = await PostAsync(config.TokenUrl, form, cancellationToken).ConfigureAwait(false);
            }
            catch (OAuthException ex) when (ex.Error is "authorization_pending")
            {
                continue;
            }
            catch (OAuthException ex) when (ex.Error is "slow_down")
            {
                interval += SlowDownStep;
                continue;
            }
            return ReadTokens(payload, config);
        }
    }

    /// <summary>公共客户端不带密钥,带了的(自建服务)一并发过去。</summary>
    private static void AddClientCredentials(List<KeyValuePair<string, string>> form, OAuthConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ClientId))
        {
            form.Add(new("client_id", config.ClientId));
        }
        if (!string.IsNullOrWhiteSpace(config.ClientSecret))
        {
            form.Add(new("client_secret", config.ClientSecret));
        }
    }

    /// <summary>POST 一个表单,把响应解成键值对;服务端报错则抛 <see cref="OAuthException" />。</summary>
    private async Task<Dictionary<string, string>> PostAsync(string url,
        IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new OAuthException("This provider has no token endpoint configured.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        // GitHub 之流默认回 application/x-www-form-urlencoded,声明一下就给 JSON;
        // 两种都能解(见 Parse),这里只是取更省事的那条路
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> payload = Parse(body, response.Content.Headers.ContentType?.MediaType);
        ThrowIfError(payload, response, body);
        return payload;
    }

    /// <summary>
    /// 挑出错误。<b>不能只看 HTTP 状态码</b>:设备码轮询期间"还没批"这件事,
    /// 有的服务端回 400 + <c>authorization_pending</c>,有的回 200 + 同样的 body。
    /// </summary>
    private static void ThrowIfError(Dictionary<string, string> payload, HttpResponseMessage response, string body)
    {
        if (payload.TryGetValue("error", out string? error) && error.Length > 0)
        {
            string description = payload.GetValueOrDefault("error_description")
                                 ?? payload.GetValueOrDefault("message")
                                 ?? error;
            throw new OAuthException(error, description);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body)}");
        }
    }

    /// <summary>把标准 token 响应读成 <see cref="OAuthTokens" />。</summary>
    private static OAuthTokens ReadTokens(Dictionary<string, string> payload, OAuthConfig config)
    {
        if (!payload.TryGetValue("access_token", out string? accessToken) || accessToken.Length == 0)
        {
            throw new OAuthException("The endpoint did not return an access_token.");
        }
        var tokens = new OAuthTokens
        {
            AccessToken = accessToken,
            RefreshToken = Empty(payload.GetValueOrDefault("refresh_token")),
            Scope = Empty(payload.GetValueOrDefault("scope"))
        };
        // expires_in 是"还有多少秒",不是时刻 —— 存成时刻才经得起进程重启
        if (payload.TryGetValue("expires_in", out string? expires)
            && double.TryParse(expires, out double seconds) && seconds > 0)
        {
            tokens.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        // 少数服务端顺手把账号信息塞在 token 响应里;有就用,没有不猜
        tokens.Account = Empty(payload.GetValueOrDefault("account")
                               ?? payload.GetValueOrDefault("email")
                               ?? payload.GetValueOrDefault("username"));
        // 订阅型端点常把"算在哪个账号头上"藏在 id_token 的 claim 里,而不是响应的顶层字段
        if (Empty(payload.GetValueOrDefault("id_token")) is { } idToken)
        {
            Dictionary<string, JsonElement> claims = ReadJwtPayload(idToken);
            tokens.Account ??= Empty(Text(claims.GetValueOrDefault("email")));
            if (!string.IsNullOrWhiteSpace(config.AccountIdClaim))
            {
                tokens.AccountId = Empty(ReadClaim(claims, config.AccountIdClaim));
            }
        }
        return tokens;
    }

    /// <summary>
    /// 解开 JWT 的载荷段读 claim。<b>不验签</b>:这枚令牌是本程序刚从 TLS 通道上亲手换回来的,
    /// 读它只为知道该把请求算到哪个账号头上,不作任何信任判断 —— 真正的鉴权在服务端。
    /// </summary>
    private static Dictionary<string, JsonElement> ReadJwtPayload(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return [];
        }
        try
        {
            string segment = parts[1].Replace('-', '+').Replace('_', '/');
            // base64url 去掉了填充,补回来才解得开
            segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(segment));
            var claims = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                claims[property.Name] = property.Value.Clone();
            }
            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return []; // 不是 JWT 就当没有这份信息,别把整次登录炸掉
        }
    }

    /// <summary>
    /// 取 claim。<b>先拿整条路径当键试一次</b>,不中再按最后一个 <c>/</c> 拆成「命名空间 + 字段」。
    /// </summary>
    /// <remarks>
    /// 不能一上来就按 <c>/</c> 全切:带命名空间的 claim 名本身<b>就含斜杠</b>
    /// (OpenAI 的是 <c>https://api.openai.com/auth</c> 这一整串当键,底下再挂
    /// <c>chatgpt_account_id</c>),切碎了反而永远找不到。
    /// </remarks>
    private static string? ReadClaim(Dictionary<string, JsonElement> claims, string path)
    {
        if (claims.TryGetValue(path, out JsonElement direct))
        {
            return Text(direct);
        }
        int split = path.LastIndexOf('/');
        if (split <= 0 || split == path.Length - 1)
        {
            return null;
        }
        return claims.TryGetValue(path[..split], out JsonElement container)
               && container.ValueKind == JsonValueKind.Object
               && container.TryGetProperty(path[(split + 1)..], out JsonElement leaf)
            ? Text(leaf)
            : null;
    }

    /// <summary>只认字符串型 claim;数字/对象之类一律当没有,不做隐式转换。</summary>
    private static string? Text(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static int Seconds(Dictionary<string, string> payload, string key, int fallback)
        => payload.TryGetValue(key, out string? text) && int.TryParse(text, out int value) && value > 0
            ? value
            : fallback;

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>错误正文进日志/界面前先截短 —— 有些网关会回一整页 HTML。</summary>
    private static string Trim(string body)
    {
        string text = body.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    /// <summary>
    /// 把响应体解成扁平的键值对。JSON 与表单编码<b>都要认</b>:
    /// 前者是规范推荐的,后者是 GitHub 等在没有 <c>Accept</c> 时的默认回法,
    /// 而中转/代理层有时会把 <c>Accept</c> 吃掉。
    /// </summary>
    private static Dictionary<string, string> Parse(string body, string? mediaType)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string trimmed = body.TrimStart();
        bool looksJson = trimmed.StartsWith('{')
                         || (mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false);
        if (looksJson && trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    result[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? "",
                        JsonValueKind.Null or JsonValueKind.Undefined => "",
                        // 嵌套对象(如错误详情)原样留着字面量,报错时至少看得见内容
                        _ => property.Value.ToString()
                    };
                }
                return result;
            }
            catch (JsonException)
            {
                // 落到表单解析:声称是 JSON 却给了别的东西时,别把整个流程炸掉
            }
        }
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = pair.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }
            result[UrlDecode(pair[..split])] = UrlDecode(pair[(split + 1)..]);
        }
        return result;
    }

    /// <summary>解析"每行一条 <c>名: 值</c>"的请求头;空行与没有冒号的行忽略。</summary>
    internal static IEnumerable<KeyValuePair<string, string>> ParseHeaders(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf(':');
            if (split <= 0)
            {
                continue;
            }
            string name = line[..split].Trim();
            string value = line[(split + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0)
            {
                yield return new KeyValuePair<string, string>(name, value);
            }
        }
    }

    /// <summary>解析"每行一条 <c>名=值</c>"的自填参数;空行与没有等号的行忽略。</summary>
    internal static IEnumerable<KeyValuePair<string, string>> ParsePairs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }
            string key = line[..split].Trim();
            if (key.Length > 0)
            {
                yield return new KeyValuePair<string, string>(key, line[(split + 1)..].Trim());
            }
        }
    }

    /// <summary>把回调地址里的查询串解成键值对(环回监听拿到的就是一条 GET 请求行)。</summary>
    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = pair.IndexOf('=');
            if (split < 0)
            {
                result[UrlDecode(pair)] = "";
                continue;
            }
            result[UrlDecode(pair[..split])] = UrlDecode(pair[(split + 1)..]);
        }
        return result;
    }

    /// <summary>
    /// 表单/查询串的百分号解码。<b>先把 <c>+</c> 换成空格再解转义</b>:查询串里的裸 <c>+</c> 就是空格,
    /// 而真正的加号会以 <c>%2B</c> 出现 —— 换在前面,它才不会被误伤。
    /// </summary>
    private static string UrlDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
