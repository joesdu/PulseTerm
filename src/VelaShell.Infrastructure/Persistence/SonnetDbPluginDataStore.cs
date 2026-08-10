using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPluginDataStore" /> 的 SonnetDB 实现:单集合
/// <see cref="SonnetDbEngine.PluginDataCollection" />,文档主键
/// <c>&lt;pluginId&gt;|kv|&lt;key&gt;</c> / <c>&lt;pluginId&gt;|secret|&lt;name&gt;</c>。
/// 隔离保证:插件 id 字符集不含 '|',能力实例只带自身前缀 —— 读不到别家数据;
/// 机密值经 <see cref="ISecretProtector" /> 加密后才入库。
/// </summary>
public sealed class SonnetDbPluginDataStore(SonnetDbEngine engine, ISecretProtector? protector) : IPluginDataStore
{
    /// <summary>文档体:值包一层对象(KV 值可为任意 JSON,文档要求对象根)。</summary>
    private sealed record ValueDoc(JsonElement V);

    private const string KvKind = "kv";
    private const string SecretKind = "secret";

    /// <inheritdoc />
    public IPluginStorage CreateStorage(string pluginId) => new DbStorage(this, pluginId);

    /// <inheritdoc />
    public ISecretsApi CreateSecrets(string pluginId) =>
        protector is null ? new UnavailableSecrets() : new DbSecrets(this, pluginId, protector);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken cancellationToken = default) =>
        await engine.WithCollectionAsync<IReadOnlyList<string>>(SonnetDbEngine.PluginDataCollection, store =>
            [.. store.Scan()
                     .Select(row => row.Id)
                     .Select(id => id.Split('|', 2)[0])
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal)],
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task PurgeAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        string prefix = pluginId + "|";
        await engine.WithCollectionAsync<object?>(SonnetDbEngine.PluginDataCollection, store =>
        {
            foreach (string id in store.Scan().Select(row => row.Id)
                                       .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                store.Delete(id);
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string DocId(string pluginId, string kind, string key) => $"{pluginId}|{kind}|{key}";

    private async Task<JsonElement?> GetRawAsync(string pluginId, string kind, string key, CancellationToken cancellationToken)
    {
        string? json = await engine.WithCollectionAsync(SonnetDbEngine.PluginDataCollection,
            store => store.Get(DocId(pluginId, kind, key))?.Json, cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<ValueDoc>(json)?.V;
    }

    private Task SetRawAsync(string pluginId, string kind, string key, JsonElement value, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(new ValueDoc(value));
        return engine.WithCollectionAsync<object?>(SonnetDbEngine.PluginDataCollection, store =>
        {
            store.Upsert(DocId(pluginId, kind, key), json);
            return null;
        }, cancellationToken);
    }

    private Task<bool> RemoveRawAsync(string pluginId, string kind, string key, CancellationToken cancellationToken) =>
        engine.WithCollectionAsync(SonnetDbEngine.PluginDataCollection, store =>
        {
            string id = DocId(pluginId, kind, key);
            if (store.Get(id) is null)
            {
                return false;
            }
            store.Delete(id);
            return true;
        }, cancellationToken);

    private Task<IReadOnlyList<string>> KeysRawAsync(string pluginId, string kind, CancellationToken cancellationToken)
    {
        string prefix = $"{pluginId}|{kind}|";
        return engine.WithCollectionAsync<IReadOnlyList<string>>(SonnetDbEngine.PluginDataCollection, store =>
            [.. store.Scan().Select(row => row.Id)
                     .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
                     .Select(id => id[prefix.Length..])
                     .Order(StringComparer.Ordinal)],
            cancellationToken);
    }

    /// <summary>绑定单插件的 KV 存储:主键前缀即隔离边界。</summary>
    private sealed class DbStorage(SonnetDbPluginDataStore owner, string pluginId) : IPluginStorage
    {
        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            JsonElement? value = await owner.GetRawAsync(pluginId, KvKind, key, cancellationToken).ConfigureAwait(false);
            return value is { } element ? element.Deserialize<T>() : default;
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            return owner.SetRawAsync(pluginId, KvKind, key, JsonSerializer.SerializeToElement(value), cancellationToken);
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            return owner.RemoveRawAsync(pluginId, KvKind, key, cancellationToken);
        }

        public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default) =>
            owner.KeysRawAsync(pluginId, KvKind, cancellationToken);
    }

    /// <summary>绑定单插件的机密存储:值先加密再入库,读出时解密。</summary>
    private sealed class DbSecrets(SonnetDbPluginDataStore owner, string pluginId, ISecretProtector protector) : ISecretsApi
    {
        public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            JsonElement? value = await owner.GetRawAsync(pluginId, SecretKind, name, cancellationToken).ConfigureAwait(false);
            return value is { ValueKind: JsonValueKind.String } element ? protector.Unprotect(element.GetString()) : null;
        }

        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(value);
            string protectedValue = protector.Protect(value)
                ?? throw new InvalidOperationException("Secret protector returned null for a non-null value.");
            return owner.SetRawAsync(pluginId, SecretKind, name, JsonSerializer.SerializeToElement(protectedValue), cancellationToken);
        }

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            return owner.RemoveRawAsync(pluginId, SecretKind, name, cancellationToken);
        }
    }
}
