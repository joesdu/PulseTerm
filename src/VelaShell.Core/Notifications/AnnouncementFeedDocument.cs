using System.Globalization;
using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Notifications;

/// <summary>
/// 资讯源的 JSON 契约与解析。这是**后台系统要照着发布的格式**,所以字段一旦发出去
/// 就不能随便改名;要加能力就加可选字段,老客户端读不到会自动忽略。
/// <para>
/// 格式(<c>schema</c> 目前恒为 1):
/// </para>
/// <code>
/// {
///   "schema": 1,
///   "items": [
///     {
///       "id":          "2026-08-30-release-1.4",   // 必填,去重与已读状态的键
///       "kind":        "news",                      // news | update | security | promotion
///       "severity":    "info",                      // info | warning | critical
///       "title":       "VelaShell 1.4 已发布",       // 必填
///       "body":        "隧道流量统计、断线自动恢复…",
///       "publishedAt": "2026-08-30T02:00:00Z",      // 必填,列表按它倒序
///       "expiresAt":   "2026-10-01T00:00:00Z",      // 到点自动消失
///       "linkLabel":   "查看详情",
///       "url":         "https://velashell.dev/…",   // 只接受 https
///       "commandId":   "settings.open",             // 站内跳转,优先于 url
///       "locales":     ["zh-Hans", "zh-Hant"],      // 定向:界面语言
///       "platforms":   ["win-x64", "osx-arm64"],    // 定向:运行平台
///       "minVersion":  "1.2.0",                     // 定向:版本区间(含端点)
///       "maxVersion":  "1.3.9"
///     }
///   ]
/// }
/// </code>
/// <para>
/// 用 <see cref="JsonDocument" /> 手工取值而非反射序列化:字段少,且不给单文件裁剪/AOT
/// 留隐患(与 <c>UpdateManifest</c> 同一取舍)。
/// </para>
/// </summary>
public static class AnnouncementFeedDocument
{
    /// <summary>单次拉取最多接受的条目数,挡住源端(被入侵或写错)一次推来上万条。</summary>
    public const int MaxItems = 100;

    /// <summary>标题与正文的长度上限,超出截断 —— 列表容不下一篇文章。</summary>
    private const int MaxTitleLength = 200;

    private const int MaxBodyLength = 1000;

    /// <summary>
    /// 解析并按 <paramref name="audience" /> 过滤。整体结构不合法时返回空列表;
    /// **单条不合法只跳过那一条** —— 一条坏数据不该让整个源哑掉。
    /// </summary>
    public static IReadOnlyList<NotificationItem> Parse(string json, FeedAudience audience, DateTime utcNow)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out JsonElement items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            List<NotificationItem> result = [];
            foreach (JsonElement element in items.EnumerateArray())
            {
                if (result.Count >= MaxItems)
                {
                    break;
                }
                NotificationItem? item = ParseItem(element, audience, utcNow);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
            return result;
        }
    }

    private static NotificationItem? ParseItem(JsonElement element, FeedAudience audience, DateTime utcNow)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? id = ReadString(element, "id");
        string? title = ReadString(element, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }
        if (ReadDate(element, "publishedAt") is not { } publishedAt)
        {
            return null;
        }
        DateTime? expiresAt = ReadDate(element, "expiresAt");
        if (expiresAt is { } expiry && expiry <= utcNow)
        {
            return null;
        }
        if (!MatchesAudience(element, audience))
        {
            return null;
        }
        return new()
        {
            Id = id,
            Kind = ParseKind(ReadString(element, "kind")),
            Severity = ParseSeverity(ReadString(element, "severity")),
            Title = Truncate(title, MaxTitleLength),
            Body = ReadString(element, "body") is { Length: > 0 } body ? Truncate(body, MaxBodyLength) : null,
            PublishedAt = publishedAt,
            ExpiresAt = expiresAt,
            Link = ParseLink(element)
        };
    }

    /// <summary>
    /// 解析去处。外链**只放行 https**:内容来自远端源,允许 http 等于让投递方
    /// 把用户导去一条可被中间人改写的链路。命令 id 与网址都没有时返回 null。
    /// </summary>
    private static NotificationLink? ParseLink(JsonElement element)
    {
        string? commandId = ReadString(element, "commandId");
        string? url = ReadString(element, "url");
        if (url is { Length: > 0 } &&
            !(Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Scheme == Uri.UriSchemeHttps))
        {
            url = null;
        }
        if (string.IsNullOrEmpty(commandId) && string.IsNullOrEmpty(url))
        {
            return null;
        }
        return new()
        {
            Label = Truncate(ReadString(element, "linkLabel") ?? string.Empty, 60),
            CommandId = commandId,
            Url = url
        };
    }

    /// <summary>定向投放:语言 / 平台 / 版本区间。字段缺省即"不限"。</summary>
    private static bool MatchesAudience(JsonElement element, FeedAudience audience) =>
        MatchesList(element, "locales", audience.UiCulture) &&
        MatchesList(element, "platforms", audience.Rid) &&
        MatchesVersionRange(element, audience.AppVersion);

    private static bool MatchesList(JsonElement element, string property, string? actual)
    {
        if (!element.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return true;
        }
        bool empty = true;
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            empty = false;
            if (string.Equals(entry.GetString(), actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // 给了个空数组等于没给条件,不该把所有人都筛掉。
        return empty;
    }

    private static bool MatchesVersionRange(JsonElement element, string? appVersion)
    {
        string? min = ReadString(element, "minVersion");
        string? max = ReadString(element, "maxVersion");
        if (min is null && max is null)
        {
            return true;
        }
        if (!TryParseVersion(appVersion, out Version? current))
        {
            // 版本读不出来时不做定向过滤:宁可多看一条,也好过因为本机版本号异常而
            // 把所有定向消息(可能正是"你这个版本有问题,快升级")全部漏掉。
            return true;
        }
        if (TryParseVersion(min, out Version? minimum) && current < minimum)
        {
            return false;
        }
        return !TryParseVersion(max, out Version? maximum) || current <= maximum;
    }

    /// <summary>解析版本号,忽略预发布后缀(<c>1.4.0-beta.2</c> → <c>1.4.0</c>)。</summary>
    private static bool TryParseVersion(string? text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string trimmed = text.TrimStart('v', 'V');
        int dash = trimmed.IndexOfAny(['-', '+']);
        if (dash >= 0)
        {
            trimmed = trimmed[..dash];
        }
        return Version.TryParse(trimmed, out version);
    }

    private static NotificationKind ParseKind(string? kind) =>
        kind?.ToLowerInvariant() switch
        {
            "update" => NotificationKind.Update,
            "security" => NotificationKind.Security,
            "promotion" or "promo" => NotificationKind.Promotion,
            _ => NotificationKind.News
        };

    private static NotificationSeverity ParseSeverity(string? severity) =>
        severity?.ToLowerInvariant() switch
        {
            "warning" or "warn" => NotificationSeverity.Warning,
            "critical" or "error" => NotificationSeverity.Critical,
            _ => NotificationSeverity.Info
        };

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static DateTime? ReadDate(JsonElement element, string property) =>
        ReadString(element, property) is { Length: > 0 } text &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed
            : null;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
