using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>一次「请求宿主打开已保存会话」的结局。</summary>
public enum PluginSessionOpenOutcome
{
    /// <summary>连上了。</summary>
    Opened,

    /// <summary>用户拒绝(确认框上点了「拒绝」,或在凭据弹窗上取消)。</summary>
    Denied,

    /// <summary>放行了,但没连上(网络不通、认证失败、指纹不符……)。</summary>
    Failed
}

/// <summary>
/// 打开结果。<see cref="Denied" /> 与 <see cref="Failed" /> 刻意分开:
/// 前者重试没有意义(用户已经说了不),后者换个时间再来可能就好了 ——
/// 插件对这两种情况的处置完全不同,合成一个"失败"就只能靠读消息文本去猜。
/// </summary>
/// <param name="Outcome">结局。</param>
/// <param name="SessionId">连上的会话 id;仅 <see cref="PluginSessionOpenOutcome.Opened" /> 时有意义。</param>
/// <param name="Error">失败/拒绝的说明(给日志与插件看,不必给不受信的一方转发)。</param>
public sealed record PluginSessionOpenResult(PluginSessionOpenOutcome Outcome, Guid SessionId = default, string? Error = null)
{
    /// <summary>连上了。</summary>
    public static PluginSessionOpenResult Opened(Guid sessionId) => new(PluginSessionOpenOutcome.Opened, sessionId);

    /// <summary>用户拒绝。</summary>
    public static PluginSessionOpenResult Denied(string reason) => new(PluginSessionOpenOutcome.Denied, default, reason);

    /// <summary>放行了但没连上。</summary>
    public static PluginSessionOpenResult Failed(string reason) => new(PluginSessionOpenOutcome.Failed, default, reason);
}

/// <summary>
/// 「按已保存配置开一条会话」的宿主 SPI(由 UI 层实现)。
/// </summary>
/// <remarks>
/// <para>
/// 这件事为什么不能留在 Infrastructure 里自己做完:它中间夹着两个**必须有人**的环节 ——
/// 给用户看理由的确认框,以及配置没记密码时的凭据弹窗。两者都在 UI 层,
/// 而 Infrastructure 不引用 Avalonia。所以这里只留一个契约,实现挂在
/// <see cref="PluginManagerOptions.SessionOpener" /> 上;无界面的宿主(headless 测试)不挂,
/// 于是 <c>ISessionsApi.OpenAsync</c> 一律拒绝 —— 没人可问不等于可以自己放行。
/// </para>
/// <para>
/// 凭据一个字节都不经过插件:这个契约里传的是宿主自己查出来的
/// <see cref="SessionProfile" />,插件那边只有一个不透明 id。
/// </para>
/// </remarks>
public interface IPluginSessionOpener
{
    /// <summary>
    /// 征得用户同意后连上 <paramref name="profile" />,并在宿主界面上开出对应的标签页。
    /// </summary>
    /// <param name="pluginId">发起请求的插件 id(确认框上显示的那个)。</param>
    /// <param name="profile">要连的已保存配置。</param>
    /// <param name="reason">插件给的理由,<b>原样显示给用户</b>。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>结局;实现不应为"用户拒绝"或"连不上"抛异常 —— 那是两种正常结果。</returns>
    Task<PluginSessionOpenResult> OpenAsync(string pluginId, SessionProfile profile, string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// 关掉一条会话连同它的标签页。会话已经不在了不算错(幂等)。
    /// </summary>
    Task CloseAsync(Guid sessionId, CancellationToken cancellationToken);
}
