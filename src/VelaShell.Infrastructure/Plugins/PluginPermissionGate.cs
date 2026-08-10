using VelaShell.Core.Data;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>用户对一次敏感能力请求的裁决。</summary>
public enum PluginPermissionDecision
{
    /// <summary>拒绝本次。</summary>
    Deny,

    /// <summary>仅允许本次。</summary>
    AllowOnce,

    /// <summary>本次运行期间允许(应用退出即失效)。</summary>
    AllowSession,

    /// <summary>始终允许(按插件持久化,可在插件管理页撤销)。</summary>
    AllowAlways
}

/// <summary>授权对话框 SPI(由 UI 层实现:弹窗展示插件、目标与内容预览,给出四种选择)。</summary>
public interface IPluginPermissionPrompt
{
    /// <summary>请求用户裁决一次终端回写。</summary>
    Task<PluginPermissionDecision> RequestTerminalWriteAsync(string pluginId, string sessionLabel, string inputPreview,
        CancellationToken cancellationToken);
}

/// <summary>
/// 终端回写的授权闸(蓝图 06 的最小可用形态):
/// 始终允许 → SonnetDB 持久化(按插件);本次运行允许 → 内存;仅本次 → 放行一次;
/// 拒绝 → 本次拒绝。同插件的并发请求串行化,不弹对话框风暴。无提示器的宿主一律拒绝。
/// </summary>
public sealed class PluginPermissionGate(IAppDataStore? store, IPluginPermissionPrompt? prompt)
{
    private const string Collection = "app_config";
    private const string DocumentId = "plugin_terminal_write_permissions";

    private sealed record PermissionsDoc(List<string> AlwaysAllow);

    private readonly HashSet<string> _sessionAllowed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private HashSet<string>? _alwaysAllowed;

    /// <summary>是否放行本次终端回写;必要时弹授权对话框。</summary>
    public async Task<bool> CheckTerminalWriteAsync(string pluginId, string sessionLabel, string inputPreview,
        CancellationToken cancellationToken)
    {
        if (await IsAlwaysAllowedAsync(pluginId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        lock (_sessionAllowed)
        {
            if (_sessionAllowed.Contains(pluginId))
            {
                return true;
            }
        }
        if (prompt is null)
        {
            return false; // 无 UI 可问 = 不放行(绝不静默授权)
        }
        // 串行化提问:并发写入只弹一个框;等待期间别人拿到的授权对后续请求同样生效。
        await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsAlwaysAllowedAsync(pluginId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            lock (_sessionAllowed)
            {
                if (_sessionAllowed.Contains(pluginId))
                {
                    return true;
                }
            }
            PluginPermissionDecision decision = await prompt
                .RequestTerminalWriteAsync(pluginId, sessionLabel, inputPreview, cancellationToken).ConfigureAwait(false);
            switch (decision)
            {
                case PluginPermissionDecision.AllowOnce:
                    return true;
                case PluginPermissionDecision.AllowSession:
                    lock (_sessionAllowed)
                    {
                        _sessionAllowed.Add(pluginId);
                    }
                    return true;
                case PluginPermissionDecision.AllowAlways:
                    await PersistAlwaysAsync(pluginId, cancellationToken).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _promptGate.Release();
        }
    }

    /// <summary>撤销某插件的授权(持久 + 会话内),插件管理页使用。</summary>
    public async Task RevokeAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        lock (_sessionAllowed)
        {
            _sessionAllowed.Remove(pluginId);
        }
        HashSet<string> always = await LoadAlwaysAsync(cancellationToken).ConfigureAwait(false);
        if (always.Remove(pluginId) && store is not null)
        {
            await store.UpsertAsync(Collection, DocumentId, new PermissionsDoc([.. always]), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>某插件当前是否持有任何回写授权(持久或会话内),插件管理页展示用。</summary>
    public async Task<bool> HasGrantAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (await IsAlwaysAllowedAsync(pluginId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        lock (_sessionAllowed)
        {
            return _sessionAllowed.Contains(pluginId);
        }
    }

    private async Task<bool> IsAlwaysAllowedAsync(string pluginId, CancellationToken cancellationToken) =>
        (await LoadAlwaysAsync(cancellationToken).ConfigureAwait(false)).Contains(pluginId);

    private async Task<HashSet<string>> LoadAlwaysAsync(CancellationToken cancellationToken)
    {
        if (_alwaysAllowed is not null)
        {
            return _alwaysAllowed;
        }
        PermissionsDoc? doc = store is null
            ? null
            : await store.GetAsync<PermissionsDoc>(Collection, DocumentId, cancellationToken).ConfigureAwait(false);
        return _alwaysAllowed = new(doc?.AlwaysAllow ?? [], StringComparer.Ordinal);
    }

    private async Task PersistAlwaysAsync(string pluginId, CancellationToken cancellationToken)
    {
        HashSet<string> always = await LoadAlwaysAsync(cancellationToken).ConfigureAwait(false);
        always.Add(pluginId);
        if (store is not null)
        {
            await store.UpsertAsync(Collection, DocumentId, new PermissionsDoc([.. always]), cancellationToken).ConfigureAwait(false);
        }
    }
}
