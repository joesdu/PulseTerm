using System.Globalization;
using System.Text.RegularExpressions;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 快捷键总表的守护:<see cref="ShortcutCatalog" /> 是设置页与
/// <c>docs/快捷键参考.md</c> 的共同来源,这里保证三者不会各说各话。
/// </summary>
/// <remarks>
/// 快捷键最容易腐坏的地方不是代码,而是「加了绑定却没人记得改表」——
/// 界面照常工作,只有参考页和文档在悄悄说谎,而说谎的参考页比没有参考页更糟。
/// 因此这里把 <c>MainWindow.axaml</c> 的 <c>KeyBinding</c> 当作事实,
/// 反向要求总表登记;文档同理:每一条目录条目都必须能在文档表格里找到同名同键的一行。
/// </remarks>
[TestClass]
public partial class ShortcutCatalogTests
{
    /// <summary>Avalonia 手势里的键名 → 表里展示的写法。</summary>
    private static readonly Dictionary<string, string> GestureAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OemComma"] = ",",
        ["OemPeriod"] = ".",
        ["OemMinus"] = "-",
        ["OemPlus"] = "+",
    };

    [GeneratedRegex(@"<KeyBinding\s+Gesture=""([^""]+)""")]
    private static partial Regex KeyBindingGesture { get; }

    /// <summary>
    /// <c>MainWindow.axaml</c> 里每一条 <c>KeyBinding</c> 都必须出现在总表里。
    /// 新加全局键位却忘了登记时,这条会直接点名说是哪个手势。
    /// </summary>
    [TestMethod]
    public void EveryMainWindowKeyBinding_IsListedInCatalog()
    {
        string axaml = File.ReadAllText(Path.Combine(SourceRoot(), "VelaShell", "Views", "MainWindow.axaml"));
        List<string> gestures = [.. KeyBindingGesture.Matches(axaml).Select(match => Normalize(match.Groups[1].Value))];
        Assert.IsGreaterThanOrEqualTo(10, gestures.Count,
                                      $"只在 MainWindow.axaml 里扫到 {gestures.Count} 条 KeyBinding —— 扫描八成失效了,别让这条测试变成空壳。");

        HashSet<string> catalog = CatalogCombos();
        List<string> missing = [.. gestures.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(gesture => !catalog.Contains(gesture))
            .Order(StringComparer.Ordinal)];

        Assert.IsEmpty(missing,
                       "以下全局键位已绑定但没登记进 ShortcutCatalog(设置页与 docs/快捷键参考.md 都会漏掉它们):\n" +
                       string.Join("\n", missing.Select(gesture => $"  {gesture}")));
    }

    /// <summary>
    /// 同一分组里不得出现「动作名 + 键位」完全相同的两行 —— 那只会是复制粘贴的残留。
    /// 跨分组重名是允许的(Esc 在多处都关东西),同名不同键也是允许的
    /// (翻页有 PageUp 与 Shift+PageUp 两行,条件不同)。
    /// </summary>
    [TestMethod]
    public void Catalog_HasNoDuplicateRowsWithinAGroup()
    {
        List<string> duplicates = [.. ShortcutCatalog.Build()
            .SelectMany(group => group.Items.Select(item => $"{group.Title} / {item.Label} / {Combo(item)}"))
            .GroupBy(row => row, StringComparer.Ordinal)
            .Where(rows => rows.Count() > 1)
            .Select(rows => rows.Key)
            .Order(StringComparer.Ordinal)];

        Assert.IsEmpty(duplicates, "总表里有重复行:\n" + string.Join("\n", duplicates.Select(row => $"  {row}")));
    }

    /// <summary>
    /// 每条文案都必须真的取到译文。<c>Strings.Get</c> 取不到时会原样回退成键名,
    /// 设置页于是显示 "Sc_OpenLink" 这种东西 —— 静默失败,只能靠这里拦。
    /// </summary>
    [TestMethod]
    public void Catalog_HasNoUnresolvedResourceKeys()
    {
        ShortcutGroup[] groups = ShortcutCatalog.Build();
        List<string> unresolved =
        [
            .. groups.Select(group => group.Title)
                .Concat(ShortcutCatalog.Flatten(groups).Select(item => item.Label))
                .Concat(ShortcutCatalog.Flatten(groups).Select(item => item.Note ?? string.Empty))
                .Where(LooksLikeResourceKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.IsEmpty(unresolved,
                       "以下文案没取到译文(资源里缺键,界面会直接显示键名):\n" +
                       string.Join("\n", unresolved.Select(key => $"  {key}")));
    }

    /// <summary>
    /// 文档必须与总表逐条对齐:每个目录条目都要在 <c>docs/快捷键参考.md</c> 的表格里
    /// 找到「动作名 + 键位」都相同的一行。失败信息直接给出可粘贴的 Markdown 行,
    /// 补文档不用再手抄一遍。
    /// </summary>
    [TestMethod]
    public void Doc_ListsEveryCatalogEntry()
    {
        // 文档以简体中文书写,取词文化必须钉死,否则本机语言一变整测失败。
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("zh-Hans");
            string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "快捷键参考.md"));
            List<string> missing = [.. ShortcutCatalog.Build()
                .SelectMany(group => group.Items)
                .Where(item => !DocHasRow(doc, item))
                .Select(item => $"| {item.Label} | `{Combo(item)}` | {item.Note ?? "—"} |")
                .Distinct(StringComparer.Ordinal)];

            Assert.IsEmpty(missing,
                           "docs/快捷键参考.md 缺少以下条目(新增快捷键必须同步文档,直接粘贴下面这几行):\n" +
                           string.Join("\n", missing));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>文档里是否有一行同时含该条目的动作名与键位(表格列序无关)。</summary>
    private static bool DocHasRow(string doc, ShortcutItem item)
    {
        string combo = $"`{Combo(item)}`";
        return doc.Split('\n')
                  .Any(line => line.Contains($"| {item.Label} |", StringComparison.Ordinal)
                            && line.Contains(combo, StringComparison.Ordinal));
    }

    private static HashSet<string> CatalogCombos() =>
        [.. ShortcutCatalog.Flatten(ShortcutCatalog.Build()).Select(Combo)];

    private static string Combo(ShortcutItem item) => string.Join('+', item.Keys);

    /// <summary>把 Avalonia 手势串规整成表里的写法(Ctrl+OemComma → Ctrl+,)。</summary>
    private static string Normalize(string gesture) =>
        string.Join('+', gesture.Split('+').Select(part => GestureAliases.GetValueOrDefault(part, part)));

    /// <summary>形如 Sc_Xxx / Cmd_Xxx / SetVm_Xxx 的裸键名 —— 只可能是取词失败的回退值。</summary>
    private static bool LooksLikeResourceKey(string text) =>
        text.StartsWith("Sc_", StringComparison.Ordinal)
        || text.StartsWith("Cmd_", StringComparison.Ordinal)
        || text.StartsWith("SetVm_", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }

    private static string SourceRoot() => Path.Combine(RepoRoot(), "src");
}
