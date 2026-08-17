using System.Text.Json;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IPluginStorage" /> 的内存实现。值经 JSON 往返(与真实实现同语义:
/// 存进去的对象引用不共享,序列化不了的类型同样报错)。线程安全。
/// </summary>
public sealed class InMemoryStorage : IPluginStorage
{
    private readonly Dictionary<string, JsonElement> _entries = [with(StringComparer.Ordinal)];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(key, out JsonElement element)
                ? element.Deserialize<T>()
                : default);
        }
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            _entries[key] = JsonSerializer.SerializeToElement(value);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            return Task.FromResult(_entries.Remove(key));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _entries.Keys]);
        }
    }
}
