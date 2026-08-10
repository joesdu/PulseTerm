namespace VelaShell.PluginSdk.Commands;

/// <summary>
/// 插件注册的一条命令。注册后出现在宿主命令面板(Ctrl+P / Ctrl+K)中,标题原样展示
/// (本地化由插件自理,可按 <see cref="IHostInfo.Locale" /> 取词)。
/// </summary>
/// <param name="Id">命令 id,必须以 <c>&lt;pluginId&gt;.</c> 为前缀(宿主强制校验,防插件间冒名)。</param>
/// <param name="Title">面向用户的显示名称。</param>
/// <param name="Category">分组标签(命令面板分区展示)。</param>
/// <param name="ExecuteAsync">命令体。在后台线程调用;异常由宿主捕获并记入插件日志,不会崩溃宿主。</param>
public sealed record PluginCommandDescriptor(
    string Id,
    string Title,
    string Category,
    Func<CancellationToken, Task> ExecuteAsync);

/// <summary>
/// 命令能力:向宿主命令面板注册命令,或按 id 执行既有命令。
/// 插件停用时其全部注册自动移除,无需手动清理;返回的句柄用于提前注销。
/// </summary>
public interface ICommandsApi
{
    /// <summary>注册(或按 id 替换本插件的)一条命令;释放返回值即注销。</summary>
    IDisposable Register(PluginCommandDescriptor command);

    /// <summary>按 id 执行一条宿主或插件命令;命令不存在或当前不可用时返回 <see langword="false" />。</summary>
    bool TryExecute(string commandId);
}
