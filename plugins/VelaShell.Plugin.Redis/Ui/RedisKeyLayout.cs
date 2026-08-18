using System.Collections.ObjectModel;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 把"已扫描到的一堆键"排成扁平列表:大多数键直接平铺,只有**同前缀多到成为噪音**的
/// 那几批折成分组行。纯函数,不碰界面 —— 折叠规则是这一屏最容易出错的地方,
/// 必须能脱离 Avalonia 单测。
/// </summary>
public static class RedisKeyLayout
{
    /// <summary>分组折叠阈值的下限。低于 2 的阈值等于"每个键都单独折一行",没有意义。</summary>
    public const int MinThreshold = 2;

    /// <summary>
    /// 排出一份扁平行表。
    /// <para>
    /// 算法(从当前公共前缀往下走一层):按下一段分区 → 分区里的键少于阈值就**整个平铺**,
    /// 达到阈值就折成**一条**分组行;分组行的文案取该批键的**最长公共前缀**,
    /// 因此 40 个 <c>demo:order:2026:NNNN</c> 折出来的是 <c>demo:order:2026:*</c>
    /// 而不是笼统的 <c>demo:order:*</c>。展开某条分组行时,对它的成员**递归套用同一套规则**
    /// —— 于是 200 个 <c>demo:session:*</c> 展开后仍会按下一层继续收敛,而不是甩出 200 行。
    /// </para>
    /// <para>
    /// 顶层刻意**不折**:所有键的公共前缀是面包屑(用户已经知道自己在哪儿),
    /// 把它再折成一行 <c>demo:* 46</c> 等于什么都没显示。分区从公共前缀**之后**那一段开始。
    /// </para>
    /// </summary>
    /// <param name="keys">已扫描到的键(顺序不限)。</param>
    /// <param name="delimiter">层级分隔符;空串表示不分层(全部平铺)。</param>
    /// <param name="threshold">同前缀达到几个才折;小于 <see cref="MinThreshold" /> 时按不折处理。</param>
    /// <param name="expanded">已展开的分组行 id 集合;<see langword="null" /> 表示全都折着。</param>
    /// <returns>扁平行表(按名字序)。</returns>
    public static List<RedisKeyRow> Build(
        IReadOnlyCollection<RedisKeyName> keys,
        string delimiter,
        int threshold,
        IReadOnlySet<string>? expanded = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rows = new List<RedisKeyRow>(keys.Count);
        if (keys.Count == 0)
        {
            return rows;
        }
        List<RedisKeyName> sorted = [.. keys.OrderBy(key => key.Text, StringComparer.OrdinalIgnoreCase)];
        if (string.IsNullOrEmpty(delimiter) || threshold < MinThreshold)
        {
            // 不分层 / 不折叠:一律平铺。这也是"分隔符设成空"的用户想要的效果。
            foreach (RedisKeyName key in sorted)
            {
                rows.Add(RedisKeyRow.ForKey(key));
            }
            return rows;
        }
        // 公共前缀只用来决定"从第几段开始分区",它本身在面包屑上,不进列表。
        int rootSegments = CommonSegmentCount(sorted, delimiter);
        Emit(rows, sorted, delimiter, threshold, expanded, rootSegments, depth: 0);
        return rows;
    }

