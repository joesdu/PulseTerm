namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>这一次拉取从哪儿拿到的清单。</summary>
public enum ModelSource
{
    /// <summary>两条路都没拿到东西 —— 保持原样。</summary>
    None,

    /// <summary>端点自己报的(见 <see cref="EndpointModelCatalog" />)。</summary>
    Endpoint,

    /// <summary>models.dev 收录的(见 <see cref="ModelsDevCatalog" />)。</summary>
    Catalogue
}

/// <summary>拉取结果。</summary>
/// <param name="Source">清单是从哪儿来的。</param>
/// <param name="Listed">这一次拉到多少个模型 id。</param>
/// <param name="Total">拉完之后这一家共有多少个模型配置。</param>
public readonly record struct ModelPullResult(ModelSource Source, int Listed, int Total);

/// <summary>
/// 「拉取模型」这一件事本身:问清单 → 配规格 → 落成可选的模型。
/// </summary>
/// <remarks>
/// <para>
/// 单独摆一个类,是因为两处界面都要做同一件事:「连接供应商」页接上之后自动拉一次
/// (<c>ProviderSetupView</c>),「模型设置」页那个按钮再拉一次(<c>SettingsView</c>)。
/// 两边各写一份的话,今天改了取舍、明天就只有一边跟上了。
/// </para>
/// <para>
/// <b>落盘与刷界面不在这儿</b> —— 那两件事两边的做法不一样(一个还要重建展开区、
/// 一个还要重建左栏),硬塞进来反而要传一堆回调。这里只改 <see cref="AiProvider" /> 这个对象。
/// </para>
/// </remarks>
public static class ModelPull
{
    /// <summary>拉取用的超时。列表接口都是小回应,慢过这个数基本就是不通。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 给一家供应商拉一次模型清单。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>端点优先。</b> 先问端点它实际供应什么(那是唯一知道这件事的人 —— 中转站只转发
    /// 其中一部分,自建的 Ollama 装了哪几个权重,目录那边一概不知),再拿 id 去 models.dev
    /// 配窗口与单价。端点没有这条接口、或请求失败时,整条退回 models.dev 的清单,
    /// 也就是这个功能存在之前的行为。
    /// </para>
    /// <para>
    /// <b>一步都不抛异常。</b> 拉模型是"锦上添花":它失败不该让"这一家已经连上了"这件事打折扣,
    /// 更不该把异常冒到登录流程的错误处理里去。拿不到就返回 <see cref="ModelSource.None" />。
    /// </para>
    /// </remarks>
    /// <param name="provider">要填充的供应商(就地修改)。</param>
    /// <param name="modelsDevId">这一家在 models.dev 的 id;空表示那边没收录。</param>
    /// <param name="catalogue">规格库。</param>
    /// <param name="store">设置存储(用它解出这一家的凭据)。</param>
    /// <param name="apiKeyOverride">设置页表单里还没保存的那把 Key;null = 用已存的。</param>
    /// <param name="force">用户明确点了「拉取模型」—— 这时还拿七天前的规格缓存糊弄他就没意义了。</param>
    /// <param name="cancellationToken">取消。</param>
    public static async Task<ModelPullResult> RunAsync(AiProvider provider, string? modelsDevId,
        ModelsDevCatalog catalogue, AiSettingsStore store, string? apiKeyOverride = null, bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(store);

        using var http = new HttpClient { Timeout = Timeout };
        // 规格缓存:没点拉取时只在过期了才重下(见 ModelsDevCatalog.CacheLifetime)——
        // 各家出新模型是以周计的事,为一次登录拖四百万字节下来纯属浪费
        await catalogue.RefreshAsync(http, force, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> ids = await ListAsync(provider, store, apiKeyOverride, http, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ModelSpec> specs = ids.Count > 0
            ? catalogue.Describe(modelsDevId, ids)
            : catalogue.ForProvider(modelsDevId);
        if (specs.Count == 0)
        {
            return new ModelPullResult(ModelSource.None, 0, provider.Models.Count);
        }

        provider.AvailableModels = [.. specs.Select(s => s.Id)];
        int total = ModelsDevCatalog.Materialise(provider, specs);
        // 展开状态交回自动判断:用户上次表态时面对的是另一份清单(往往是出厂那一条),
        // 而这一拉可能就是三百个 —— 拿旧决定套新长度,左栏一进去就是滚不到底的长龙。
        // 见 AiProvider.ModelsExpanded 与设置页的 AutoCollapseFrom。
        provider.ModelsExpanded = null;
        return new ModelPullResult(ids.Count > 0 ? ModelSource.Endpoint : ModelSource.Catalogue, specs.Count, total);
    }

    /// <summary>问端点要清单;问不到(没这条接口、没网、401)一律返回空,由调用方回落。</summary>
    private static async Task<IReadOnlyList<string>> ListAsync(AiProvider provider, AiSettingsStore store,
        string? apiKeyOverride, HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            ProviderCredential credential = apiKeyOverride is null
                ? await store.ResolveProviderCredentialAsync(provider, cancellationToken).ConfigureAwait(false)
                : ProviderCredential.Key(apiKeyOverride);
            // 订阅登录换回来的令牌可能自带地址(见 OAuthTokens.BaseUrl),那时以它为准
            string baseUrl = string.IsNullOrWhiteSpace(credential.BaseUrl) ? provider.BaseUrl : credential.BaseUrl;
            return await EndpointModelCatalog
                         .FetchAsync(http, baseUrl, provider.DefaultProtocol, credential, cancellationToken)
                         .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }
}
