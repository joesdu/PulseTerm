using System.Collections.ObjectModel;
using VelaShell.Plugin.Redis.Ui;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 键列表的排布规则:什么时候平铺、什么时候折成分组行、折出来的前缀写什么。
/// <para>
/// 这是左栏最容易出错的一处逻辑,而且错法都很安静 —— 折错了只是"列表看起来怪",
/// 不会抛异常。所以整套规则做成纯函数并在这里逐条钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisKeyLayoutTests
{
    private const string Delimiter = ":";

    private static RedisKeyName K(string text) => new(text);

    private static List<RedisKeyName> Demo()
    {
        var keys = new List<RedisKeyName>
        {
            K("demo:config:rate-limit"),
            K("demo:user:10086:profile"),
            K("demo:user:10086:lock"),
            K("demo:user:10086:sessions"),
            K("demo:queue:email"),
            K("demo:tags:vip")
        };
        for (int i = 1; i <= 40; i++)
        {
            keys.Add(K($"demo:order:2026:{i:0000}"));
        }
        return keys;
    }

    /// <summary>真实数据的样子:40 个订单折成一行,3 个 user 键平铺 —— 与设计稿一致。</summary>
    [TestMethod]
    public void Build_FoldsOnlyTheNoisyPrefix()
    {
        List<RedisKeyRow> rows = RedisKeyLayout.Build(Demo(), Delimiter, threshold: 8);

        CollectionAssert.AreEqual(
            new[]
            {
                "demo:config:rate-limit",
                "demo:order:2026:*",
                "demo:queue:email",
                "demo:tags:vip",
                "demo:user:10086:lock",
                "demo:user:10086:profile",
                "demo:user:10086:sessions"
            },
            rows.Select(row => row.Display).ToArray());

        RedisKeyRow group = rows.Single(row => row.IsGroup);
        Assert.AreEqual(40, group.Count);
        Assert.IsFalse(group.IsExpanded);
        // 一行 = 一个完整键名。树上只有片段,这正是要改掉的东西。
        Assert.IsTrue(rows.Where(row => row.IsKey).All(row => row.Display.StartsWith("demo:", StringComparison.Ordinal)));
    }

    /// <summary>折出来的前缀要走到最深的公共段,而不是笼统的上一层。</summary>
    [TestMethod]
    public void Build_GroupPrefixReachesTheDeepestCommonSegment()
    {
        List<RedisKeyRow> rows = RedisKeyLayout.Build(Demo(), Delimiter, threshold: 8);
        RedisKeyRow group = rows.Single(row => row.IsGroup);

        // `demo:order:*` 是对的但没说到位;40 个键共享到 2026 这一段。
        Assert.AreEqual("demo:order:2026:*", group.Display);
    }

    /// <summary>顶层公共前缀不折 —— 折了等于整屏只剩一行 <c>demo:* 46</c>。</summary>
    [TestMethod]
    public void Build_NeverFoldsTheCommonRoot()
    {
        List<RedisKeyRow> rows = RedisKeyLayout.Build(Demo(), Delimiter, threshold: 2);

        Assert.IsFalse(rows is [{ Display: "demo:*" }], "整批键的公共前缀是面包屑,不该再折成一行。");
        Assert.IsGreaterThan(1, rows.Count);
    }

    /// <summary>展开分组行:成员就地铺开,并且**继续按同一套规则收敛**。</summary>
    [TestMethod]
    public void Build_ExpandedGroup_LaysOutMembersRecursively()
    {
        // 另起一个 app:cache 分支,好让公共前缀停在 app —— 否则 app:session 自己就成了
        // 面包屑根,而根是从不折叠的(见 Build_NeverFoldsTheCommonRoot)。
        var keys = new List<RedisKeyName> { K("app:cache:x") };
        foreach (string tenant in new[] { "abc", "def" })
        {
            for (int i = 0; i < 10; i++)
            {
                keys.Add(K($"app:session:{tenant}:{i:00}"));
            }
        }

        List<RedisKeyRow> collapsed = RedisKeyLayout.Build(keys, Delimiter, threshold: 8);
        CollectionAssert.AreEqual(
            new[] { "app:cache:x", "app:session:*" },
            collapsed.Select(row => row.Display).ToArray());
        RedisKeyRow group = collapsed.Single(row => row.IsGroup);
        Assert.AreEqual(20, group.Count);

        List<RedisKeyRow> expanded = RedisKeyLayout.Build(
            keys, Delimiter, threshold: 8, new HashSet<string>(StringComparer.Ordinal) { group.Id });

        // 展开后不是甩出 20 行,而是继续收敛成两条子分组 —— 这正是递归套用规则的意义。
        CollectionAssert.AreEqual(
            new[] { "app:cache:x", "app:session:*", "app:session:abc:*", "app:session:def:*" },
            expanded.Select(row => row.Display).ToArray());
        Assert.IsTrue(expanded[1].IsExpanded);
        Assert.IsTrue(expanded[2].Depth == 1 && expanded[3].Depth == 1, "展开出来的成员要缩进一级。");
    }

    /// <summary>少量同前缀的键一律平铺 —— 折叠是为了压噪音,不是为了制造点击。</summary>
    [TestMethod]
    public void Build_SmallPrefixGroupsStayFlat()
    {
        var keys = new List<RedisKeyName>
        {
            K("demo:user:10086:profile"),
            K("demo:user:10086:lock"),
            K("demo:user:10086:sessions")
        };

        List<RedisKeyRow> rows = RedisKeyLayout.Build(keys, Delimiter, threshold: 8);

        Assert.DoesNotContain(row => row.IsGroup, rows);
        Assert.HasCount(3, rows);
    }

    /// <summary>
    /// <c>a:b</c> 与 <c>a:b:c</c> 并存:不能折出 <c>a:b:*</c> —— <c>a:b</c> 自己不在它底下。
    /// <para>树被这件事逼得把 b 分裂成"一个键节点 + 一个前缀节点";列表里它就是两行,没有歧义。</para>
    /// </summary>
    [TestMethod]
    public void Build_KeyThatIsAlsoAPrefix_IsNotSwallowedByItsOwnGroup()
    {
        var keys = new List<RedisKeyName> { K("a:b"), K("a:b:c"), K("a:b:d") };

        List<RedisKeyRow> rows = RedisKeyLayout.Build(keys, Delimiter, threshold: 2);

        Assert.DoesNotContain(row => row.Display == "a:b:*", rows,
            "a:b 会被这条分组行错误地算进去。");
        CollectionAssert.AreEqual(
            new[] { "a:b", "a:b:c", "a:b:d" },
            rows.Select(row => row.Display).ToArray());
    }

    [TestMethod]
    public void Build_NoDelimiter_ListsEverythingFlat()
    {
        var keys = new List<RedisKeyName> { K("b"), K("a"), K("c") };

        List<RedisKeyRow> rows = RedisKeyLayout.Build(keys, delimiter: "", threshold: 2);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, rows.Select(row => row.Display).ToArray());
    }

    /// <summary>阈值低于下限即"不折" —— 阈值 1 会让每个键都自成一组,那是纯粹的噪音。</summary>
    [TestMethod]
    public void Build_ThresholdBelowMinimum_DisablesFolding()
    {
        List<RedisKeyRow> rows = RedisKeyLayout.Build(Demo(), Delimiter, threshold: 1);

        Assert.DoesNotContain(row => row.IsGroup, rows);
        Assert.HasCount(46, rows);
    }

    [TestMethod]
    public void Build_EmptyInput_ReturnsNoRows() =>
        Assert.IsEmpty(RedisKeyLayout.Build([], Delimiter, threshold: 8));

    [TestMethod]
    public void Breadcrumb_IsTheSharedPrefixOfEverythingScanned()
    {
        CollectionAssert.AreEqual(new[] { "demo" }, RedisKeyLayout.Breadcrumb(Demo(), Delimiter).ToArray());

        // 单个键:它自己那一段不算前缀,否则面包屑会把用户"带到"一个键上。
        CollectionAssert.AreEqual(
            new[] { "demo", "user", "10086" },
            RedisKeyLayout.Breadcrumb([K("demo:user:10086:profile")], Delimiter).ToArray());

        Assert.IsEmpty(RedisKeyLayout.Breadcrumb([K("alpha:1"), K("beta:2")], Delimiter));
        Assert.IsEmpty(RedisKeyLayout.Breadcrumb([], Delimiter));
    }

    /// <summary>
    /// 重排要**复用同 id 的行对象**:扫描每来一页就重排一次,整表替换会把选中项
    /// 和滚动位置一起打掉,表现成"列表自己在跳"。
    /// </summary>
    [TestMethod]
    public void Sync_ReusesRowObjects_AndRefreshesGroupCounts()
    {
        var target = new ObservableCollection<RedisKeyRow>();
        // demo:b:1 让公共前缀停在 demo,于是 demo:a 那一支才有得折。
        var firstPage = new List<RedisKeyName> { K("demo:a:1"), K("demo:a:2"), K("demo:b:1") };
        RedisKeyLayout.Sync(target, RedisKeyLayout.Build(firstPage, Delimiter, threshold: 2));

        RedisKeyRow groupBefore = target.Single(row => row.IsGroup);
        RedisKeyRow keyBefore = target.Single(row => row.IsKey);
        Assert.AreEqual("demo:a:*", groupBefore.Display);
        Assert.AreEqual(2, groupBefore.Count);
        Assert.AreEqual("demo:b:1", keyBefore.Display);
        // 视图模型在建行时填的那些文案也要跟着新算的走,不能停在上一页。
        groupBefore.GroupTip = "第一页的提示";

        // 第二页到了:同一条分组行的计数要变,但必须还是**同一个对象**。
        var secondPage = new List<RedisKeyName>(firstPage) { K("demo:a:3"), K("demo:a:4") };
        RedisKeyLayout.Sync(target, RedisKeyLayout.Build(secondPage, Delimiter, threshold: 2));

        RedisKeyRow groupAfter = target.Single(row => row.IsGroup);
        Assert.AreSame(groupBefore, groupAfter, "分组行被换成了新对象,选中状态会丢。");
        Assert.AreEqual(4, groupAfter.Count, "计数停在了第一页的数字上。");
        Assert.AreEqual(string.Empty, groupAfter.GroupTip, "旧文案要被新算的那一份整份顶掉。");
        Assert.AreSame(keyBefore, target.Single(row => row.IsKey), "键行也要复用,否则选中项会被顶掉。");
    }

    [TestMethod]
    public void Sync_ShrinkingList_DropsTheTail()
    {
        var target = new ObservableCollection<RedisKeyRow>();
        RedisKeyLayout.Sync(target, RedisKeyLayout.Build(
            [K("a:1"), K("b:1"), K("c:1")], Delimiter, threshold: 8));
        Assert.HasCount(3, target);

        RedisKeyLayout.Sync(target, RedisKeyLayout.Build([K("a:1")], Delimiter, threshold: 8));

        Assert.HasCount(1, target);
        Assert.AreEqual("a:1", target[0].Display);
    }
}