    /// <summary>
    /// 面包屑:所有键共享的那几段前缀。用户看到的"我现在在哪儿"就是它。
    /// </summary>
    /// <param name="keys">已扫描到的键。</param>
    /// <param name="delimiter">层级分隔符。</param>
    /// <returns>逐段的前缀;没有公共前缀时是空表。</returns>
    public static IReadOnlyList<string> Breadcrumb(IReadOnlyCollection<RedisKeyName> keys, string delimiter)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || string.IsNullOrEmpty(delimiter))
        {
            return [];
        }
        List<RedisKeyName> list = [.. keys];
        int common = CommonSegmentCount(list, delimiter);
        return common == 0 ? [] : list[0].Segments(delimiter)[..common];
    }

    /// <summary>把一批键按"下一段"分区,逐区决定平铺还是折叠。</summary>
    private static void Emit(
        List<RedisKeyRow> rows,
        List<RedisKeyName> sorted,
        string delimiter,
        int threshold,
        IReadOnlySet<string>? expanded,
        int from,
        int depth)
    {
        int i = 0;
        while (i < sorted.Count)
        {
            string[] segments = sorted[i].Segments(delimiter);
            if (segments.Length <= from)
            {
                // 这个键正好**落在**当前前缀上(如 a:b 与 a:b:c 并存时的 a:b):
                // 它没有"下一段"可分,只能自己占一行。树在这里被迫把 b 分裂成
                // 一个键节点 + 一个前缀节点;列表里它就是一行,没有歧义。
                rows.Add(RedisKeyRow.ForKey(sorted[i], depth));
                i++;
                continue;
            }
            string head = segments[from];
            int end = i + 1;
            while (end < sorted.Count)
            {
                string[] next = sorted[end].Segments(delimiter);
                if (next.Length <= from || !string.Equals(next[from], head, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                end++;
            }
            List<RedisKeyName> partition = sorted[i..end];
            // 折叠:文案取这批键的最长公共前缀(**按段**,不按字符 —— 按字符会切出
            // `demo:order:2026:00` 这种半截数字,看着像个真前缀,其实不是)。
            int common = CommonSegmentCount(partition, delimiter);
            // common <= from 意味着这批里有键**恰好终止在当前这一层**(如 a:b 与 a:b:c 并存):
            // 再往下没有一个对所有成员都成立的真前缀,折出来的 `a:b:*` 会把 a:b 自己也算进去。
            // 这种就老实平铺。它同时保证了递归的 from 严格前进,不会打转。
            if (partition.Count < threshold || common <= from)
            {
                foreach (RedisKeyName key in partition)
                {
                    rows.Add(RedisKeyRow.ForKey(key, depth));
                }
                i = end;
                continue;
            }
            string prefix = string.Join(delimiter, partition[0].Segments(delimiter)[..common]) + delimiter;
            var group = RedisKeyRow.ForGroup(prefix, partition.Count, depth);
            bool open = expanded?.Contains(group.Id) == true;
            group.IsExpanded = open;
            rows.Add(group);
            if (open)
            {
                // 展开后对成员递归套同一套规则:大批量的键不会因为"点开了"就一次性铺满屏。
                Emit(rows, partition, delimiter, threshold, expanded, common, depth + 1);
            }
            i = end;
        }
    }

    /// <summary>
    /// 一批键共享几段前缀。
    /// <para>
    /// 结果被压到"最短的那个键的段数 - 1":前缀必须是每个成员的**真前缀**。
    /// 不压的话 <c>[a:b, a:b:c]</c> 会算出 2 段,折出一个 <c>a:b:*</c> ——
    /// 而 <c>a:b</c> 自己并不在 <c>a:b:</c> 底下,那一行就在撒谎。
    /// </para>
    /// </summary>
    private static int CommonSegmentCount(List<RedisKeyName> keys, string delimiter)
    {
        string[] first = keys[0].Segments(delimiter);
        int common = first.Length;
        int shortest = first.Length;
        for (int i = 1; i < keys.Count; i++)
        {
            string[] other = keys[i].Segments(delimiter);
            shortest = Math.Min(shortest, other.Length);
            int limit = Math.Min(common, other.Length);
            int shared = 0;
            while (shared < limit && string.Equals(first[shared], other[shared], StringComparison.OrdinalIgnoreCase))
            {
                shared++;
            }
            common = shared;
            if (common == 0)
            {
                break;
            }
        }
        return Math.Max(Math.Min(common, shortest - 1), 0);
    }

    /// <summary>
    /// 把新排出来的行表同步进界面绑定的集合:**按 id 复用既有行对象**,只补差异。
    /// <para>
    /// 不能直接 Clear 再 Add —— 扫描每来一页就重排一次,整表替换会把用户的选中项和
    /// 滚动位置一起打掉,表现成"列表一直在自己跳"。而复用行对象还顺带保住了
    /// 已经取到的类型/TTL/规模,不必重新问服务器。
    /// </para>
    /// </summary>
    /// <param name="target">界面绑定的集合。</param>
    /// <param name="desired">新排出来的行表。</param>
    public static void Sync(ObservableCollection<RedisKeyRow> target, List<RedisKeyRow> desired)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        var existing = new Dictionary<string, RedisKeyRow>(target.Count, StringComparer.Ordinal);
        foreach (RedisKeyRow row in target)
        {
            existing[row.Id] = row;
        }
        for (int i = 0; i < desired.Count; i++)
        {
            RedisKeyRow wanted = desired[i];
            if (existing.TryGetValue(wanted.Id, out RedisKeyRow? reuse))
            {
                // 复用旧对象,但可变的那部分(计数、展开态)以新算出来的为准 ——
                // 分组行的计数每来一页都在长,不搬过来就永远停在第一页的数字上。
                reuse.AdoptFrom(wanted);
                desired[i] = reuse;
            }
        }
        // 位置同步:逐格比对,不同就换掉。行数通常只增不减,绝大多数格子原地不动。
        int index = 0;
        while (index < desired.Count)
        {
            if (index >= target.Count)
            {
                target.Add(desired[index]);
            }
            else if (!ReferenceEquals(target[index], desired[index]))
            {
                target[index] = desired[index];
            }
            index++;
        }
        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
