namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 输入框里 <c>@</c> 文件引用的语法(与界面无关的纯逻辑,便于单测)。
/// 规则:<c>@</c> 必须在行首或空白之后;含空格的路径写成 <c>@"…"</c>,
/// 目录下钻期间开引号可以一直敞着(还没输完);只认绝对路径与 <c>~</c> 开头,
/// 因此 <c>@someone</c> 这类提及不会被误当成文件。
/// </summary>
public static class FileReference
{
    /// <summary>
    /// 找光标处正在输入的引用 token。
    /// </summary>
    /// <param name="text">输入框全文。</param>
    /// <param name="caret">光标位置。</param>
    /// <param name="start"><c>@</c> 在全文中的下标。</param>
    /// <param name="quoted">是否是 <c>@"</c> 形式。</param>
    /// <param name="token"><c>@</c>(与开引号)之后到光标之间的内容。</param>
    /// <returns>光标是否正处在一次引用里。</returns>
    public static bool TryFindToken(string text, int caret, out int start, out bool quoted, out string token)
    {
        start = -1;
        quoted = false;
        token = "";
        caret = Math.Clamp(caret, 0, text.Length);
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '\n')
            {
                return false;
            }
            if (c == '"')
            {
                // token 里唯一允许的引号是 @" 的开引号(下钻含空格路径时它一直敞着)
                if (i >= 1 && text[i - 1] == '@' && (i - 1 == 0 || char.IsWhiteSpace(text[i - 2])))
                {
                    start = i - 1;
                    quoted = true;
                    break;
                }
                return false;
            }
            if (c == '@' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                start = i;
                break;
            }
            if (IsTokenBreak(c) && !HasOpenQuote(text, i))
            {
                return false;
            }
        }
        if (start < 0)
        {
            return false;
        }
        token = text[(start + (quoted ? 2 : 1))..caret];
        return true;
    }

    /// <summary>把 token 拆成「要列的目录」与「过滤词」;<c>~</c> 与相对路径按工作目录展开。</summary>
    public static (string Directory, string Filter) Split(string token, string workingDirectory)
    {
        string cwd = string.IsNullOrEmpty(workingDirectory) ? "/" : workingDirectory;
        string expanded = token.StartsWith('~') ? cwd.TrimEnd('/') + token[1..] : token;
        int slash = expanded.LastIndexOf('/');
        if (slash < 0)
        {
            return (cwd, expanded);
        }
        string directory = slash == 0 ? "/" : expanded[..slash];
        if (!directory.StartsWith('/'))
        {
            directory = cwd.TrimEnd('/') + "/" + directory;
        }
        return (directory, expanded[(slash + 1)..]);
    }

    /// <summary>从一条消息里提取被引用的远端路径(去重、保序)。</summary>
    public static List<string> Parse(string text)
    {
        var paths = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '@' || (i > 0 && !char.IsWhiteSpace(text[i - 1])))
            {
                continue;
            }
            int start = i + 1;
            string path;
            if (start < text.Length && text[start] == '"')
            {
                int end = text.IndexOf('"', start + 1);
                if (end < 0)
                {
                    break; // 引号没闭合:后面的都不算
                }
                path = text[(start + 1)..end];
                i = end;
            }
            else
            {
                int end = start;
                while (end < text.Length && !IsTokenBreak(text[end]))
                {
                    end++;
                }
                path = text[start..end];
                i = end;
            }
            // 句末标点不属于路径(“看看 @/etc/hosts。”)
            path = path.TrimEnd('.', ',', ';', ':', ')');
            if ((path.StartsWith('/') || path.StartsWith('~')) && !paths.Contains(path, StringComparer.Ordinal))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary><c>~</c> 展开为工作目录。</summary>
    public static string Expand(string path, string workingDirectory)
        => path.StartsWith('~') ? workingDirectory.TrimEnd('/') + path[1..] : path;

    /// <summary>头 1KB 里出现 NUL 就当二进制(和 <c>file</c> 的粗判一个路子)。</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
        => bytes[..Math.Min(bytes.Length, 1024)].IndexOf((byte)0) >= 0;

    /// <summary>
    /// 未加引号的路径到哪儿为止:空白,或任何 CJK/全角/表情区字符(U+2E80 以上)。
    /// 中文句子里的「@/etc/hosts,然后…」标点后不带空格,不在这里断开就会把整句吞进路径;
    /// 代价是<b>非 ASCII 的路径必须写成 <c>@"…"</c></b> —— 补全插入时会自动加引号。
    /// </summary>
    public static bool IsTokenBreak(char c) => char.IsWhiteSpace(c) || c >= '\u2E80';

    /// <summary>该路径在插入输入框时是否需要 <c>@"…"</c> 包裹(含空格或非 ASCII)。</summary>
    public static bool NeedsQuoting(string path) => path.Any(IsTokenBreak);

    /// <summary>位置 <paramref name="index" /> 是否落在一个未闭合的 <c>@"</c> 引用里。</summary>
    private static bool HasOpenQuote(string text, int index)
    {
        for (int i = index; i >= 1; i--)
        {
            if (text[i] == '\n')
            {
                return false;
            }
            if (text[i] == '"' && text[i - 1] == '@' && (i - 1 == 0 || char.IsWhiteSpace(text[i - 2])))
            {
                return true;
            }
        }
        return false;
    }
}
