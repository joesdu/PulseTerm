namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 聊天面板工具条上那几处短格式:token 计数、时长、单行截断。
/// </summary>
/// <remarks>
/// 从 <c>ChatPanelView</c> 拆出来的一簇(Q-01)。它们是纯字符串函数,却原先住在一个
/// 两千八百行、要整套 Avalonia 与插件上下文才构造得起来的代码隐藏里 ——
/// 于是"1000 该显示成 1k 还是 1.0k"这类一眼能验的事,一条用例都没有。
/// </remarks>
public static class ChatFormatting
{
    /// <summary>
    /// 把 token 计数压成 <c>12.3k</c> / <c>1.2M</c> 这种短形式。
    /// </summary>
    /// <remarks>
    /// 工具条按字符宽度计价:一个 <c>1234567</c> 会把旁边的模型名挤出可视区。
    /// 一万以上只留整数位(<c>12k</c>),因为那时的小数位没有信息量、只占地方。
    /// </remarks>
    /// <param name="value">计数。</param>
    /// <returns>短形式。</returns>
    public static string Compact(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:0.#}M",
        >= 10_000 => $"{value / 1000.0:0}k",
        >= 1_000 => $"{value / 1000.0:0.#}k",
        _ => value.ToString()
    };

    /// <summary>把一段时长写成人读的短形式(<c>0.8s</c> / <c>12.3s</c> / <c>1m 5s</c>)。</summary>
    /// <param name="span">时长。</param>
    /// <returns>短形式。</returns>
    public static string Duration(TimeSpan span) => span.TotalSeconds switch
    {
        < 60 => $"{span.TotalSeconds:0.#}s",
        _ => $"{(int)span.TotalMinutes}m {span.Seconds}s"
    };

    /// <summary>
    /// 压成单行并截断到指定长度,超出部分用省略号收尾。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先折行再截断,顺序不能反:一段带换行的文本直接截,在界面上会变成一个
    /// 高度突然涨到几行的标签,把整条工具条顶变形。
    /// </para>
    /// <para>
    /// <b>逐字保留了搬过来之前的语义</b>,包括两处小别扭:<c>\r\n</c> 会压成**两个**空格
    /// (而不是一个),截断结果是 <paramref name="max" /> + 1 个字符(省略号在上限之外)。
    /// 这一批是零行为变化的重构,不在里面夹带修正 —— 真要改的话它是一处独立的小改动,
    /// 影响的是折叠详情里那行工具参数预览。
    /// </para>
    /// </remarks>
    /// <param name="text">原文。</param>
    /// <param name="max">最大字符数。</param>
    /// <returns>单行短文本。</returns>
    public static string OneLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
