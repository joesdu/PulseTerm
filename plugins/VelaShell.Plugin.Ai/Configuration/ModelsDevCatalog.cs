using System.Text.Json;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Configuration;

/// <summary>
/// 一个模型的规格:够把 <see cref="AiModelConfig" /> 里那几项<b>本来要用户手填</b>的东西填满。
/// </summary>
/// <param name="Id">模型 id(直接就是请求里要发的那个)。</param>
/// <param name="Name">显示名。</param>
/// <param name="ContextTokens">上下文窗口;0 = 未知。</param>
/// <param name="OutputTokens">单轮最大输出;0 = 未知。</param>
/// <param name="InputPrice">每百万输入 token 单价;0 = 未知。</param>
/// <param name="OutputPrice">每百万输出 token 单价。</param>
/// <param name="CachedInputPrice">每百万缓存命中输入 token 单价。</param>
/// <param name="Reasoning">这个模型支不支持思考。</param>
public sealed record ModelSpec(
    string Id,
    string Name,
    int ContextTokens,
    int OutputTokens,
    double InputPrice,
    double OutputPrice,
    double CachedInputPrice,
    bool Reasoning);

/// <summary>
/// 模型规格库,数据来自开源的 <b>models.dev</b>(<c>github.com/sst/models.dev</c>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>它与 <see cref="EndpointModelCatalog" /> 各知道一半,合起来才够用。</b>
/// 供应商自己的 <c>/v1/models</c> 只给一串 id,给不出上下文窗口和单价 —— 而那几项恰恰是
/// 本插件里最难填、填错了又<b>不报错</b>的东西(窗口填错,输入框下方的占比就是错的;
/// 单价填错,花费估算跟着错),何况订阅型的私有后端(ChatGPT 的 Codex 后端就是)根本没有那条接口;
/// 这一份记的正是 id + 窗口 + 单价 + 能力位,却不知道你接的这家中转站到底转发了哪几个。
/// 所以清单以端点为准、规格来这儿补,取舍见 <see cref="ModelPull" />。
/// </para>
/// <para>
/// <b>下载一次、瘦身落盘。</b>原始 <c>api.json</c> 有四百多万字节(两百多家供应商),
/// 但本插件只要每个模型的七个字段 —— 下载后当场转成精简索引再落盘(约五分之一大小),
/// 之后每次读都只解析这一份。缓存放插件私有数据目录,卸载插件时随之清除。
/// </para>
/// <para>
/// <b>拉不到不是错误。</b>没网、被墙、对方改版,一律退回已有的缓存;缓存也没有就退回
/// 目录里的出厂示例。模型清单是"锦上添花",不该拦住"这一家已经连上了"这件事。
/// </para>
/// </remarks>
/// <param name="context">插件上下文(用它的数据目录与日志)。</param>
public sealed class ModelsDevCatalog(IPluginContext context)
{
    /// <summary>数据源。</summary>
    public const string SourceUrl = "https://models.dev/api.json";

    /// <summary>缓存多久算新鲜。各家出新模型是以周计的事,一天一拉纯属浪费。</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>精简索引的落盘位置。</summary>
    private string CacheFile => Path.Combine(context.DataDirectory, "models-dev.json");

    /// <summary>本次进程内解析好的索引;第一次用到时才读盘。</summary>
    private Dictionary<string, List<ModelSpec>>? _index;

