namespace VelaShell.Infrastructure.Startup;

/// <summary>
/// 启动参数中与插件开发相关的那几项。它们都有等价的环境变量,但**参数优先** ——
/// 因为参数是跟着 IDE 启动配置走的、写在插件工程里,而环境变量是机器级的全局状态:
/// 同时开两个插件工程、或在两条分支间切换时,全局状态必然互相串味。
/// <list type="bullet">
///   <item><c>--dev-root &lt;dir&gt;</c>:开发期插件根(可重复,也可用路径分隔符串成一条);</item>
///   <item><c>--wait-debugger[=&lt;ids&gt;]</c>:隔离插件等待调试器附加(省略值等同 <c>*</c>);</item>
///   <item><c>--data-root &lt;dir&gt;</c>:数据根目录(连带切换单实例互斥键与数据库位置);</item>
///   <item><c>--dev-watch</c>:开发期插件目录变更后自动重载。</item>
/// </list>
/// <para>
/// 每项都接受 <c>--name value</c> 与 <c>--name=value</c> 两种写法;认不出的参数一律忽略
/// (Avalonia 还要从同一份 argv 里取它自己那些)。
/// </para>
/// </summary>
public sealed class VelaShellStartupArguments
{
    /// <summary>开发期插件根目录(命令行给出的,按出现顺序)。</summary>
    public IReadOnlyList<string> DevPluginRoots { get; private init; } = [];

    /// <summary>要等待调试器的插件 id(<c>*</c> 表示全部);未指定时为空。</summary>
    public IReadOnlyCollection<string> DebugPluginIds { get; private init; } = [];

    /// <summary>数据根目录覆盖;未指定为 <see langword="null" />(用 <c>~/.velashell</c>)。</summary>
    public string? DataRoot { get; private init; }

    /// <summary>是否监视开发期插件目录并自动重载。</summary>
    public bool DevWatch { get; private init; }

    /// <summary>本进程的启动参数。<c>Main</c> 在做任何路径解析之前赋值。</summary>
    public static VelaShellStartupArguments Current { get; set; } = new();

    /// <summary>解析 argv。认不出的参数忽略,写坏的值不会让启动失败。</summary>
    public static VelaShellStartupArguments Parse(IReadOnlyList<string>? args)
    {
        var devRoots = new List<string>();
        var debugIds = new List<string>();
        string? dataRoot = null;
        bool devWatch = false;
        bool waitDebuggerSeen = false;

        for (int i = 0; i < (args?.Count ?? 0); i++)
        {
            string arg = args![i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            string name = arg;
            string? inline = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                name = arg[..equals];
                inline = arg[(equals + 1)..];
            }
            switch (name)
            {
                case "--dev-root":
                    foreach (string root in Split(inline ?? TakeValue(args, ref i)))
                    {
                        devRoots.Add(root);
                    }
                    break;
                case "--wait-debugger":
                    waitDebuggerSeen = true;
                    debugIds.AddRange(SplitIds(inline ?? TakeValue(args, ref i)));
                    break;
                case "--data-root":
                    if ((inline ?? TakeValue(args, ref i)) is { Length: > 0 } root2)
                    {
                        dataRoot = root2.Trim().Trim('"');
                    }
                    break;
                case "--dev-watch":
                    devWatch = true;
                    break;
            }
        }
        // `--wait-debugger` 不带值 = 全部插件都等:这是最常用的写法(只挂了一个开发插件时),
        // 让它必须重复一遍插件 id 纯属添堵。
        if (waitDebuggerSeen && debugIds.Count == 0)
        {
            debugIds.Add("*");
        }
        return new()
        {
            DevPluginRoots = devRoots,
            DebugPluginIds = debugIds,
            DataRoot = dataRoot,
            DevWatch = devWatch
        };
    }

    /// <summary>取下一个参数作为值;下一个已是选项(或没有下一个)时返回 null 并保持位置不动。</summary>
    private static string? TakeValue(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }
        return args[++index];
    }

    /// <summary>把一条 <c>--dev-root</c> 值切成多条路径(允许用系统路径分隔符串写)。</summary>
    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(v => v.Trim('"'))
                     .Where(v => v.Length > 0);

    /// <summary>把一条 <c>--wait-debugger</c> 值切成插件 id 集合(逗号/分号分隔)。</summary>
    private static IEnumerable<string> SplitIds(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
