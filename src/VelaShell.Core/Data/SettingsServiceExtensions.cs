using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// <see cref="ISettingsService" /> 的只读读取通道。
/// </summary>
public static class SettingsServiceExtensions
{
    /// <summary>
    /// 取只读设置快照:有共享快照就直接返回,否则加载一次(加载后即有快照)。
    /// 只读调用方(传输调优、代理解析、连接工厂、状态栏刷新等)都应该走这里,
    /// 而不是 <see cref="ISettingsService.GetSettingsAsync" /> —— 后者每次都要把
    /// 整份 <see cref="AppSettings" /> 反序列化一遍,上传一万个小文件就是一万次。
    /// </summary>
    /// <remarks>
    /// 这里**故意**写成扩展方法而不是接口的默认实现。NSubstitute(底下的 Castle
    /// DynamicProxy)会为默认接口成员一并生成实现并路由给拦截器,于是替身上的
    /// <c>GetSnapshotAsync()</c> 会返回 <c>default</c>(= null 设置)而不是回落到
    /// 被 stub 的 <c>GetSettingsAsync()</c>,把测试炸成一片 NRE。扩展方法是静态
    /// 解析的,代理拦不到,替身因此照旧走 <see cref="ISettingsService.CurrentSnapshot" />
    /// (默认返回 null)→ <c>GetSettingsAsync()</c>。
    /// <c>SettingsSnapshotSubstituteTests</c> 钉住这个行为。
    /// </remarks>
    /// <param name="settings">设置服务。</param>
    /// <returns>只读共享快照;调用方不得修改。</returns>
    public static async ValueTask<AppSettings> GetSnapshotAsync(this ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CurrentSnapshot is { } snapshot)
        {
            return snapshot;
        }
        // 加载一次后再取一次快照:实现会在加载过程中把它发布出来,这样**每一次**调用
        // 拿到的都是同一个共享实例(否则首次调用会拿到 GetSettingsAsync 的那份独立实例,
        // 与后续调用引用不一致)。实现没有发布快照(如测试替身)时退回刚加载的那份。
        AppSettings loaded = await settings.GetSettingsAsync().ConfigureAwait(false);
        return settings.CurrentSnapshot ?? loaded;
    }

    /// <summary>
    /// 同步取只读快照,给那些确实回不了异步的调用点(DI 工厂委托、
    /// <c>IWebProxy</c> 的同步接口)。有快照时**完全不阻塞**;没有快照时退回
    /// 阻塞加载一次,之后就一直走快照。
    /// </summary>
    /// <param name="settings">设置服务;为 null 时返回默认设置。</param>
    /// <returns>只读共享快照;调用方不得修改。</returns>
    public static AppSettings GetSnapshotBlocking(this ISettingsService? settings)
    {
        if (settings is null)
        {
            return new();
        }
        return settings.CurrentSnapshot ?? settings.GetSettingsAsync().GetAwaiter().GetResult();
    }
}
