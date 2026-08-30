using VelaShell.Core.Models;

namespace VelaShell.Core.Notifications;

/// <summary>
/// 订阅的资讯源:从一个 URL 取回公告/资讯/安全新闻,交给消息中心。
/// <para>
/// 源不可达、返回垃圾、字段缺失都只当作"这次没有新消息",绝不抛给调用方 ——
/// 一个终端客户端不该因为公告服务器抽风而给用户看错误。
/// </para>
/// </summary>
public interface IAnnouncementFeed
{
    /// <summary>
    /// 拉取并按本机情况过滤后的条目。地址为空、拉取失败或内容无法解析时返回空列表。
    /// </summary>
    Task<IReadOnlyList<NotificationItem>> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 资讯源的投放上下文:决定一条消息该不该给这台机器看。
/// </summary>
/// <param name="AppVersion">当前应用版本,用于 <c>minVersion</c>/<c>maxVersion</c> 定向。</param>
/// <param name="Rid">运行平台标识(如 <c>win-x64</c>),用于 <c>platforms</c> 定向。</param>
/// <param name="UiCulture">界面语言(如 <c>zh-Hans</c>),用于 <c>locales</c> 定向。</param>
public sealed record FeedAudience(string? AppVersion, string? Rid, string? UiCulture);
