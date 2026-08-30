using VelaShell.Core.Models;
using VelaShell.Core.Notifications;

namespace VelaShell.Core.Tests.Notifications;

/// <summary>
/// 资讯源的 JSON 契约。**这是后台系统要照着发布的格式**,所以这些用例既是回归测试,
/// 也是那份格式的可执行说明:定向投放怎么算、什么样的条目会被丢掉、外链放行到什么程度。
/// </summary>
[TestClass]
[TestCategory("Notifications")]
public class AnnouncementFeedDocumentTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FeedAudience Anyone = new("1.3.0", "win-x64", "zh-Hans");

    /// <summary>一条完整的条目应被原样解析出来。</summary>
    [TestMethod]
    public void Parse_ReadsFullItem()
    {
        const string json = """
        {
          "schema": 1,
          "items": [{
            "id": "2026-08-30-release",
            "kind": "update",
            "severity": "warning",
            "title": "VelaShell 1.4 已发布",
            "body": "隧道流量统计、断线自动恢复。",
            "publishedAt": "2026-08-30T02:00:00Z",
            "linkLabel": "查看详情",
            "url": "https://velashell.dev/releases/1.4"
          }]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, Anyone, Now);

        Assert.HasCount(1, items);
        NotificationItem item = items[0];
        Assert.AreEqual("2026-08-30-release", item.Id);
        Assert.AreEqual(NotificationKind.Update, item.Kind);
        Assert.AreEqual(NotificationSeverity.Warning, item.Severity);
        Assert.AreEqual("VelaShell 1.4 已发布", item.Title);
        Assert.AreEqual("隧道流量统计、断线自动恢复。", item.Body);
        Assert.AreEqual("查看详情", item.Link?.Label);
        Assert.AreEqual("https://velashell.dev/releases/1.4", item.Link?.Url);
    }

    /// <summary>缺 id / 标题 / 发布时间的条目直接丢掉,但**不影响同一批里的好条目**。</summary>
    [TestMethod]
    public void Parse_SkipsBadItems_ButKeepsGoodOnes()
    {
        const string json = """
        {
          "items": [
            { "title": "没有 id", "publishedAt": "2026-08-30T02:00:00Z" },
            { "id": "no-title", "publishedAt": "2026-08-30T02:00:00Z" },
            { "id": "no-date", "title": "没有发布时间" },
            "这一条压根不是对象",
            { "id": "good", "title": "好的", "publishedAt": "2026-08-30T02:00:00Z" }
          ]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, Anyone, Now);

        Assert.HasCount(1, items);
        Assert.AreEqual("good", items[0].Id);
    }

    /// <summary>整体结构不对(不是 JSON / 没有 items 数组)时返回空,不抛。</summary>
    [TestMethod]
    public void Parse_ReturnsEmpty_ForMalformedDocument()
    {
        Assert.IsEmpty(AnnouncementFeedDocument.Parse("这不是 JSON", Anyone, Now));
        Assert.IsEmpty(AnnouncementFeedDocument.Parse("{}", Anyone, Now));
        Assert.IsEmpty(AnnouncementFeedDocument.Parse("""{"items":"不是数组"}""", Anyone, Now));
        Assert.IsEmpty(AnnouncementFeedDocument.Parse("[]", Anyone, Now));
    }

    /// <summary>过了 expiresAt 的条目不再展示 —— 活动结束了就不该还挂在消息列表里。</summary>
    [TestMethod]
    public void Parse_DropsExpiredItems()
    {
        const string json = """
        {
          "items": [
            { "id": "expired", "title": "已过期", "publishedAt": "2026-08-01T00:00:00Z", "expiresAt": "2026-08-15T00:00:00Z" },
            { "id": "live",    "title": "仍有效", "publishedAt": "2026-08-01T00:00:00Z", "expiresAt": "2026-09-15T00:00:00Z" }
          ]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, Anyone, Now);

        Assert.HasCount(1, items);
        Assert.AreEqual("live", items[0].Id);
    }

    /// <summary>按界面语言定向:不在 locales 里的收不到。</summary>
    [TestMethod]
    public void Parse_FiltersByLocale()
    {
        const string json = """
        {
          "items": [
            { "id": "zh", "title": "中文公告", "publishedAt": "2026-08-30T02:00:00Z", "locales": ["zh-Hans", "zh-Hant"] },
            { "id": "ja", "title": "日本語のお知らせ", "publishedAt": "2026-08-30T02:00:00Z", "locales": ["ja"] }
          ]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, Anyone, Now);

        Assert.HasCount(1, items);
        Assert.AreEqual("zh", items[0].Id);
    }

    /// <summary>按平台定向:只投给指定 RID。</summary>
    [TestMethod]
    public void Parse_FiltersByPlatform()
    {
        const string json = """
        {
          "items": [
            { "id": "win", "title": "Windows", "publishedAt": "2026-08-30T02:00:00Z", "platforms": ["win-x64"] },
            { "id": "mac", "title": "macOS",   "publishedAt": "2026-08-30T02:00:00Z", "platforms": ["osx-arm64"] }
          ]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, Anyone, Now);

        Assert.HasCount(1, items);
        Assert.AreEqual("win", items[0].Id);
    }

    /// <summary>按版本区间定向(含端点),预发布后缀不参与比较。</summary>
    [TestMethod]
    public void Parse_FiltersByVersionRange()
    {
        const string json = """
        {
          "items": [
            { "id": "too-old", "title": "给更新的版本", "publishedAt": "2026-08-30T02:00:00Z", "minVersion": "1.5.0" },
            { "id": "too-new", "title": "给更旧的版本", "publishedAt": "2026-08-30T02:00:00Z", "maxVersion": "1.2.0" },
            { "id": "in-range", "title": "正好", "publishedAt": "2026-08-30T02:00:00Z", "minVersion": "1.2.0", "maxVersion": "1.3.0" }
          ]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, new("1.3.0-beta.2", "win-x64", "zh-Hans"), Now);

        Assert.HasCount(1, items);
        Assert.AreEqual("in-range", items[0].Id);
    }

    /// <summary>
    /// 本机版本号读不出来时不做版本定向过滤 —— 宁可多看一条,也好过因为版本号异常
    /// 就把"你这个版本有问题,快升级"这种正是冲着你来的消息漏掉。
    /// </summary>
    [TestMethod]
    public void Parse_KeepsTargetedItems_WhenLocalVersionUnknown()
    {
        const string json = """
        { "items": [{ "id": "targeted", "title": "定向", "publishedAt": "2026-08-30T02:00:00Z", "minVersion": "1.5.0" }] }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, new(null, "win-x64", "zh-Hans"), Now);

        Assert.HasCount(1, items);
    }

    /// <summary>空的定向数组等于没设条件,不该把所有人筛掉。</summary>
    [TestMethod]
    public void Parse_TreatsEmptyTargetingArrayAsUnrestricted()
    {
        const string json = """
        { "items": [{ "id": "all", "title": "给所有人", "publishedAt": "2026-08-30T02:00:00Z", "locales": [], "platforms": [] }] }
        """;

        Assert.HasCount(1, AnnouncementFeedDocument.Parse(json, Anyone, Now));
    }

    /// <summary>
    /// 外链只放行 https:内容来自远端源,放行 http 等于允许投递方把用户导去
    /// 一条可被中间人改写的链路。非 https 的链接被抹掉,条目本身仍然保留。
    /// </summary>
    [TestMethod]
    public void Parse_RejectsNonHttpsLinks()
    {
        const string json = """
        {
          "items": [
            { "id": "http",   "title": "明文", "publishedAt": "2026-08-30T02:00:00Z", "url": "http://example.com" },
            { "id": "file",   "title": "本地", "publishedAt": "2026-08-30T02:00:00Z", "url": "file:///etc/passwd" },
            { "id": "https",  "title": "加密", "publishedAt": "2026-08-30T02:00:00Z", "url": "https://example.com" }
          ]
        }
        """;

        IReadOnlyList<NotificationItem> items = AnnouncementFeedDocument.Parse(json, Anyone, Now);

        Assert.HasCount(3, items);
        Assert.IsNull(items.Single(item => item.Id == "http").Link, "http 链接应被丢弃。");
        Assert.IsNull(items.Single(item => item.Id == "file").Link, "file 链接应被丢弃。");
        Assert.AreEqual("https://example.com", items.Single(item => item.Id == "https").Link?.Url);
    }

    /// <summary>站内命令 id 与外链可以并存,由界面决定优先级(站内优先)。</summary>
    [TestMethod]
    public void Parse_KeepsCommandIdAlongsideUrl()
    {
        const string json = """
        { "items": [{ "id": "both", "title": "两个都给", "publishedAt": "2026-08-30T02:00:00Z",
                      "commandId": "app.settings.about", "url": "https://example.com" }] }
        """;

        NotificationLink? link = AnnouncementFeedDocument.Parse(json, Anyone, Now)[0].Link;

        Assert.AreEqual("app.settings.about", link?.CommandId);
        Assert.AreEqual("https://example.com", link?.Url);
    }

    /// <summary>源端一次推来上万条时只取前 100 条,不让消息列表被一次拉取撑爆。</summary>
    [TestMethod]
    public void Parse_CapsItemCount()
    {
        IEnumerable<string> entries = Enumerable.Range(0, 250)
            .Select(i => $$"""{ "id": "n{{i}}", "title": "第 {{i}} 条", "publishedAt": "2026-08-30T02:00:00Z" }""");
        string json = $$"""{ "items": [{{string.Join(",", entries)}}] }""";

        Assert.HasCount(AnnouncementFeedDocument.MaxItems, AnnouncementFeedDocument.Parse(json, Anyone, Now));
    }

    /// <summary>未知的 kind / severity 落到最保守的默认值,而不是让整条失效。</summary>
    [TestMethod]
    public void Parse_FallsBackForUnknownEnums()
    {
        const string json = """
        { "items": [{ "id": "x", "title": "未知枚举", "publishedAt": "2026-08-30T02:00:00Z",
                      "kind": "宇宙射线", "severity": "非常严重" }] }
        """;

        NotificationItem item = AnnouncementFeedDocument.Parse(json, Anyone, Now)[0];

        Assert.AreEqual(NotificationKind.News, item.Kind);
        Assert.AreEqual(NotificationSeverity.Info, item.Severity);
    }
}
