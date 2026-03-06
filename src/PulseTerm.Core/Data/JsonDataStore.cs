using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PulseTerm.Core.Data;

public class JsonDataStore
{
    private readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly SemaphoreSlim _dictionaryLock = new(1, 1);
    private readonly ILogger<JsonDataStore>? _logger;
    
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonDataStore(ILogger<JsonDataStore>? logger = null)
    {
        _logger = logger;
    }

    public async Task<T?> LoadAsync<T>(string filePath, CancellationToken cancellationToken = default) where T : class, new()
    {
        var fileLock = await GetFileLockAsync(filePath).ConfigureAwait(false);
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            if (!File.Exists(filePath))
            {
                return new T();
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
                
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupt JSON detected in {FilePath}, resetting to defaults", filePath);
            return new T();
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task SaveAsync<T>(string filePath, T data, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fileLock = await GetFileLockAsync(filePath).ConfigureAwait(false);
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(
                        filePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);
                        
                    await JsonSerializer.SerializeAsync(stream, data, _options, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    await Task.Delay((int)Math.Pow(2, attempt) * 100, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<SemaphoreSlim> GetFileLockAsync(string filePath)
    {
        await _dictionaryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_fileLocks.TryGetValue(filePath, out var fileLock))
            {
                fileLock = new SemaphoreSlim(1, 1);
                _fileLocks[filePath] = fileLock;
            }
            return fileLock;
        }
        finally
        {
            _dictionaryLock.Release();
        }
    }
}
