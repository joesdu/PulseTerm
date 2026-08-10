namespace VelaShell.PluginSdk.Secrets;

/// <summary>
/// 机密存储能力:插件私有的加密键值(API token、口令等)。
/// 与 <see cref="Storage.IPluginStorage" /> 的区别:值**加密落盘**(宿主的机密保护器,
/// Windows 上为 DPAPI 包裹的本地密钥),且隔离模式下机密只存在宿主侧、
/// 不落插件进程的数据目录。命名空间按插件隔离,互不可见。
/// 适合少量短字符串;不要塞大 payload。
/// </summary>
public interface ISecretsApi
{
    /// <summary>读取机密;不存在返回 <see langword="null" />。</summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>写入机密(加密后持久化)。</summary>
    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>删除机密;返回此前是否存在。</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
