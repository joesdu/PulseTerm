using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;

namespace VelaShell.Infrastructure.Notifications;

/// <summary>
/// 从一个 https 地址拉取资讯源(见 <see cref="AnnouncementFeedDocument" /> 的格式约定)。
/// <para>
/// <b>不配地址就一个字节都不发。</b> 这是个终端工具,不该在用户没要求的情况下
/// 定期向外汇报"这台机器还活着";地址由用户/部署方在设置里填,留空即彻底关闭。
/// </para>
/// <para>
/// 出站走进程级 <c>HttpClient.DefaultProxy</c>(由 <c>VelaWebProxy</c> 接管),
/// 因此自动遵守「设置 → 网络代理」,与更新检查、Gist 同步同一条链路。
/// </para>
/// </summary>
/// <param name="feedUrlProvider">取当前配置的资讯源地址;返回空表示不启用。</param>
/// <param name="audienceProvider">取本机的投放上下文(版本/平台/语言)。</param>
/// <param name="httpClient">注入以便测试;省略时使用内置的短超时客户端。</param>
public sealed class HttpAnnouncementFeed(
    Func<Task<string?>> feedUrlProvider,
    Func<FeedAudience> audienceProvider,
    HttpClient? httpClient = null) : IAnnouncementFeed
{
    /// <summary>响应体大小上限:公告是文本,超过这个量级只可能是配错了地址或源被做了手脚。</summary>
    private const int MaxResponseBytes = 512 * 1024;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _http = httpClient ?? Shared;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationItem>> FetchAsync(CancellationToken cancellationToken = default)
    {
        string? url;
        try
        {
            url = await feedUrlProvider().ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? feed) ||
            feed.Scheme != Uri.UriSchemeHttps)
        {
            // 只走 https:明文取回的公告可被中间人替换,而公告里带的是用户会去点的链接。
            return [];
        }
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feed);
            request.Headers.Accept.Add(new("application/json"));
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return [];
            }
            json = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            // 源不可达是常态(离线、内网、地址写错),当作"这次没有新消息"。
            return [];
        }
        try
        {
            return AnnouncementFeedDocument.Parse(json, audienceProvider(), DateTime.UtcNow);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>读取响应体,超过上限即放弃 —— 不声明长度的源不能拿来当无限流读。</summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[MaxResponseBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total > MaxResponseBytes ? string.Empty : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// 按本机情况组装投放上下文:版本取入口程序集,平台按进程架构解析(与更新产物的
    /// RID 命名一致),语言取当前界面语言。
    /// </summary>
    public static FeedAudience DescribeAudience() =>
        new(EntryVersion(), CurrentRid(), CultureInfo.CurrentUICulture.Name);

    private static string? EntryVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);

    /// <summary>
    /// 当前进程的 RID。按**进程**架构而非 OS 架构解析:x64 版本跑在 arm64 Windows
    /// 的仿真层上时应继续算 x64,与更新产物的选择口径一致。
    /// </summary>
    private static string? CurrentRid()
    {
        string? os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : null;
        string? arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null
        };
        return os is not null && arch is not null ? $"{os}-{arch}" : null;
    }
}
