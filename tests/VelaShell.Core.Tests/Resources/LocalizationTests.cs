using System.Globalization;
using VelaShell.Core.Localization;
using VelaShell.Core.Resources;

namespace VelaShell.Core.Tests.Resources;

[TestClass]
[TestCategory("i18n")]
public class LocalizationTests : IDisposable
{
    private readonly CultureInfo _originalCulture;

    public LocalizationTests()
    {
        _originalCulture = CultureInfo.CurrentUICulture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _originalCulture;
        CultureInfo.CurrentCulture = _originalCulture;
        GC.SuppressFinalize(this);
    }

    [TestMethod]
    public void DefaultCulture_ReturnsEnglishStrings()
    {
        CultureInfo.CurrentUICulture = new("en");
        Assert.AreEqual("Quick Connect", Strings.QuickConnect);
        Assert.AreEqual("Recent Connections", Strings.RecentConnections);
        Assert.AreEqual("Settings", Strings.Settings);
        Assert.AreEqual("New Tab", Strings.NewTab);
        Assert.AreEqual("Connect", Strings.Connect);
    }

    [TestMethod]
    public void ChineseCulture_ReturnsChineseStrings()
    {
        CultureInfo.CurrentUICulture = new("zh-CN");
        Assert.AreEqual("快速连接", Strings.QuickConnect);
        Assert.AreEqual("最近连接", Strings.RecentConnections);
        Assert.AreEqual("设置", Strings.Settings);
        Assert.AreEqual("新标签页", Strings.NewTab);
        Assert.AreEqual("连接", Strings.Connect);
    }

    [TestMethod]
    public void LocalizationService_GetString_ReturnsEnglishByDefault()
    {
        CultureInfo.CurrentUICulture = new("en");
        var service = new LocalizationService();
        Assert.AreEqual("Quick Connect", service.GetString("QuickConnect"));
        Assert.AreEqual("Recent Connections", service.GetString("RecentConnections"));
        Assert.AreEqual("Disconnect", service.GetString("Disconnect"));
    }

    [TestMethod]
    public void LocalizationService_GetString_ReturnsChineseForZhCN()
    {
        CultureInfo.CurrentUICulture = new("zh-CN");
        var service = new LocalizationService();
        Assert.AreEqual("快速连接", service.GetString("QuickConnect"));
        Assert.AreEqual("最近连接", service.GetString("RecentConnections"));
        Assert.AreEqual("断开", service.GetString("Disconnect"));
    }

    [TestMethod]
    public void LocalizationService_GetString_MissingKey_ReturnsKeyName()
    {
        var service = new LocalizationService();
        Assert.AreEqual("NonExistentKey", service.GetString("NonExistentKey"));
    }

    [TestMethod]
    public void LocalizationService_CurrentLanguage_TracksServiceCulture()
    {
        // 服务自持文化(构造时快照 CurrentUICulture,此后只随 SetLanguage 变化),
        // 取词不再依赖线程环境 —— 换语言实时刷新依赖这一点。
        CultureInfo.CurrentUICulture = new("en");
        var service = new LocalizationService();
        Assert.AreEqual("en", service.CurrentLanguage);
        service.SetLanguage("zh-CN");
        Assert.AreEqual("zh-CN", service.CurrentLanguage);
    }

    [TestMethod]
    public void LocalizationService_SetLanguage_ChangesCurrentCulture()
    {
        var service = new LocalizationService();
        service.SetLanguage("zh-CN");
        Assert.AreEqual("zh-CN", CultureInfo.CurrentUICulture.Name);
        Assert.AreEqual("zh-CN", service.CurrentLanguage);
        service.SetLanguage("en");
        Assert.AreEqual("en", CultureInfo.CurrentUICulture.Name);
        Assert.AreEqual("en", service.CurrentLanguage);
    }

