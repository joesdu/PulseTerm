using System.Reflection;
using System.Text.RegularExpressions;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 插件自理的多语言表:每个键都必须齐五种语言。
/// </summary>
/// <remarks>
/// <see cref="Loc" /> 按下标取值(<c>values[_index]</c>),少一项就是运行时的
/// <see cref="IndexOutOfRangeException" /> —— 而且只有把界面切到日语或韩语才撞得到,
/// 开发机上多半是中文,永远发现不了。宿主那边由 <c>LocalizedKeyUsageTests</c> 守着五份 resx,
/// 插件这份表同样需要一个守门的。
/// </remarks>
[TestClass]
public sealed class LocTableTests
{
    private static Dictionary<string, string[]> Table()
    {
        FieldInfo field = typeof(Loc).GetField("Table", BindingFlags.NonPublic | BindingFlags.Static)
                          ?? throw new InvalidOperationException("Loc.Table is gone — update this test.");
        return (Dictionary<string, string[]>)field.GetValue(null)!;
    }

    [TestMethod]
    public void EveryKey_HasAllFiveLanguages()
    {
        List<string> broken =
        [
            .. Table().Where(entry => entry.Value.Length != 5)
                      .Select(entry => $"{entry.Key} ({entry.Value.Length})")
        ];

        Assert.IsEmpty(broken,
            $"These keys do not have exactly five translations: {string.Join(", ", broken)}");
    }

    [TestMethod]
    public void NoTranslation_IsBlank()
    {
        List<string> blank =
        [
            .. Table().Where(entry => entry.Value.Any(string.IsNullOrWhiteSpace)).Select(entry => entry.Key)
        ];

        Assert.IsEmpty(blank, $"These keys have a blank translation: {string.Join(", ", blank)}");
    }

    /// <summary>取词真的跟着语言走(顺序 en / zh-Hans / zh-Hant / ja / ko)。</summary>
    [TestMethod]
    public void Lookup_FollowsTheLocale()
    {
        Assert.AreEqual("Copy", new Loc("en")["Copy"]);
        Assert.AreNotEqual(new Loc("en")["BridgeThinking"], new Loc("zh-Hans")["BridgeThinking"]);
        Assert.AreNotEqual(new Loc("ja")["BridgeThinking"], new Loc("ko")["BridgeThinking"]);
    }

    /// <summary>缺键时返回键名本身,而不是抛 —— 少一句文案不该让面板打不开。</summary>
    [TestMethod]
    public void MissingKey_ReturnsTheKey()
        => Assert.AreEqual("NoSuchKeyHere", new Loc("en")["NoSuchKeyHere"]);

    /// <summary>
    /// 同一个键不许在表里出现两次。
    /// </summary>
    /// <remarks>
    /// <b>这条只能按源码查,运行时查不出来。</b>表是用索引器 <c>["key"] = […]</c> 初始化的,
    /// 重复键是<b>静默覆盖</b>(换成集合初始化器的 <c>Add</c> 才会抛),所以 <see cref="Table" />
    /// 里根本看不出曾经有过两条。
    /// <para>
    /// 真事:设置页的「审批超时(秒)」标签与 IM 里那句「没人应答,按拒绝处理。」都叫
    /// <c>BridgeApprovalTimeout</c>,后写的把前面盖掉 —— 于是输入框上方的标签变成了一整句话。
    /// 编译、测试、启动全都正常,只有把界面渲染出来看才发现。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void NoKeyIsDefinedTwice()
    {
        string source = LocSourcePath();
        List<string> duplicates =
        [
            .. Regex.Matches(File.ReadAllText(source), @"^\s*\[""([A-Za-z0-9]+)""\]\s*=", RegexOptions.Multiline)
                    .Select(m => m.Groups[1].Value)
                    .GroupBy(k => k, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key} (×{g.Count()})")
        ];

        Assert.IsEmpty(duplicates,
            "these keys are defined more than once; the later one silently wins: " + string.Join(", ", duplicates));
    }

    /// <summary>从测试程序集往上找到 <c>Loc.cs</c>。找不到就失败 —— 跳过会被记成通过。</summary>
    private static string LocSourcePath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "plugins", "VelaShell.Plugin.Ai", "Ui", "Loc.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException($"Could not find Loc.cs above '{AppContext.BaseDirectory}'.");
    }
}
