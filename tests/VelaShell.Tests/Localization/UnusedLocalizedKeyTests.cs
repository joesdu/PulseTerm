using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using VelaShell.Core.Resources;

namespace VelaShell.Tests.Localization;

/// <summary>
/// 资源里不该留下没人引用的键。
/// </summary>
/// <remarks>
/// 与 <see cref="LocalizedKeyUsageTests" /> 正好相反的方向:那条查「引用了但没定义」(界面显示英文键名),
/// 这条查「定义了但没人引用」。后者不会让界面出错,所以没有任何自然反馈会暴露它 ——
/// 只会让五个 resx 一路膨胀:每加一条死键,翻译方就要多译五份、审校多看五处,
/// 而删掉一段界面时对应的文案几乎不会有人记得回来清。2026-08-13 首次清理时,
/// 1253 个键里有 14 个已经没有任何引用(含两条被 Tunnel_Route* 取代后遗留的旧键)。
///
/// 判定用法时刻意放宽:任何 .cs / .axaml 文件里出现该键名(哪怕只是同名标识符)都算命中。
/// 宁可漏掉几个死键,也不能把还在用的键判死 —— 后者会在 <see cref="ResourceManager" /> 取不到时
/// 静默回退成键名,直接显示到用户脸上。
/// </remarks>
[TestClass]
[TestCategory("i18n")]
public partial class UnusedLocalizedKeyTests
{
    /// <summary>
    /// 运行期拼出来的键,静态扫描必然看不见,按前缀豁免。
    /// 新增此类用法时必须同步登记在这里,并写清拼接点在哪。
    /// </summary>
    private static readonly string[] ComposedKeyPrefixes =
    [
        // PluginManagerViewModel.StatusText:Strings.Get($"PluginState_{descriptor.State}")
        "PluginState_"
    ];

    /// <summary>
    /// 字面量与标记里的裸词,用作「这个键还有人提」的宽松证据。
    /// 长度不设下限:首版写成至少三个字符,结果两字母的 <c>OK</c> 键永远扫不到,
    /// 被当成死键删掉后 MessageDialog 立刻编译不过 —— 短键正是最容易被漏判的那一类。
    /// </summary>
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*")]
    private static partial Regex Word { get; }

    [TestMethod]
    public void EveryResourceKey_IsReferencedSomewhere()
    {
        HashSet<string> referenced = ReferencedWords();
        List<string> orphans = [.. DefinedKeys()
            .Where(key => !referenced.Contains(key))
            .Where(key => !ComposedKeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)];

        Assert.IsEmpty(orphans,
                       "以下资源键已无人引用,请连同五个 resx 一起删掉(确属运行期拼接的,登记到 ComposedKeyPrefixes):\n" +
                       string.Join("\n", orphans.Select(key => $"  {key}")));
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

    /// <summary>
    /// 源码(src 与 plugins)里出现过的全部单词。Strings.cs 例外:
    /// 强类型访问器自身的声明不算引用,否则一条没人调用的属性会把对应的键一直养着。
    /// </summary>
    private static HashSet<string> ReferencedWords()
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        int scanned = 0;
        foreach (string file in SourceFiles())
        {
            if (Path.GetFileName(file) == "Strings.cs")
            {
                continue;
            }
            scanned++;
            foreach (Match match in Word.Matches(File.ReadAllText(file)))
            {
                words.Add(match.Value);
            }
        }

        // 没有这道下限,扫描路径一旦失效(挪目录、改布局)就会一个词都扫不到,
        // 于是「全部键都没人引用」——一条本该守门的测试反过来把整份资源判死。
        Assert.IsGreaterThanOrEqualTo(200, scanned,
                                      $"只扫到 {scanned} 个源码文件,远低于预期 —— 扫描八成失效了。");
        return words;
    }

    private static IEnumerable<string> SourceFiles()
    {
        string root = RepositoryRoot();
        foreach (string area in (string[])["src", "plugins"])
        {
            string dir = Path.Combine(root, area);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                {
                    if (!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    /// <summary>从测试输出目录向上找到仓库根(以 VelaShell.slnx 为锚)。</summary>
    private static string RepositoryRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根(找不到 VelaShell.slnx)。");
    }
}
