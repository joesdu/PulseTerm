namespace VelaShell.Core.Models;

/// <summary>
/// 设置里"目录/路径"类取值的唯一权威解析器(下载目录、传输日志目录等)。
/// </summary>
/// <remarks>
/// 关键约定:<b>相对路径以用户主目录为基准,绝不落到进程工作目录上</b>。
/// 工作目录是外部环境决定的 —— 开机自启(<c>HKCU\...\Run</c>)时 Explorer 会把它设成
/// <c>C:\Windows\System32</c>,归一之后则是应用安装目录(商店版还是只读的 WindowsApps)——
/// 两者都不是用户填 <c>downloads</c> 时想要的地方。历史上这里有四份各自为政的展开逻辑,
/// 只处理 <c>~</c> 而把相对路径原样丢给 <c>Path.GetFullPath</c> / <c>Directory.CreateDirectory</c>,
/// 于是同一个配置值在不同功能里落到不同地方(#120)。全部统一到这里。
/// </remarks>
public static class UserPathResolver
{
    /// <summary>用户主目录。</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 把设置里的目录/路径解析成绝对路径:
    /// 空白 → <paramref name="fallback" />;<c>~</c> 开头 → 用户主目录下;
    /// 相对路径 → 用户主目录下;绝对路径 → 规范化后原样返回。
    /// </summary>
    /// <param name="configured">设置里保存的原始取值,可为 null/空。</param>
    /// <param name="fallback">取值为空时的回退路径(调用方各自的默认落点)。</param>
    public static string Resolve(string? configured, string fallback)
    {
        string value = configured?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return fallback;
        }

        string home = Home;
        if (value == "~")
        {
            return home;
        }

        // 只认 "~/" 与 "~\":TrimStart('~','/','\\') 会把 "~~/a"、"~///a" 里的多个字符一起吃掉,
        // 那是历史实现之间行为不一致的来源之一。
        if (value.StartsWith("~/", StringComparison.Ordinal)
            || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Combine(home, value[2..]);
        }

        // Path.GetFullPath(单参) 对相对路径按【进程工作目录】解析 —— 这正是要避开的行为,
        // 故相对路径显式给出用户主目录作为基准。
        return Path.IsPathRooted(value) ? Normalize(value) : Combine(home, value);
    }

    /// <summary>
    /// 同 <see cref="Resolve" />,但取值为空时回退到用户主目录。
    /// </summary>
    public static string ResolveOrHome(string? configured) => Resolve(configured, Home);

    private static string Combine(string basePath, string relative) =>
        Normalize(Path.Combine(basePath, relative.TrimStart('/', '\\')));

    /// <summary>规范化路径;路径含非法字符等无法规范化时原样返回,由调用方的存在性检查兜底。</summary>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
        catch (PathTooLongException)
        {
            return path;
        }
    }
}
