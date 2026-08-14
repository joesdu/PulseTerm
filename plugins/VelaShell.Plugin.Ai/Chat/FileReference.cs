using System.Text;

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

    /// <summary>
    /// 找紧挨着光标【左侧】的那条<b>已完成</b>引用(补全落定后的形态),把它整条当一个不可分的
    /// "文件块"看待 —— 退格一次删掉整块,而不是一个字符一个字符地啃。
    /// </summary>
    /// <remarks>
    /// 这正是 Claude Code / OpenCode 里选中文件后显示成一枚芯片的交互:块是原子的。
    /// 除了顺手,它还直接决定性能:啃字符会让每一次退格都变成一次新的 <c>@</c> 目录列举
    /// (每次都要发 SFTP 请求、并取消上一次),长路径退格因此又卡又刷屏。
    ///
    /// "已完成"的判据只有两种形态,和 <c>AcceptCandidate</c> 落笔的形态一一对应:
    /// <list type="bullet">
    ///   <item>带闭合引号:<c>@"/var/log/sys log"</c>(可再跟一个尾空格)</item>
    ///   <item>不带引号、以一个尾空格收尾:<c>@/var/log/syslog␣</c></item>
    /// </list>
    /// 还在敲、尚未落定的 token(既没闭引号也没尾空格)不算块 —— 那时退格照常一个字符,
    /// 用户正在改自己刚打错的那几位。路径同样必须以 <c>/</c> 或 <c>~</c> 开头,
    /// 于是 <c>@某人 </c> 这类提及不会被整段吞掉。
    /// </remarks>
    /// <param name="text">输入框全文。</param>
    /// <param name="caret">光标位置(块的右边界)。</param>
    /// <param name="start">块在全文中的起始下标(<c>@</c> 所在处)。</param>
    /// <returns>光标左侧是否正好是一条已完成引用。</returns>
    public static bool TryFindCompletedReferenceBefore(string text, int caret, out int start)
    {
        start = -1;
        caret = Math.Clamp(caret, 0, text.Length);
        // 尾空格属于这块(补全文件时是自动补上的),连它一起删,免得留下一个孤零零的空格
        int end = caret;
        bool closedBySpace = end > 0 && text[end - 1] == ' ';
        if (closedBySpace)
        {
            end--;
        }
        if (end <= 0)
        {
            return false;
        }
        bool quoted = text[end - 1] == '"';
        if (!quoted && !closedBySpace)
        {
            return false; // 没闭引号也没收尾空格 = 还在敲这条引用,退格照常一个字符
        }
        int scanFrom = quoted ? end - 2 : end - 1;
        for (int i = scanFrom; i >= 0; i--)
        {
            char c = text[i];
            if (c == '\n')
            {
                return false;
            }
            if (quoted)
            {
                // 引号形态:一路回扫到开引号 @" 为止,中间允许空格
                if (c == '"')
                {
                    return i >= 1 && text[i - 1] == '@' && IsReferenceStart(text, i - 1)
                        && Accept(text, i - 1, i + 1, end - 1, ref start);
                }
                continue;
            }
            if (c == '@')
            {
                return IsReferenceStart(text, i) && Accept(text, i, i + 1, end, ref start);
            }
            if (IsTokenBreak(c))
            {
                return false; // 未加引号的路径里不允许空白:那就不是一条引用
            }
        }
        return false;

        // 路径本体(pathStart..pathEnd)必须是绝对路径或 ~ 开头,否则不认
        static bool Accept(string text, int at, int pathStart, int pathEnd, ref int start)
        {
            if (pathEnd <= pathStart)
            {
                return false;
            }
            char first = text[pathStart];
            if (first is not ('/' or '~'))
            {
                return false;
            }
            start = at;
            return true;
        }
    }

    /// <summary><c>@</c> 只有在行首或空白之后才起头一条引用(邮箱、@某人 里的 @ 不算)。</summary>
    private static bool IsReferenceStart(string text, int at) => at == 0 || char.IsWhiteSpace(text[at - 1]);

    /// <summary>
    /// <paramref name="index" /> 处是否<b>正好起头</b>一条已完成引用;供输入框把它整段画成一枚芯片。
    /// </summary>
    /// <remarks>
    /// 判据与 <see cref="TryFindCompletedReferenceBefore" /> 同源(闭合引号,或以空白收尾),
    /// 于是"画成芯片的"和"退格整块删掉的"永远是同一段文字 —— 看得见什么就删掉什么。
    /// 差别只有一处:<paramref name="length" /> <b>不含</b>那个收尾空白,空格仍是普通文本,
    /// 光标可以正常停在芯片后面。
    /// </remarks>
    /// <param name="text">全文。</param>
    /// <param name="index">待判定的位置(应当是 <c>@</c>)。</param>
    /// <param name="length">引用本体长度(<c>@</c> 起,含闭引号,不含收尾空白)。</param>
    /// <param name="path">引用到的远端路径(不含引号)。</param>
    public static bool TryFindCompletedReferenceAt(string text, int index, out int length, out string path)
    {
        length = 0;
        path = "";
        if (index < 0 || index >= text.Length || text[index] != '@' || !IsReferenceStart(text, index))
        {
            return false;
        }
        int bodyStart = index + 1;
        int end;
        if (bodyStart < text.Length && text[bodyStart] == '"')
        {
            int close = text.IndexOf('"', bodyStart + 1);
            if (close < 0)
            {
                return false; // 引号还没闭合 = 还在敲
            }
            path = text[(bodyStart + 1)..close];
            end = close + 1;
        }
        else
        {
            int scan = bodyStart;
            while (scan < text.Length && !IsTokenBreak(text[scan]))
            {
                scan++;
            }
            // 未加引号者靠一个收尾空白判定落定;直抵文末说明用户还在敲这一条
            if (scan >= text.Length || !char.IsWhiteSpace(text[scan]))
            {
                return false;
            }
            path = text[bodyStart..scan];
            end = scan;
        }
        if (path.Length == 0 || (!path.StartsWith('/') && !path.StartsWith('~')))
        {
            return false;
        }
        length = end - index;
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

    /// <summary>
    /// 引用在界面上显示的短名:路径末段(目录保留结尾的 <c>/</c>)。
    /// 输入框里的芯片与消息气泡里的引用都用它,两处看到的必须是同一个名字。
    /// </summary>
    public static string DisplayName(string path)
    {
        string trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/"; // 根目录:没有末段可取,也别再补一道斜杠
        }
        int slash = trimmed.LastIndexOf('/');
        string name = slash < 0 ? trimmed : trimmed[(slash + 1)..];
        return path.EndsWith('/') ? name + "/" : name;
    }

    /// <summary>
    /// 把文本里每条<b>已完成</b>引用的长路径换成短名(<c>@/root/abc.txt</c> → <c>@abc.txt</c>),
    /// 供消息气泡显示 —— 用户在输入框里看到的就是短名,发出去后不该突然变回长路径。
    /// </summary>
    /// <remarks>送给模型的那份文本不走这里:模型需要完整路径。</remarks>
    public static string Shorten(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '@' && TryFindCompletedReferenceAt(text, i, out int length, out string path))
            {
                builder.Append('@').Append(DisplayName(path));
                i += length - 1;
                continue;
            }
            builder.Append(text[i]);
        }
        return builder.ToString();
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
