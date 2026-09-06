using VelaShell.ViewModels;

namespace VelaShell.Services;

/// <summary>
/// 远程文件列表的排序与隐藏项过滤。
/// </summary>
/// <remarks>
/// 从 <c>FileBrowserViewModel</c> 拆出来的一簇(Q-01)。它是纯函数,却原先夹在一个
/// 三千行、要真实 SFTP 会话才构造得起来的视图模型里 —— 于是"目录永远排在最前"
/// 这条最容易被下一次改动破坏的规则,一直没有用例守着。
/// </remarks>
public static class RemoteFileSort
{
    /// <summary>按列名与方向排序;<b>目录始终分组在最前</b>。</summary>
    /// <remarks>
    /// 目录的大小无意义(多数 SFTP 服务器报 4096 或 0),混进大小排序会让它们归到
    /// 一个看起来随机的位置。把"目录在前"钉死在方向之外,列表才始终读得懂。
    /// </remarks>
    /// <param name="items">待排项。</param>
    /// <param name="column">列名(<c>size</c> / <c>permissions</c> / <c>owner</c> / <c>group</c> /
    /// <c>type</c> / <c>modified</c>,其余按名字)。</param>
    /// <param name="descending">是否降序。</param>
    /// <returns>排好序的序列。</returns>
    public static IEnumerable<RemoteFileInfoViewModel> Sort(
        IEnumerable<RemoteFileInfoViewModel> items,
        string? column,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(items);
        IOrderedEnumerable<RemoteFileInfoViewModel> dirsFirst = items.OrderByDescending(f => f.IsDirectory);
        return column switch
        {
            "size" => descending
                ? dirsFirst.ThenByDescending(f => f.SizeBytes)
                : dirsFirst.ThenBy(f => f.SizeBytes),
            "permissions" => descending
                ? dirsFirst.ThenByDescending(f => f.Permissions, StringComparer.Ordinal)
                : dirsFirst.ThenBy(f => f.Permissions, StringComparer.Ordinal),

            // 属主/属组查得到名字时排的是名字,查不到时排的是数字 id 的字符串形式
            // (即 "1000" 排在 "999" 前)—— 混排两种形式的价值不足以为此引入数值特判。
            "owner" => descending
                ? dirsFirst.ThenByDescending(f => f.Owner, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenBy(f => f.Owner, StringComparer.OrdinalIgnoreCase),
            "group" => descending
                ? dirsFirst.ThenByDescending(f => f.Group, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenBy(f => f.Group, StringComparer.OrdinalIgnoreCase),
            "type" => descending
                ? dirsFirst.ThenByDescending(f => f.FileTypeDisplay, StringComparer.CurrentCultureIgnoreCase)
                : dirsFirst.ThenBy(f => f.FileTypeDisplay, StringComparer.CurrentCultureIgnoreCase),
            "modified" => descending
                ? dirsFirst.ThenByDescending(f => f.LastModified)
                : dirsFirst.ThenBy(f => f.LastModified),
            _ => descending
                ? dirsFirst.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : dirsFirst.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>点号开头的隐藏项过滤。</summary>
    /// <param name="items">待过滤项。</param>
    /// <param name="showHidden">true = 全部保留。</param>
    /// <returns>过滤后的序列。</returns>
    public static IEnumerable<RemoteFileInfoViewModel> ApplyHiddenFilter(
        IEnumerable<RemoteFileInfoViewModel> items,
        bool showHidden)
    {
        ArgumentNullException.ThrowIfNull(items);
        return showHidden ? items : items.Where(f => !f.Name.StartsWith('.'));
    }

    /// <summary>
    /// 点一次表头之后的新排序状态。
    /// </summary>
    /// <remarks>
    /// 同一列反向、换一列则从升序开始 —— 换列时沿用上一列的方向,会让人以为点错了地方。
    /// </remarks>
    /// <param name="clicked">被点的列;调用方保证非空。</param>
    /// <param name="currentColumn">当前排序列。</param>
    /// <param name="currentDescending">当前是否降序。</param>
    /// <returns>新的列与方向。</returns>
    public static (string Column, bool Descending) NextSortState(
        string clicked, string? currentColumn, bool currentDescending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clicked);
        return clicked == currentColumn
                   ? (clicked, !currentDescending)
                   : (clicked, false);
    }
}
