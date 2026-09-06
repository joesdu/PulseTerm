using VelaShell.Core.Models;

namespace VelaShell.Core.Data;

/// <summary>
/// 应用设置与运行时状态的持久化服务:读写用户设置(<see cref="AppSettings" />)
/// 与应用状态(<see cref="AppState" />),并在保存后广播以支持热更新。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 设置持久化后触发,使得在线消费者(已打开的终端标签、
    /// 主题等)无需重启即可重新应用这些设置 (#3/#21)。
    /// </summary>
    event Action<AppSettings>? SettingsSaved;

    /// <summary>读取当前持久化的应用设置;不存在时返回默认值。</summary>
    /// <remarks>
    /// 每次调用都返回一个**独立实例**,调用方可以安全修改后再 <see cref="SaveSettingsAsync" />。
    /// 代价是每次都要反序列化整份 <see cref="AppSettings" />;只读的调用方请改用
    /// <c>GetSnapshotAsync()</c>(<see cref="SettingsServiceExtensions" />)。
    /// </remarks>
    Task<AppSettings> GetSettingsAsync();

    /// <summary>
    /// 与最近一次读取/保存一致的**只读共享实例**;尚未加载过(或测试替身)时为 null。
    /// </summary>
    /// <remarks>
    /// 调用方**不得修改**返回的对象 —— 它被所有只读调用方共享。要改请走
    /// <see cref="GetSettingsAsync" />。默认实现返回 null,使不关心快照的实现
    /// (以及测试替身)自动回落到 <see cref="GetSettingsAsync" />。
    /// </remarks>
    AppSettings? CurrentSnapshot => null;

    /// <summary>持久化应用设置,并触发 <see cref="SettingsSaved" /> 以通知在线消费者。</summary>
    Task SaveSettingsAsync(AppSettings settings);

    /// <summary>读取当前持久化的应用运行时状态(如窗口/会话布局)。</summary>
    Task<AppState> GetStateAsync();

    /// <summary>持久化应用运行时状态。</summary>
    Task SaveStateAsync(AppState state);
}
