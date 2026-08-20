namespace VelaShell.ViewModels;

/// <summary>
/// 把用户手动敲进路径栏的文本规整成可直接导航的 POSIX 绝对路径(#226)。
/// <para>
/// 抽成纯函数是为了可单测:路径规整的坑(相对路径、<c>..</c> 越过根、重复斜杠、
/// 粘贴带引号)全在这里收口,视图模型只管拿结果去导航。
/// </para>
/// <para>
/// 刻意<b>不</b>把反斜杠当分隔符 —— POSIX 下 <c>\</c> 是合法文件名字符,替换会让
/// 名字里带反斜杠的目录永远打不开。
/// </para>
/// </summary>
public static class RemotePathInput
{
    /// <summary>
    /// 规整一条手动输入的远程路径。
    /// </summary>
    /// <param name="input">用户输入的原文。</param>
    /// <param name="currentPath">当前目录,用于解析相对路径。</param>
    /// <param name="homePath">
    /// 远端家目录,用于展开 <c>~</c>;未知(null/空)时保留 <c>~</c> 原样,
    /// 让服务端给出「无此目录」的真实错误,而不是在本地悄悄猜一个。
    /// </param>
    /// <returns>规整后的绝对路径;输入为空白时返回 null(表示无需导航)。</returns>
    public static string? Normalize(string? input, string currentPath, string? homePath)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        string text = Unquote(input.Trim());
        if (text.Length == 0)
        {
            return null;
        }

        // ~ / ~/sub → 家目录。~user 形式无法在本地解析(需要读远端 passwd),原样透传。
        if (
            !string.IsNullOrWhiteSpace(homePath)
            && (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
        )
        {
            string tail = text.Length > 1 ? text[2..] : string.Empty;
            text = tail.Length == 0 ? homePath! : homePath!.TrimEnd('/') + "/" + tail;
        }

        // 相对路径以当前目录为基准;绝对路径直接用。
        string combined = text.StartsWith('/')
            ? text
            : (string.IsNullOrEmpty(currentPath) ? "/" : currentPath).TrimEnd('/') + "/" + text;

        return Collapse(combined);
    }

    /// <summary>去掉整体包裹的一层成对引号(粘贴 <c>"/var/log"</c> 这类内容时常见)。</summary>
    private static string Unquote(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }
        char first = text[0];
        return (first is '"' or '\'') && text[^1] == first ? text[1..^1].Trim() : text;
    }

    /// <summary>折叠 <c>.</c>、<c>..</c> 与重复斜杠;<c>..</c> 在根目录处停住,不会越过根。</summary>
    private static string Collapse(string path)
    {
        var stack = new List<string>();
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    continue;
                default:
                    stack.Add(segment);
                    continue;
            }
        }
        return stack.Count == 0 ? "/" : "/" + string.Join('/', stack);
    }
}
