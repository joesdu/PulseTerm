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

    /// <summary>
    /// 请求用户裁决一次「按已保存配置开会话」。
    /// </summary>
    /// <param name="pluginId">发起请求的插件 id。</param>
    /// <param name="target">要连的那台机器(会话树上的名字 + user@host:port)。</param>
    /// <param name="reason">插件给的理由,<b>原样显示</b> —— 用户是照着它决定点不点同意的。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<PluginPermissionDecision> RequestSessionOpenAsync(string pluginId, string target, string reason,
        CancellationToken cancellationToken);
}

/// <summary>
/// 敏感能力的授权闸(蓝图 06 的最小可用形态):
/// 始终允许 → SonnetDB 持久化(按插件);本次运行允许 → 内存;仅本次 → 放行一次;
/// 拒绝 → 本次拒绝。同插件的并发请求串行化,不弹对话框风暴。无提示器的宿主一律拒绝。
/// </summary>
/// <remarks>
/// 两类能力(终端回写 / 开会话)各记各的账:落库分两个文档,内存也分两套。
/// 合成一本账就意味着"允许它替我敲一行命令"顺带把"允许它自己连生产机"也批了 ——
/// 这两件事的分量差得远,用户在确认框上点的也不是同一个"是"。
/// </remarks>
public sealed class PluginPermissionGate(IAppDataStore? store, IPluginPermissionPrompt? prompt)
{
    private const string Collection = "app_config";

    private sealed record PermissionsDoc(List<string> AlwaysAllow);

    /// <summary>一类能力的授权账本(持久 + 本次运行)。</summary>
    private sealed class Grants(string documentId)
    {
        public string DocumentId { get; } = documentId;
        public HashSet<string> SessionAllowed { get; } = new(StringComparer.Ordinal);
        public HashSet<string>? AlwaysAllowed { get; set; }
    }

    /// <summary>终端回写。文档 id 是历史名字,不改 —— 改了等于把用户已有的授权丢掉。</summary>
    private readonly Grants _terminalWrite = new("plugin_terminal_write_permissions");

    /// <summary>按已保存配置开会话。</summary>
    private readonly Grants _sessionOpen = new("plugin_session_open_permissions");

    private readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>是否放行本次终端回写;必要时弹授权对话框。</summary>
    public Task<bool> CheckTerminalWriteAsync(string pluginId, string sessionLabel, string inputPreview,
        CancellationToken cancellationToken)
        => CheckAsync(_terminalWrite, pluginId,
            ct => prompt!.RequestTerminalWriteAsync(pluginId, sessionLabel, inputPreview, ct), cancellationToken);

    /// <summary>
    /// 是否放行本次「按已保存配置开会话」;必要时弹授权对话框。
    /// </summary>
    /// <remarks>
    /// 授权按插件计,而不是按 (插件, 机器) 计:后者更精确,但也意味着一个 IM 桥接插件
    /// 要为运维手上每一台机器各弹一次框 —— 用户会在第三台上开始盲点。分寸取在
    /// "这个插件可以替我连机器"这一层,想收回就去插件管理页撤销。
    /// </remarks>
    public Task<bool> CheckSessionOpenAsync(string pluginId, string target, string reason,
        CancellationToken cancellationToken)
        => CheckAsync(_sessionOpen, pluginId,
            ct => prompt!.RequestSessionOpenAsync(pluginId, target, reason, ct), cancellationToken);

    /// <summary>撤销某插件的**全部**授权(持久 + 会话内),插件管理页使用。</summary>
    public async Task RevokeAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        foreach (Grants grants in Ledgers)
        {
            lock (grants.SessionAllowed)
            {
                grants.SessionAllowed.Remove(pluginId);
            }
            HashSet<string> always = await LoadAlwaysAsync(grants, cancellationToken).ConfigureAwait(false);
            if (always.Remove(pluginId) && store is not null)
            {
                await store.UpsertAsync(Collection, grants.DocumentId, new PermissionsDoc([.. always]), cancellationToken)
                           .ConfigureAwait(false);
            }
        }
    }

    /// <summary>某插件当前是否持有**任何**授权(持久或会话内、任一类能力),插件管理页展示用。</summary>
    public async Task<bool> HasGrantAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        foreach (Grants grants in Ledgers)
        {
            if (await IsAlwaysAllowedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            lock (grants.SessionAllowed)
            {
                if (grants.SessionAllowed.Contains(pluginId))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private IEnumerable<Grants> Ledgers
    {
        get
        {
            yield return _terminalWrite;
            yield return _sessionOpen;
        }
    }

    private async Task<bool> CheckAsync(Grants grants, string pluginId,
        Func<CancellationToken, Task<PluginPermissionDecision>> ask, CancellationToken cancellationToken)
    {
        if (await IsGrantedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (prompt is null)
        {
            return false; // 无 UI 可问 = 不放行(绝不静默授权)
        }
        // 串行化提问:并发请求只弹一个框;等待期间别人拿到的授权对后续请求同样生效。
        await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsGrantedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            PluginPermissionDecision decision = await ask(cancellationToken).ConfigureAwait(false);
            switch (decision)
            {
                case PluginPermissionDecision.AllowOnce:
                    return true;
                case PluginPermissionDecision.AllowSession:
                    lock (grants.SessionAllowed)
                    {
                        grants.SessionAllowed.Add(pluginId);
                    }
                    return true;
                case PluginPermissionDecision.AllowAlways:
                    await PersistAlwaysAsync(grants, pluginId, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> IsGrantedAsync(Grants grants, string pluginId, CancellationToken cancellationToken)
    {
        if (await IsAlwaysAllowedAsync(grants, pluginId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        lock (grants.SessionAllowed)
        {
            return grants.SessionAllowed.Contains(pluginId);
        }
    }

    private async Task<bool> IsAlwaysAllowedAsync(Grants grants, string pluginId, CancellationToken cancellationToken) =>
        (await LoadAlwaysAsync(grants, cancellationToken).ConfigureAwait(false)).Contains(pluginId);

    private async Task<HashSet<string>> LoadAlwaysAsync(Grants grants, CancellationToken cancellationToken)
    {
        if (grants.AlwaysAllowed is not null)
        {
            return grants.AlwaysAllowed;
        }
        PermissionsDoc? doc = store is null
            ? null
            : await store.GetAsync<PermissionsDoc>(Collection, grants.DocumentId, cancellationToken).ConfigureAwait(false);
        return grants.AlwaysAllowed = new(doc?.AlwaysAllow ?? [], StringComparer.Ordinal);
    }

    private async Task PersistAlwaysAsync(Grants grants, string pluginId, CancellationToken cancellationToken)
    {
        HashSet<string> always = await LoadAlwaysAsync(grants, cancellationToken).ConfigureAwait(false);
        always.Add(pluginId);
        if (store is not null)
        {
            await store.UpsertAsync(Collection, grants.DocumentId, new PermissionsDoc([.. always]), cancellationToken)
                       .ConfigureAwait(false);
        }
    }
}
