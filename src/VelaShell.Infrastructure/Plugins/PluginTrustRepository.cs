using System.Text.Json;
using VelaShell.Core.Data;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>受保护的插件发布者信任与安装凭据快照。</summary>
public sealed class PluginTrustState
{
    /// <summary>持久化结构版本。</summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>是否已经为升级前存在的插件目录建立过一次基线。</summary>
    public bool LegacyInstallMigrationCompleted { get; set; }
    /// <summary>用户明确信任的发布者。</summary>
    public List<TrustedPluginPublisher> Publishers { get; set; } = [];
    /// <summary>按插件 id 索引的安装内容收据。</summary>
    public Dictionary<string, InstalledPluginReceipt> Receipts { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>用户通过独立渠道核对后信任的发布者。</summary>
public sealed record TrustedPluginPublisher(string PublicKey, string Fingerprint, DateTimeOffset TrustedAtUtc);

/// <summary>安装时对插件目录内容建立的受保护基线。</summary>
public sealed record InstalledPluginReceipt(
    string PluginId,
    string ContentSha256,
    string? PackageSha256,
    string? PublisherPublicKey,
    bool LegacyAdopted,
    DateTimeOffset InstalledAtUtc);

/// <summary>
/// 信任状态存入 SonnetDB，但安全边界来自 <see cref="ISecretProtector" /> 的 AES-GCM 认证标签，
/// 而不是数据库格式本身。密文被替换、截断或改写时解密失败并 fail closed。
/// </summary>
public sealed class PluginTrustRepository(
    SonnetDbEngine engine,
    ISecretProtector protector,
    string? legacyJsonPath = null)
{
    private const string DocumentId = "plugin_trust_v1";
    private const string ProtectedPrefix = "enc1:";
    private sealed record ProtectedDocument(string Payload);

    /// <summary>读取并认证信任状态；任何格式、密文或认证异常都拒绝降级为明文。</summary>
    public async Task<PluginTrustState> LoadAsync(CancellationToken cancellationToken = default)
    {
        string? stored = await engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection,
            collection => collection.Get(DocumentId)?.Json, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            ProtectedDocument document = JsonSerializer.Deserialize<ProtectedDocument>(stored)
                ?? throw new InvalidDataException("Plugin trust document is empty.");
            if (string.IsNullOrWhiteSpace(document.Payload)
                || !document.Payload.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Plugin trust document is not authenticated.");
            }
            string plaintext = protector.Unprotect(document.Payload)
                ?? throw new InvalidDataException("Plugin trust document could not be decrypted.");
            if (plaintext.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Plugin trust document authentication failed.");
            }
            try
            {
                PluginTrustState loadedState = JsonSerializer.Deserialize<PluginTrustState>(plaintext)
                                               ?? throw new InvalidDataException("Plugin trust payload is empty.");
                ValidateAndNormalize(loadedState);
                return loadedState;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Plugin trust payload is corrupt.", ex);
            }
        }

        var state = new PluginTrustState();
        if (legacyJsonPath is { } path && File.Exists(path))
        {
            try
            {
                foreach (string key in JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path, cancellationToken)) ?? [])
                {
                    if (string.IsNullOrWhiteSpace(key) || state.Publishers.Any(p => p.PublicKey == key))
                    {
                        continue;
                    }
                    state.Publishers.Add(new(key, VelaShell.PluginSdk.Packaging.VpxContainer.PublicKeyFingerprint(key), DateTimeOffset.UtcNow));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                throw new InvalidDataException("Legacy plugin publisher trust file could not be migrated safely.", ex);
            }
        }
        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        if (legacyJsonPath is { } oldPath && File.Exists(oldPath))
        {
            File.Move(oldPath, oldPath + ".migrated", overwrite: true);
        }
        return state;
    }

    /// <summary>认证加密并原子更新 SonnetDB 中的信任状态文档。</summary>
    public Task SaveAsync(PluginTrustState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateAndNormalize(state);
        string plaintext = JsonSerializer.Serialize(state);
        string payload = protector.Protect(plaintext)
            ?? throw new InvalidOperationException("Secret protector returned null for plugin trust state.");
        if (!payload.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Plugin trust state was not protected.");
        }
        string document = JsonSerializer.Serialize(new ProtectedDocument(payload));
        return engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, collection =>
        {
            collection.Upsert(DocumentId, document);
            return null;
        }, cancellationToken);
    }

    private static void ValidateAndNormalize(PluginTrustState state)
    {
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported plugin trust schema version {state.SchemaVersion}.");
        }
        state.Publishers ??= [];
        state.Receipts = state.Receipts is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(state.Receipts, StringComparer.OrdinalIgnoreCase);
        var seenPublishers = new HashSet<string>(StringComparer.Ordinal);
        foreach (TrustedPluginPublisher publisher in state.Publishers)
        {
            string expected;
            try
            {
                expected = VelaShell.PluginSdk.Packaging.VpxContainer.PublicKeyFingerprint(publisher.PublicKey);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Plugin trust state contains an invalid publisher key.", ex);
            }
            if (!seenPublishers.Add(publisher.PublicKey)
                || !string.Equals(expected, publisher.Fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Plugin trust state contains inconsistent publisher identity data.");
            }
        }
        foreach ((string id, InstalledPluginReceipt receipt) in state.Receipts)
        {
            if (!string.Equals(id, receipt.PluginId, StringComparison.OrdinalIgnoreCase)
                || !IsSha256(receipt.ContentSha256)
                || receipt.PackageSha256 is not null && !IsSha256(receipt.PackageSha256))
            {
                throw new InvalidDataException($"Plugin trust state contains an invalid installation receipt for '{id}'.");
            }
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
