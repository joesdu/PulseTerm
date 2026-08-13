using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;

namespace VelaShell.Tests.Localization;

/// <summary>
/// 界面引用的每个本地化键都必须在资源里真实存在。
/// </summary>
/// <remarks>
/// 这是缺键唯一的兜底:Strings.cs 取不到键时回退成键名本身(GetString(key) ?? key),
/// 既不抛也不记日志 —— 界面在所有语言下都直接显示英文键名。用户报的 SFTP 面板关闭按钮
/// 到处显示 "Close" 就是这么来的:{loc:Localize Close} 写了,resx 里却从没加过 Close。
///
/// 已有的 LocalizationTests.AllCultures_HaveIdenticalKeySets 管不到这一类:它比的是五种语言
/// 之间键集是否一致,而 Close 是五个文件里都没有 —— 平价成立,照样漏。那条测的是「翻译齐不齐」,
/// 这条测的是「引用的键存不存在」。
/// </remarks>
[TestClass]
[TestCategory("i18n")]
public partial class LocalizedKeyUsageTests
{
    /// <summary>XAML 里的位置参数写法 {loc:Localize SomeKey}(LocalizeExtension 的唯一用法)。</summary>
    [GeneratedRegex(@"\{loc:Localize\s+([A-Za-z0-9_]+)\s*\}")]
    private static partial Regex XamlKey { get; }

    /// <summary>代码里的字面量取词 Strings.Get("SomeKey");变量传参匹配不到,也不该匹配。</summary>
    [GeneratedRegex(@"Strings\.Get\(""([A-Za-z0-9_]+)""\)")]
    private static partial Regex CodeKey { get; }

    [TestMethod]
    public void EveryLocalizeKeyUsedInXaml_ExistsInResources()
    {
        AssertAllKeysDefined("*.axaml", XamlKey, minimumExpected: 100);
    }

    [TestMethod]
    public void EveryLocalizeKeyUsedInCode_ExistsInResources()
    {
        AssertAllKeysDefined("*.cs", CodeKey, minimumExpected: 50);
    }

    /// <summary>
    /// 扫 src 下所有匹配文件里的键,逐个比对中性资源。
    /// </summary>
    /// <param name="filePattern">要扫描的文件通配符(如 *.axaml)。</param>
    /// <param name="pattern">从文件内容里捞出键名的正则,第 1 个捕获组为键。</param>
    /// <param name="minimumExpected">
    /// 至少该扫到多少个键。没有这道下限,一旦扫描路径失效(挪目录、改布局)就一个键都找不到,
    /// 测试会安静地变成永远通过的空壳 —— 那正是它本该拦住的那种失败。
    /// </param>
    private static void AssertAllKeysDefined(string filePattern, Regex pattern, int minimumExpected)
    {
        HashSet<string> defined = DefinedKeys();
        Dictionary<string, string> missing = [];
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(SourceRoot(), filePattern, SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Value;
                found.Add(key);
                if (!defined.Contains(key))
                {
                    missing.TryAdd(key, Path.GetFileName(file));
                }
            }
        }

        Assert.IsGreaterThanOrEqualTo(minimumExpected, found.Count,
                                      $"只在 {filePattern} 里扫到 {found.Count} 个键,远低于预期 —— 扫描八成失效了,别让这条测试变成空壳。");
        Assert.IsEmpty(missing,
                       "以下键被界面引用但资源里没有,会在所有语言下显示成英文键名:\n" +
                       string.Join("\n", missing.Select(entry => $"  {entry.Key}  ({entry.Value})")));
    }

    /// <summary>中性(英文)资源里已定义的全部键。</summary>
    private static HashSet<string> DefinedKeys()
    {
        var manager = new ResourceManager("VelaShell.Core.Resources.Strings", typeof(Strings).Assembly);
        ResourceSet neutral = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var keys = neutral.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.Ordinal);
        Assert.IsNotEmpty(keys, "中性资源为空,后面的比对就没意义了。");
        return keys;
    }

    /// <summary>从测试输出目录向上找到仓库里的 src 目录。</summary>
    private static string SourceRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "src");
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库的 src 目录(找不到同级的 VelaShell.slnx)。");
    }
}
