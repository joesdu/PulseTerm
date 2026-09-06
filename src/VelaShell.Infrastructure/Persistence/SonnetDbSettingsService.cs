using System.Text.Json;
using SonnetDB.Documents;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 文档集合 <c>app_config</c> 的设置服务:
/// settings 与 state 各为一个固定 Id 的 JSON 文档。首次运行导入既有 settings.json / state.json。
/// </summary>
public sealed class SonnetDbSettingsService(SonnetDbEngine engine, IReadOnlyList<string>? legacyDirectories = null) : ISettingsService
{
    private const string SettingsDocId = "settings";
    private const string StateDocId = "state";

    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly IReadOnlyList<string> _legacyDirectories = legacyDirectories ?? [];

    /// <summary>
    /// settings 文档的 JSON 缓存:设置是读热点(每次连接/每个传输文件都读),
    /// 而引擎全局锁串行所有集合操作。缓存序列化文本、按次反序列化,调用方仍各拿独立
    /// 实例(可安全修改后再保存),读路径却不再进锁/碰盘。保存时同步刷新。
    /// </summary>
    private volatile string? _settingsJsonCache;

    /// <summary>
    /// 只读共享快照:与 <see cref="_settingsJsonCache" /> 同源,但省掉每次调用的反序列化。
    /// 只读调用方(传输调优、代理解析、连接工厂)走 <c>GetSnapshotAsync()</c> 拿它;
    /// 需要改写的调用方仍走 <see cref="GetSettingsAsync" /> 拿独立实例。
    /// 保存时整体替换为**新反序列化的**实例(不直接引用调用方传进来的对象,
    /// 否则调用方后续继续改那个对象会把共享快照一起改掉)。
    /// </summary>
    private volatile AppSettings? _snapshot;

    /// <inheritdoc />
    public AppSettings? CurrentSnapshot => _snapshot;

    /// <summary>设置保存成功后触发,携带刚持久化的设置快照,供订阅方刷新缓存或界面。</summary>
    public event Action<AppSettings>? SettingsSaved;

    /// <summary>读取应用设置:优先命中 JSON 缓存,未命中则从文档集合读取或首次导入并归一化。</summary>
    public async Task<AppSettings> GetSettingsAsync()
    {
        if (_settingsJsonCache is { } cached && SonnetDbJson.Deserialize<AppSettings>(cached) is { } fromCache)
        {
            fromCache.Normalize();
            return fromCache;
        }
        AppSettings settings = await GetOrImportAsync<AppSettings>(SettingsDocId, "settings.json").ConfigureAwait(false);
        settings.Normalize();
        string serialized = SonnetDbJson.Serialize(settings);
        _settingsJsonCache = serialized;
        _snapshot = DeserializeNormalized(serialized);
        return settings;
    }

    /// <summary>持久化应用设置,同步刷新 JSON 缓存与只读快照,并触发 <see cref="SettingsSaved"/>。</summary>
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string json = SonnetDbJson.Serialize(settings);
        await UpsertJsonAsync(SettingsDocId, json).ConfigureAwait(false);
        _settingsJsonCache = json;
        _snapshot = DeserializeNormalized(json);
        MirrorRenderMode(settings);
        SettingsSaved?.Invoke(settings);
    }

    /// <summary>从 JSON 反序列化出一份归一化后的独立实例;失败时返回 null(退回按需加载)。</summary>
    private static AppSettings? DeserializeNormalized(string json)
    {
        if (SonnetDbJson.Deserialize<AppSettings>(json) is not { } settings)
        {
            return null;
        }
        settings.Normalize();
        return settings;
    }

    /// <summary>读取应用运行状态:从文档集合读取,不存在时首次导入既有 state.json 或返回新实例。</summary>
    public async Task<AppState> GetStateAsync() => await GetOrImportAsync<AppState>(StateDocId, "state.json").ConfigureAwait(false);

    /// <summary>持久化应用运行状态到固定 Id 的状态文档。</summary>
    public async Task SaveStateAsync(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        await UpsertAsync(StateDocId, state).ConfigureAwait(false);
    }

    private async Task<T> GetOrImportAsync<T>(string docId, string legacyFileName) where T : class, new()
    {
        T? existing = await _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
        {
            DocumentRow? row = store.Get(docId);
            return row is null ? null : SonnetDbJson.Deserialize<T>(row.Json);
        }).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }
        T? imported = await TryImportLegacyAsync<T>(legacyFileName).ConfigureAwait(false);
        if (imported is null)
        {
            return new();
        }
        await UpsertAsync(docId, imported).ConfigureAwait(false);
        return imported;
    }

    private Task UpsertAsync<T>(string docId, T value) where T : class => UpsertJsonAsync(docId, SonnetDbJson.Serialize(value));

    private async Task UpsertJsonAsync(string docId, string json)
    {
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, store =>
        {
            store.Upsert(docId, json);
            return null;
        }).ConfigureAwait(false);
    }

    private async Task<T?> TryImportLegacyAsync<T>(string fileName) where T : class
    {
        foreach (string directory in _legacyDirectories)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                T? value = SonnetDbJson.Deserialize<T>(await File.ReadAllTextAsync(path).ConfigureAwait(false));
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 旧文件损坏时跳过。
            }
        }
        return null;
    }

    /// <summary>
    /// 把渲染模式镜像成一个单行文件。渲染后端要在 Avalonia 初始化前定下,那时数据库还没起来,
    /// 启动路径读不了这份设置 —— 见 <see cref="VelaShellStoragePaths.RenderModeFile" />。
    /// 写失败只意味着下次启动沿用上一次的模式,不值得打断保存。
    /// </summary>
    private static void MirrorRenderMode(AppSettings settings)
    {
        try
        {
            var paths = new VelaShellStoragePaths();
            Directory.CreateDirectory(paths.RootDirectory);
            File.WriteAllText(
                paths.RenderModeFile,
                settings.Appearance.HardwareAcceleration ? "gpu" : "software"
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为。
        }
    }
}
