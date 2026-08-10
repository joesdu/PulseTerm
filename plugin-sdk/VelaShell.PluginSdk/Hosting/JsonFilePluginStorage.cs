using System.Text.Json;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.PluginSdk.Hosting;

/// <summary>
/// <see cref="IPluginStorage" /> 的文件实现:插件数据目录下单个 <c>storage.json</c>,
/// 惰性加载、原子落盘(临时文件 + 替换)、信号量串行化。进程内宿主与
/// PluginHost 进程共用(隔离模式下存储在插件进程本地执行,不走 RPC)。
/// </summary>
public sealed class JsonFilePluginStorage(string dataDirectory) : IPluginStorage
{
    private readonly string _filePath = Path.Combine(dataDirectory, "storage.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, JsonElement>? _entries;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> entries = LoadLocked();
            return entries.TryGetValue(key, out JsonElement element)
                ? element.Deserialize<T>()
                : default;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> entries = LoadLocked();
            entries[key] = JsonSerializer.SerializeToElement(value);
            SaveLocked(entries);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, JsonElement> entries = LoadLocked();
            if (!entries.Remove(key))
            {
                return false;
            }
            SaveLocked(entries);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. LoadLocked().Keys];
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, JsonElement> LoadLocked()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        try
        {
            _entries = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_filePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 损坏的存储文件不应让插件永远激活失败:从空表重新开始,旧文件保留为 .bak 供排查。
            TryBackupCorruptFile();
            _entries = [];
        }
        return _entries;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }
        }
        catch
        {
            // 尽力而为。
        }
    }

    private void SaveLocked(Dictionary<string, JsonElement> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
