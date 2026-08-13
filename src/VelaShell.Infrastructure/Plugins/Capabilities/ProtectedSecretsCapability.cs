using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.PluginSdk.Secrets;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// <see cref="ISecretsApi" /> 的宿主实现:值经 <see cref="ISecretProtector" /> 加密后
/// 落在插件数据目录的 <c>secrets.json</c>(与宿主自身凭据不同命名空间)。
/// 隔离模式下本实现只在宿主进程运行 —— 机密不出主进程的磁盘管辖。
/// </summary>
internal sealed class ProtectedSecretsCapability(string dataDirectory, ISecretProtector protector) : ISecretsApi
{
    private readonly string _filePath = Path.Combine(dataDirectory, "secrets.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _entries;

    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadLocked().TryGetValue(name, out string? protectedValue)
                ? protector.Unprotect(protectedValue)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        string protectedValue = protector.Protect(value)
            ?? throw new InvalidOperationException("Secret protector returned null for a non-null value.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> entries = LoadLocked();
            entries[name] = protectedValue;
            SaveLocked(entries);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> entries = LoadLocked();
            if (!entries.Remove(name))
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

    private Dictionary<string, string> LoadLocked()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        try
        {
            _entries = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 损坏即弃(机密没有"半恢复"的意义),留 .bak 供排查。
            try
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }
            catch
            {
                // 尽力而为。
            }
            _entries = [];
        }
        return _entries;
    }

    private void SaveLocked(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, _filePath, overwrite: true);
    }
}

/// <summary>机密能力不可用(无保护器)时的空实现:绝不明文兜底,直接报不可用。</summary>
internal sealed class UnavailableSecrets : ISecretsApi
{
    private static InvalidOperationException Unavailable() =>
        new InvalidOperationException("Secrets capability is unavailable in this host (no secret protector).");

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromException<string?>(Unavailable());
    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromException<bool>(Unavailable());
}

/// <summary>剪贴板能力不可用(无 UI 宿主)时的空实现。</summary>
internal sealed class UnavailableClipboard : PluginSdk.Clipboard.IClipboardApi
{
    private static InvalidOperationException Unavailable() =>
        new InvalidOperationException("Clipboard capability is unavailable in this host.");

    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) => Task.FromException<string?>(Unavailable());
    public Task SetTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
}

/// <summary>终端能力不可用(无 UI 宿主)时的空实现。</summary>
internal sealed class UnavailableTerminal : PluginSdk.Terminal.ITerminalApi
{
    private static InvalidOperationException Unavailable() =>
        new InvalidOperationException("Terminal capability is unavailable in this host.");

    public Task<string> GetOutputAsync(string sessionId, int maxLines = 1000, CancellationToken cancellationToken = default)
        => Task.FromException<string>(Unavailable());

    public Task<IReadOnlyList<PluginSdk.Terminal.TerminalMatch>> SearchOutputAsync(string sessionId, string pattern,
        bool isRegex = false, int maxMatches = 100, CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<PluginSdk.Terminal.TerminalMatch>>(Unavailable());

    public Task WriteAsync(string sessionId, string input, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable());
}
