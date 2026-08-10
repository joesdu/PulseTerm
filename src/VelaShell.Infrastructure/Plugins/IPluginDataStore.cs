using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Storage;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 插件数据的宿主侧后端(默认 = SonnetDB 单集合,复合主键按插件 id 命名空间化):
/// 插件永不直连数据库 —— 只能经绑定了自身 id 的能力实例读写,读不到其它插件的数据;
/// 隔离进程经 RPC 路由到同一实现。卸载清理经 <see cref="PurgeAsync" /> 整体删除。
/// </summary>
public interface IPluginDataStore
{
    /// <summary>创建绑定到指定插件的 KV 存储能力。</summary>
    IPluginStorage CreateStorage(string pluginId);

    /// <summary>创建绑定到指定插件的机密存储能力(值加密落库;无保护器时报不可用,绝不明文兜底)。</summary>
    ISecretsApi CreateSecrets(string pluginId);

    /// <summary>列出当前存有数据的全部插件 id(卸载清理的扫描依据)。</summary>
    Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>整体删除某插件的全部数据(KV + 机密)。</summary>
    Task PurgeAsync(string pluginId, CancellationToken cancellationToken = default);
}