    [TestMethod]
    public void AllRequiredStrings_ExistInEnglishResx()
    {
        CultureInfo.CurrentUICulture = new("en");
        Assert.IsFalse(string.IsNullOrEmpty(Strings.QuickConnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RecentConnections));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Settings));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Notifications));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.CloseTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Search));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Copy));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Split));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.FileName));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Size));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Permissions));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Modified));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Upload));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Download));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Refresh));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RemoteForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalPort));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTunnel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Network));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Docker));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Custom));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connecting));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Latency));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Save));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Cancel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Delete));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Edit));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.OK));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Error));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Warning));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Language));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Theme));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Font));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.FontSize));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ScrollbackLines));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Password));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.PrivateKey));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Username));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Host));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Port));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.HostKeyVerification));
    }

    [TestMethod]
    public void AllRequiredStrings_ExistInChineseResx()
    {
        CultureInfo.CurrentUICulture = new("zh-CN");
        Assert.IsFalse(string.IsNullOrEmpty(Strings.QuickConnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RecentConnections));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Settings));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Notifications));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.CloseTab));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Search));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Copy));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Split));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.FileName));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Size));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Permissions));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Modified));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Upload));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Download));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Refresh));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.RemoteForward));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.LocalPort));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.NewTunnel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Network));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Docker));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Custom));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connecting));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnected));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Latency));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Connect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Disconnect));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Save));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Cancel));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Delete));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Edit));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.OK));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Error));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Warning));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Language));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Theme));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Font));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.FontSize));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.ScrollbackLines));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Password));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.PrivateKey));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Username));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Host));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.Port));
        Assert.IsFalse(string.IsNullOrEmpty(Strings.HostKeyVerification));
    }

    /// <summary>
    /// 五语言键集平价:zh-Hans/zh-Hant/ja/ko 卫星资源必须与默认(英文)资源完全同键 ——
    /// 缺失 = 漏译、多余 = 孤儿键,双向都算失败。卫星按脚本中性文化命名,
    /// zh-CN/zh-TW 等具体文化经标准回退链命中(另有专项回退测试)。
    /// </summary>
    [TestMethod]
    public void AllCultures_HaveIdenticalKeySets()
    {
        var manager = new System.Resources.ResourceManager("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);
        System.Resources.ResourceSet neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var baseline = neutral.Cast<System.Collections.DictionaryEntry>()
                                          .Select(entry => (string)entry.Key)
                                          .ToHashSet();
        Assert.IsNotEmpty(baseline);
        foreach (string culture in (string[])["zh-Hans", "zh-Hant", "ja", "ko"])
        {
            System.Resources.ResourceSet? set = manager.GetResourceSet(new CultureInfo(culture), true, false);
            Assert.IsNotNull(set, $"{culture} 卫星资源缺失");
            var keys = set.Cast<System.Collections.DictionaryEntry>()
                                      .Select(entry => (string)entry.Key)
                                      .ToHashSet();
            var missing = baseline.Except(keys).Order().ToList();
            var extra = keys.Except(baseline).Order().ToList();
            Assert.IsEmpty(missing, $"{culture} 缺失 {missing.Count} 键: {string.Join(", ", missing.Take(20))}");
            Assert.IsEmpty(extra, $"{culture} 多余 {extra.Count} 键: {string.Join(", ", extra.Take(20))}");
        }
    }

    /// <summary>
    /// 具体文化沿标准回退链命中脚本中性卫星:zh-CN/zh-SG → zh-Hans,
    /// zh-TW/zh-HK → zh-Hant,ja-JP → ja。这是把卫星命名为 zh-Hans/zh-Hant
    /// 而非 zh-CN/zh-TW 的理由 —— 港澳台星等地区文化都能自动落到正确的中文。
    /// </summary>
    [TestMethod]
    public void SpecificCultures_FallBackToScriptNeutralSatellites()
    {
        var service = new LocalizationService();
        service.SetLanguage("zh-SG");
        Assert.AreEqual("快速连接", service.GetString("QuickConnect"), "zh-SG 应回退到 zh-Hans");
        service.SetLanguage("zh-TW");
        Assert.AreEqual("快速連線", service.GetString("QuickConnect"), "zh-TW 应回退到 zh-Hant");
        service.SetLanguage("zh-HK");
        Assert.AreEqual("快速連線", service.GetString("QuickConnect"), "zh-HK 应回退到 zh-Hant");
        service.SetLanguage("ja-JP");
        Assert.AreEqual("クイック接続", service.GetString("QuickConnect"), "ja-JP 应回退到 ja");
    }
}