    /// <summary>缓存文件的时间(没有缓存时为 null)。</summary>
    public DateTimeOffset? CachedAt
    {
        get
        {
            try
            {
                return File.Exists(CacheFile) ? File.GetLastWriteTimeUtc(CacheFile) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>缓存不存在或已经过期。</summary>
    public bool IsStale => CachedAt is not { } at || DateTimeOffset.UtcNow - at > CacheLifetime;

    /// <summary>
    /// 取某家供应商的全部模型规格(按 id 排序);没有这一家、或还没有缓存时返回空。
    /// </summary>
    /// <param name="modelsDevId">models.dev 里的供应商 id(见 <see cref="ProviderCatalogEntry.ModelsDevId" />)。</param>
    public IReadOnlyList<ModelSpec> ForProvider(string? modelsDevId)
    {
        if (string.IsNullOrWhiteSpace(modelsDevId))
        {
            return [];
        }
        return Index().TryGetValue(modelsDevId, out List<ModelSpec>? models) ? models : [];
    }

    /// <summary>
    /// 给端点报上来的一串 id 配规格(见 <see cref="EndpointModelCatalog" />)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>清单以端点为准,规格来这儿补。</b> 返回的每条 <see cref="ModelSpec.Id" /> 一定是
    /// <paramref name="ids" /> 里那个原样的 id —— 请求里要发的是它,不是 models.dev 那边的写法。
    /// 配不上就返回一条只有 id 的空规格:窗口与单价留 0,而 <see cref="Apply" /> 只填非 0 的值,
    /// 于是用户已经填好的东西不会被"未知"覆盖掉。
    /// </para>
    /// <para>
    /// <b>配不上这一家时,跨供应商按 id 找,但只补容量、不补单价。</b> 自定义端点与中转站没有
    /// <see cref="ProviderCatalogEntry.ModelsDevId" />,只能这么找。同一个型号在谁家跑,
    /// 上下文窗口都是同一个数(那是模型自身的属性);<b>价目却是各家自己定的</b> ——
    /// 中转站普遍加价,照抄原厂单价会让花费估算<b>静默地</b>偏低,而那正是最难被发现的一类错。
    /// 宁可让单价空着:空着至少还写在脸上。
    /// </para>
    /// </remarks>
    /// <param name="modelsDevId">这一家在 models.dev 的 id;空表示那边没收录(自定义 / 自建)。</param>
    /// <param name="ids">端点报上来的模型 id。</param>
    public IReadOnlyList<ModelSpec> Describe(string? modelsDevId, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var own = new Dictionary<string, ModelSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelSpec spec in ForProvider(modelsDevId))
        {
            own[spec.Id] = spec;
        }
        var described = new List<ModelSpec>(ids.Count);
        foreach (string id in ids)
        {
            if (own.TryGetValue(id, out ModelSpec? exact))
            {
                described.Add(exact with { Id = id });
                continue;
            }
            described.Add(FindAnywhere(id) is { } loose
                ? loose with { Id = id, InputPrice = 0, OutputPrice = 0, CachedInputPrice = 0 }
                : new ModelSpec(id, id, 0, 0, 0, 0, 0, false));
        }
        return described;
    }

    /// <summary>
    /// 不限供应商,按 id 找一条规格。
    /// </summary>
    /// <remarks>
    /// 先找完全相同的;找不到再拿<b>最后一段</b>找一次 —— 中转站习惯给 id 加前缀
    /// (<c>anthropic/claude-sonnet-4</c>),剥掉前缀就能对上同一个型号。
    /// </remarks>
    private ModelSpec? FindAnywhere(string id)
    {
        string bare = id[(id.LastIndexOf('/') + 1)..];
        ModelSpec? loose = null;
        foreach (List<ModelSpec> specs in Index().Values)
        {
            foreach (ModelSpec spec in specs)
            {
                if (string.Equals(spec.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return spec;
                }
                if (loose is null && bare.Length > 0
                                  && string.Equals(spec.Id, bare, StringComparison.OrdinalIgnoreCase))
                {
                    loose = spec;
                }
            }
        }
        return loose;
    }

    /// <summary>
    /// 需要时把索引拉新(缓存还新鲜就直接返回 false,不发请求)。
    /// </summary>
    /// <param name="http">发请求用的客户端。</param>
    /// <param name="force">忽略新鲜度,强制重下。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>这次是否真的更新了缓存。</returns>
    public async Task<bool> RefreshAsync(HttpClient http, bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!force && !IsStale)
        {
            return false;
        }
        try
        {
            string payload = await http.GetStringAsync(SourceUrl, cancellationToken).ConfigureAwait(false);
            Dictionary<string, List<ModelSpec>> parsed = Parse(payload);
            if (parsed.Count == 0)
            {
                return false; // 对方改版了或者拿到一份空的,别拿它把好缓存盖掉
            }
            await File.WriteAllTextAsync(CacheFile, Serialize(parsed), cancellationToken).ConfigureAwait(false);
            _index = parsed;
            context.Log.Info($"models.dev: cached {parsed.Count} providers.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 没网/被墙/对方改版 —— 退回已有缓存,连接流程照常
            context.Log.Warn($"models.dev: refresh failed, keeping the cached copy: {ex.Message}");
            return false;
        }
    }

    private Dictionary<string, List<ModelSpec>> Index()
    {
        if (_index is not null)
        {
            return _index;
        }
        try
        {
            _index = File.Exists(CacheFile) ? Parse(File.ReadAllText(CacheFile)) : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            context.Log.Warn($"models.dev: the cached index could not be read: {ex.Message}");
            _index = [];
        }
        return _index;
    }

    /// <summary>
    /// 解析。<b>原始 <c>api.json</c> 与我们自己的精简索引都认</b> —— 两者的层级一样
    /// (供应商 → 模型 → 规格),只是字段名不同,靠"有没有 <c>models</c> 这一层"区分。
    /// 这样刷新与读缓存共用一条路径,不必写两份解析。
    /// </summary>
    internal static Dictionary<string, List<ModelSpec>> Parse(string json)
    {
        var result = new Dictionary<string, List<ModelSpec>>(StringComparer.OrdinalIgnoreCase);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }
            foreach (JsonProperty provider in document.RootElement.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                // 原始格式外面还包一层 {id,name,doc,models:{…}};精简索引直接就是模型表
                JsonElement models = provider.Value.TryGetProperty("models", out JsonElement nested)
                                     && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : provider.Value;
                var specs = new List<ModelSpec>();
                foreach (JsonProperty model in models.EnumerateObject())
                {
                    if (model.Value.ValueKind == JsonValueKind.Object
                        && IsUsableForChat(model.Name, model.Value)
                        && ReadSpec(model.Name, model.Value) is { } spec)
                    {
                        specs.Add(spec);
                    }
                }
                if (specs.Count > 0)
                {
                    specs.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                    result[provider.Name] = specs;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 这一条值不值得摆进模型下拉。<b>在解析时就筛掉</b>,缓存里存的就是干净的。
    /// </summary>
    /// <remarks>
    /// 三条判据,全部取自数据本身,不猜:
    /// <list type="bullet">
    /// <item><c>status == "deprecated"</c> —— 已下架。上游明确标了的(线上两百来个),
    /// 摆出来只会让人选中一个用不了的型号。<c>beta</c> 保留:那是能用的。</item>
    /// <item>输出上限为 0 —— 画图那类根本不产出 token,进聊天下拉没有意义。</item>
    /// <item>id 里带 <c>embedding</c> —— 向量模型。这一条是<b>名字启发式</b>,因为
    /// 上游没有能把它和聊天模型分开的字段(它的 <c>limit.output</c> 填的是向量维度,
    /// 不是 token 数)。风险很低:没有哪个聊天模型叫 embedding。</item>
    /// </list>
    /// </remarks>
    private static bool IsUsableForChat(string key, JsonElement model)
    {
        if (string.Equals(Text(model, "status"), "deprecated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string id = Text(model, "id") ?? key;
        if (id.Contains("embedding", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // 精简缓存里没有 limit 这一层,那时不判(存进去的本来就是筛过的)
        JsonElement limit = Object(model, "limit");
        return limit.ValueKind != JsonValueKind.Object || Number(limit, "output") is not (null or <= 0);
    }

    /// <summary>读一条模型记录;两套字段名都试(原始 / 精简)。</summary>
    private static ModelSpec? ReadSpec(string key, JsonElement model)
    {
        string id = Text(model, "id") ?? key;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        JsonElement limit = Object(model, "limit");
        JsonElement cost = Object(model, "cost");
        return new ModelSpec(
            id,
            Text(model, "name") ?? id,
            Number(limit, "context") ?? Number(model, "ctx") ?? 0,
            Number(limit, "output") ?? Number(model, "max") ?? 0,
            Decimal(cost, "input") ?? Decimal(model, "pin") ?? 0,
            Decimal(cost, "output") ?? Decimal(model, "pout") ?? 0,
            Decimal(cost, "cache_read") ?? Decimal(model, "pcache") ?? 0,
            Bool(model, "reasoning") ?? Bool(model, "think") ?? false);
    }

    /// <summary>写成精简索引:每个模型只留七个字段,体积约为原始的五分之一。</summary>
    internal static string Serialize(Dictionary<string, List<ModelSpec>> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var slim = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach ((string provider, List<ModelSpec> models) in index)
        {
            var table = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (ModelSpec spec in models)
            {
                table[spec.Id] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = spec.Name,
                    ["ctx"] = spec.ContextTokens,
                    ["max"] = spec.OutputTokens,
                    ["pin"] = spec.InputPrice,
                    ["pout"] = spec.OutputPrice,
                    ["pcache"] = spec.CachedInputPrice,
                    ["think"] = spec.Reasoning
                };
            }
            slim[provider] = table;
        }
        return JsonSerializer.Serialize(slim);
    }

    private static JsonElement Object(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out int number)
            ? number
            : null;

    private static double? Decimal(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out double number)
            ? number
            : null;

    private static bool? Bool(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out JsonElement value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// 把规格填进模型配置。
    /// </summary>
    /// <remarks>
    /// <b>只填知道的,不知道的一律不动</b> —— 上游没给窗口/单价时,把用户已经填好的值
    /// 覆盖成 0 比不填还糟(窗口 0 会让上下文占比整个消失,单价 0 会让花费估算静默停掉)。
    /// </remarks>
    /// <param name="model">要填的模型配置。</param>
    /// <param name="spec">规格。</param>
    public static void Apply(AiModelConfig model, ModelSpec spec)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(spec);
        model.Model = spec.Id;
        if (spec.ContextTokens > 0)
        {
            model.MaxInputTokens = spec.ContextTokens;
        }
        if (spec.OutputTokens > 0)
        {
            model.MaxTokens = spec.OutputTokens;
        }
        if (spec.InputPrice > 0)
        {
            model.InputPricePerMillion = spec.InputPrice;
        }
        if (spec.OutputPrice > 0)
        {
            model.OutputPricePerMillion = spec.OutputPrice;
        }
        if (spec.CachedInputPrice > 0)
        {
            model.CachedInputPricePerMillion = spec.CachedInputPrice;
        }
    }

    /// <summary>
    /// 把清单<b>落成真正可选的模型</b>,并返回这一家现在共有多少个模型。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只把 id 存进 <see cref="AiProvider.AvailableModels" /> 是不够的 —— 顶栏那个模型下拉读的是
    /// <see cref="AiProvider.Models" />(经 <see cref="AiSettings.ResolveModels" />),
    /// 清单不落进去,用户在界面上就一个也挑不着,"拉取"等于没做。
    /// </para>
    /// <para>
    /// <b>已有的模型原地更新,不重建</b>:<see cref="AiSettings.ActiveModelId" /> 指着某个
    /// <see cref="AiModelConfig.Id" />,重建会让当前选中的模型凭空消失;而且用户可能已经给它
    /// 改过名字、调过思考档位或专用提示词,那些都得留着 —— 这里只补规格,不动用户改过的东西。
    /// </para>
    /// </remarks>
    /// <param name="provider">要填充的供应商。</param>
    /// <param name="specs">拿到的规格清单。</param>
    public static int Materialise(AiProvider provider, IReadOnlyList<ModelSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0)
        {
            return provider.Models.Count;
        }

        // 出厂那一条(通常是目录给的示例)先对齐到清单里真实存在的型号 ——
        // 它的 Id 可能正被 ActiveModelId 指着,换掉的话当前选中的模型就没了
        if (provider.Models.Count > 0 && ChooseDefault(provider.Models[0].Model, specs) is { } chosen)
        {
            Apply(provider.Models[0], chosen);
        }

        var known = new HashSet<string>(
            provider.Models.Select(m => m.Model).Where(id => id.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (ModelSpec spec in specs)
        {
            if (!known.Add(spec.Id))
            {
                // 已经有了:补上规格(用户没填过的窗口/单价这时才有值),名字之类一律不碰
                AiModelConfig existing = provider.Models.First(
                    m => string.Equals(m.Model, spec.Id, StringComparison.OrdinalIgnoreCase));
                Apply(existing, spec);
                continue;
            }
            var model = new AiModelConfig();
            Apply(model, spec);
            provider.Models.Add(model);
        }
        return provider.Models.Count;
    }

    /// <summary>
    /// 拿到清单后这一家该默认用哪个模型。
    /// </summary>
    /// <remarks>
    /// 目录里的出厂示例优先 —— 那是挑过的。它已经不在清单里了(型号更新换代),
    /// 就退回<b>同前缀里最新的那个</b>:<c>gpt-5-codex</c> 没了,选 <c>gpt-5.3-codex</c>
    /// 显然比选字母序第一个的 <c>gpt-3.5</c> 合理。都对不上才取第一个。
    /// </remarks>
    /// <param name="preferred">目录里的出厂示例。</param>
    /// <param name="available">拿到的清单。</param>
    public static ModelSpec? ChooseDefault(string preferred, IReadOnlyList<ModelSpec> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
        {
            return null;
        }
        foreach (ModelSpec spec in available)
        {
            if (string.Equals(spec.Id, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }
        // 出厂示例没了:按"最长的共同前缀"找同一族里最新的那个
        ModelSpec? best = null;
        int bestScore = 0;
        foreach (ModelSpec spec in available)
        {
            int score = CommonPrefix(spec.Id, preferred);
            if (score > bestScore || (score == bestScore && best is not null
                                                         && string.CompareOrdinal(spec.Id, best.Id) > 0))
            {
                best = spec;
                bestScore = score;
            }
        }
        // 前缀太短(两三个字母)说明根本不是一族,别硬凑
        return bestScore >= 4 ? best : available[0];
    }

    private static int CommonPrefix(string left, string right)
    {
        int max = Math.Min(left.Length, right.Length);
        int count = 0;
        while (count < max && char.ToLowerInvariant(left[count]) == char.ToLowerInvariant(right[count]))
        {
            count++;
        }
        return count;
    }
}
