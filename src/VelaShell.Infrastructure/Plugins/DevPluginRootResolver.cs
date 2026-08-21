namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 开发期插件根目录的来源解析。让插件作者把工程的 <c>bin/Debug/net11.0</c> 直接挂进宿主,
/// 免去"打包 → 安装 → 再看效果"这一圈:
/// <list type="number">
///   <item>启动参数 <c>--dev-root &lt;dir&gt;</c>(可重复;跟着 IDE 启动配置走,工程本地);</item>
///   <item>环境变量 <c>VELA_PLUGIN_DEV_ROOT</c>(多条以 <see cref="Path.PathSeparator" /> 分隔);</item>
///   <item>数据根目录下的 <c>plugins.dev.txt</c>,每行一个目录,<c>#</c> 起头为注释。</item>
/// </list>
/// <para>
/// 三者叠加(不是互斥),顺序即上表 —— 参数最先,因为它最"局部":同时开两个插件工程时,
/// 各自的启动配置各说各的,而环境变量与登记文件是机器级的全局状态,必然互相串味。
/// </para>
/// <para>
/// 刻意用纯文本而不是 JSON:这个文件既要人手改,也要被模板工程的构建目标追加一行,
/// 纯文本两边都省事,也不会因为一个逗号让宿主启动路径上多一处解析失败。
/// </para>
/// <para>
/// 路径支持环境变量(<c>%VAR%</c> / <c>$VAR</c> 由 OS 约定)与起首的 <c>~</c>(用户主目录)。
/// </para>
/// </summary>
public static class DevPluginRootResolver
{
    /// <summary>环境变量名。</summary>
    public const string EnvironmentVariable = "VELA_PLUGIN_DEV_ROOT";

    /// <summary>数据根目录下的登记文件名。</summary>
    public const string ListFileName = "plugins.dev.txt";

    /// <summary>调试目标环境变量名(见 <see cref="PluginManagerOptions.DebugPluginIds" />)。</summary>
    public const string DebugEnvironmentVariable = "VELA_PLUGIN_WAIT_DEBUGGER";

    /// <summary>
    /// 解析要等待调试器的插件 id 集合:启动参数 <c>--wait-debugger</c> 与环境变量
    /// <c>VELA_PLUGIN_WAIT_DEBUGGER</c> 并集,取 <c>*</c>(全部)或以逗号/分号分隔的插件 id。
    /// 两处都没有时返回空集合(生产路径分文不动)。
    /// </summary>
    /// <param name="commandLineIds">启动参数给出的 id(见 <c>VelaShellStartupArguments</c>)。</param>
    public static IReadOnlyCollection<string> ResolveDebugPluginIds(IReadOnlyCollection<string>? commandLineIds = null)
    {
        var ids = new List<string>(commandLineIds ?? []);
        ids.AddRange((Environment.GetEnvironmentVariable(DebugEnvironmentVariable) ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return [.. ids.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 解析开发期插件根目录。返回去重后的绝对路径(保持来源顺序);不存在的目录也一并返回 ——
    /// 发现期本就跳过不存在的根,而保留它们能让"路径写错了"在日志里看得见。
    /// </summary>
    /// <param name="dataRootDirectory">宿主数据根目录(<c>plugins.dev.txt</c> 所在处)。</param>
    /// <param name="commandLineRoots">启动参数给出的根(见 <c>VelaShellStartupArguments</c>),排在最前。</param>
    /// <param name="readFile">文件读取器(测试注入用);默认读磁盘。</param>
    public static IReadOnlyList<string> Resolve(string dataRootDirectory,
        IReadOnlyList<string>? commandLineRoots = null, Func<string, string[]?>? readFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRootDirectory);
        readFile ??= ReadLinesOrNull;

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in commandLineRoots ?? [])
        {
            Add(raw);
        }
        foreach (string raw in (Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Add(raw);
        }
        foreach (string line in readFile(Path.Combine(dataRootDirectory, ListFileName)) ?? [])
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                Add(trimmed);
            }
        }
        return roots;

        void Add(string raw)
        {
            if (Normalize(raw) is { } path && seen.Add(path))
            {
                roots.Add(path);
            }
        }
    }

    private static string? Normalize(string raw)
    {
        string expanded = Environment.ExpandEnvironmentVariables(raw).Trim().Trim('"');
        if (expanded.Length == 0)
        {
            return null;
        }
        if (expanded is "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[1..].TrimStart('/', '\\'));
        }
        try
        {
            // 去掉结尾分隔符再比:同一个目录写成 "C:\a\b" 与 "C:\a\b\" 都常见,
            // 不归一化的话会被当成两条,同一个插件于是被发现两次而互相判重。
            string full = Path.GetFullPath(expanded);
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 || Path.GetPathRoot(full) == full ? full : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // 写坏的一行不该让宿主起不来。
        }
    }

    private static string[]? ReadLinesOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
