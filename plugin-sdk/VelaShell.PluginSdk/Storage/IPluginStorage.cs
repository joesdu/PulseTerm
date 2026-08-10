namespace VelaShell.PluginSdk.Storage;

/// <summary>
/// 插件私有的持久化键值存储:数据落在插件数据目录内(JSON 序列化),插件间互不可见。
/// 适合配置与小状态(单值建议 ≤ 256KB);大块数据请直接写
/// <see cref="IPluginContext.DataDirectory" /> 下的文件。实现线程安全。
/// </summary>
public interface IPluginStorage
{
    /// <summary>读取键值并反序列化为 <typeparamref name="T" />;键不存在时返回 <see langword="default" />。</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>写入键值(JSON 序列化,立即持久化)。</summary>
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    /// <summary>删除键;返回该键此前是否存在。</summary>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>列出当前全部键(快照)。</summary>
    Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default);
}
