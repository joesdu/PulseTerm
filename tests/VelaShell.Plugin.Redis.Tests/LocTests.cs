namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 文案表本身的完整性。
/// <para>
/// 这组测试守的是两类**编译期看不出、运行期才炸**的问题:字典初始化器里的重复键
/// (静态构造时抛 <c>ArgumentException</c>,一炸就是整个插件不可用),
/// 以及两种语言键集不齐平(中文界面上莫名冒出一句英文,或直接显示键名)。
/// </para>
/// </summary>
[TestClass]
public sealed class LocTests
{
    [TestMethod]
    public void Tables_Initialize_WithoutDuplicateKeys()
    {
        // 触发静态构造。重复键会在这里抛。
        Assert.IsGreaterThan(100, Loc.AllKeys.Count);
    }

    [TestMethod]
    public void BothLanguages_CoverTheSameKeys()
    {
        var english = Loc.KeysOf(chinese: false).ToHashSet(StringComparer.Ordinal);
        var chinese = Loc.KeysOf(chinese: true).ToHashSet(StringComparer.Ordinal);

        string[] missingInChinese = [.. english.Except(chinese).Order()];
        string[] missingInEnglish = [.. chinese.Except(english).Order()];

        Assert.IsEmpty(missingInChinese, $"中文表缺:{string.Join(", ", missingInChinese)}");
        Assert.IsEmpty(missingInEnglish, $"英文表缺:{string.Join(", ", missingInEnglish)}");
    }

    [TestMethod]
    public void EveryKey_ResolvesInBothLanguages()
    {
        var zh = new Loc("zh-Hans");
        var en = new Loc("en");

        foreach (string key in Loc.AllKeys)
        {
            // 未收录的键原样返回 —— 那意味着界面上会出现一个键名。
            Assert.AreNotEqual(key, zh[key], $"中文表未收录 {key}");
            Assert.AreNotEqual(key, en[key], $"英文表未收录 {key}");
        }
    }

    [TestMethod]
    public void PlaceholderCounts_MatchAcrossLanguages()
    {
        // 占位符个数不一致 = 换一种语言就 FormatException。
        var zh = new Loc("zh-Hans");
        var en = new Loc("en");

        foreach (string key in Loc.AllKeys)
        {
            Assert.AreEqual(
                CountPlaceholders(en[key]),
                CountPlaceholders(zh[key]),
                $"{key} 的占位符个数在两种语言里不一致");
        }
    }

    [TestMethod]
    public void UnknownKey_ReturnsTheKeyItself()
    {
        // 刻意的:漏了哪条一眼就能在界面上看出来,而不是显示一片空白。
        var loc = new Loc("en");

        Assert.AreEqual("Redis_NoSuchKey", loc["Redis_NoSuchKey"]);
    }

    [TestMethod]
    public void UnknownLocale_FallsBackToEnglish()
    {
        var loc = new Loc("ja");

        Assert.AreEqual("Server address", loc["Redis_Host"]);
    }

    private static int CountPlaceholders(string text)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i + 2 < text.Length + 1; i++)
        {
            if (text[i] != '{' || i + 1 >= text.Length || !char.IsDigit(text[i + 1]))
            {
                continue;
            }
            seen.Add(text[i + 1] - '0');
        }
        return seen.Count;
    }
}
