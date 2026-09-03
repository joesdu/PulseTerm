using System.Diagnostics;
using System.Runtime.InteropServices;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 把 <c>MainWindowViewModel.WorkingDirectoryReportHook</c> 原样交给<b>真正的 bash</b> 跑一遍。
/// </summary>
/// <remarks>
/// <para>
/// 这段钩子是 shell 语义,C# 只断言得了"字符串里有没有某几个词"
/// (<see cref="MainWindowSshFeatureTests" /> 就是那么做的),拦不住"跑起来才炸" ——
/// 而它已经炸过两次,两次都是这类:
/// </para>
/// <list type="bullet">
/// <item>原值结尾带分号 → 拼出 <c>;;</c> → 用户每敲一次回车都看到一行语法错误;</item>
/// <item>会话已经是坏的 → 旧的"装过就跳过"判定认为无事可做 → 永远修不回来。</item>
/// </list>
/// <para>
/// 所以这里逐个状态真跑:装完的 <c>PROMPT_COMMAND</c> 必须<b>等于</b>期望值,并且必须
/// <b>真的能执行</b>(bash 拿它当命令跑一遍,语法错就算失败)。每个状态连跑三遍,顺带验幂等 ——
/// 重连、多开标签都会把同一段再注入一次。
/// </para>
/// <para>
/// 找不到 bash 就报 Inconclusive:Windows 开发机上 bash 来自 Git for Windows,不是人人都装。
/// Linux/macOS 上它一定在,CI 与真机验证不会被绕过去。
/// </para>
/// </remarks>
[TestClass]
public sealed class PromptHookShellTests
{
    /// <summary>钩子里那个函数名;摘装与断言都围着它转。</summary>
    private const string HookFunction = "vela_shell_osc7";

    private static string? _bash;

    [ClassInitialize]
    public static void Init(TestContext _) => _bash = FindBash();

    /// <summary>
    /// 每个用例:进来时的 <c>PROMPT_COMMAND</c>(null = 完全未设置)→ 装完之后应该是什么。
    /// </summary>
    private static IEnumerable<object?[]> Cases =>
    [
        // 完全没有别的钩子。
        [null, HookFunction, "未设置"],
        [string.Empty, HookFunction, "空串"],

        // pyenv-virtualenv 在 PROMPT_COMMAND 原本为空时就是设成带尾分号的 ——
        // 正是它让旧版拼出 `_pyenv_virtualenv_hook;;vela_shell_osc7`(真机报的那个)。
        ["_pyenv_virtualenv_hook;", $"_pyenv_virtualenv_hook;{HookFunction}", "尾部带分号"],
        ["_pyenv_virtualenv_hook; ", $"_pyenv_virtualenv_hook;{HookFunction}", "尾部分号加空格"],
        ["_pyenv_virtualenv_hook", $"_pyenv_virtualenv_hook;{HookFunction}", "尾部干净"],
        ["a;b", $"a;b;{HookFunction}", "两个已有钩子"],

        // 已经被旧版毒过的会话:再连一次必须自愈,而不是"看到装过了就跳过"。
        [$"_pyenv_virtualenv_hook;;{HookFunction}", $"_pyenv_virtualenv_hook;{HookFunction}", "已中招"],
        [$"_pyenv_virtualenv_hook; ;{HookFunction}", $"_pyenv_virtualenv_hook;{HookFunction}", "已中招(带空格)"],

        // 已经装好的各种写法:结果不变,也不许重复追加。
        [$"a;{HookFunction}", $"a;{HookFunction}", "已装好"],
        [$"a; {HookFunction}", $"a;{HookFunction}", "已装好(分号后带空格)"],
        [HookFunction, HookFunction, "只有自己"],

        // 自己不在末尾:摘掉时不能在中间留下 `;;`,也不能留下开头的分号。
        [$"{HookFunction};a", $"a;{HookFunction}", "自己在开头"],
        [$"a;{HookFunction};b", $"a;b;{HookFunction}", "自己夹在中间"],

        // 用户 PROMPT_COMMAND 里合法的 case 分支带 `;;` —— 一个字符都不许动。
        [
            "case $TERM in xterm*) :;; *) :;; esac",
            $"case $TERM in xterm*) :;; *) :;; esac;{HookFunction}",
            "用户自带 case"
        ]
    ];

    /// <summary>找一个能用的 bash:Unix 上就是自带的那份,Windows 上从 PATH 里挑 Git 带的那份。</summary>
    /// <remarks>
    /// Windows 上要<b>跳过</b> <c>%SystemRoot%\System32\bash.exe</c> 与 <c>WindowsApps\bash.exe</c> ——
    /// 那两个是 WSL 的入口,不是 Git for Windows 的 bash。用它跑脚本会掉进 WSL 的文件系统视图,
    /// 临时脚本的路径(<c>/c/Users/…</c>)在那边根本不存在(WSL 是 <c>/mnt/c/…</c>),
    /// 表现是一句莫名其妙的 "No such file or directory"。Git 也不一定装在 Program Files
    /// (本机就在 D:\Git),所以按 PATH 找而不是猜几个固定路径。
    /// </remarks>
    private static string? FindBash()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return File.Exists("/bin/bash") ? "/bin/bash" : null;
        }
        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0
                || dir.StartsWith(system, StringComparison.OrdinalIgnoreCase)
                || dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string candidate;
            try
            {
                candidate = Path.Combine(dir, "bash.exe");
            }
            catch (ArgumentException)
            {
                // PATH 里混进了带非法字符的项,跳过就是。
                continue;
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 建一段脚本:摆好初始状态 → 把钩子原样跑三遍 → 打印结果并试着真执行一次。
    /// </summary>
    /// <remarks>
    /// 钩子那三行整个重定向到 /dev/null:它末尾那记清行(<c>printf "\r\033[2K"</c>)会往
    /// stdout 写控制字符,混进来就没法比对了。跑完把 <c>vela_shell_osc7</c> 覆盖成空操作,
    /// 免得末尾那次试执行真的吐一串 OSC 7 出来。
    /// </remarks>
    private static string BuildScript(string? initial)
    {
        string hook = MainWindowViewModel.WorkingDirectoryReportHook;
        // 刻意用逐行拼接而不是内插原始字符串:脚本里大括号很多(函数体、命令组),
        // 内插会逼着把每一个都写成 {{ }},读起来比脚本本身还难。
        string[] lines =
        [
            "set -u",
            "_pyenv_virtualenv_hook() { :; }",
            "a() { :; }",
            "b() { :; }",
            initial is null ? "unset PROMPT_COMMAND" : "PROMPT_COMMAND=" + Quote(initial),
            "{",
            hook,
            hook,
            hook,
            "} >/dev/null 2>&1",
            HookFunction + "() { :; }",
            "printf 'RESULT<%s>\\n' \"$PROMPT_COMMAND\"",
            "if ( eval \"$PROMPT_COMMAND\" ) >/dev/null 2>&1; then printf 'RUNS<yes>\\n'; else printf 'RUNS<no>\\n'; fi"
        ];
        return string.Join('\n', lines);
    }

    /// <summary>包成 shell 单引号字面量(内部的单引号按 <c>'\''</c> 拆开重接)。</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static (string PromptCommand, bool Runs) RunHook(string? initial)
    {
        string script = Path.Combine(Path.GetTempPath(), $"vela-prompt-hook-{Guid.NewGuid():N}.sh");
        // bash 只认 LF;Windows 上写出 CRLF 会得到 "$'\r': command not found"。
        File.WriteAllText(script, BuildScript(initial).ReplaceLineEndings("\n"));
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = _bash!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Windows 的 bash 不认盘符路径,转成 /c/... 形式。
            psi.ArgumentList.Add(ToBashPath(script));

            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            Assert.IsTrue(process.WaitForExit(30_000), "bash 没有在 30 秒内退出");
            Assert.IsEmpty(stderr.Trim(), $"钩子本身向 stderr 写了东西:{stderr}");

            return (Extract(stdout, "RESULT"), Extract(stdout, "RUNS") == "yes");
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static string ToBashPath(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;

    private static string Extract(string output, string tag)
    {
        int start = output.IndexOf(tag + "<", StringComparison.Ordinal);
        // 打不出标记通常不是"输出错了",是脚本半路就退出了 —— 钩子那三行的 stderr 被丢进
        // /dev/null(不然它自己的噪音会混进来比对),所以这里得替它把最可能的原因说出来。
        Assert.IsGreaterThanOrEqualTo(
            0,
            start,
            $"bash 输出里没有 {tag}<…>,脚本多半中途退出了(常见原因:set -u 撞上未定义的变量)。实际输出:{output}");
        start += tag.Length + 1;
        int end = output.IndexOf('>', start);
        Assert.IsGreaterThanOrEqualTo(0, end, $"bash 输出里的 {tag}<…> 没有闭合:{output}");
        return output[start..end];
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Hook_LeavesPromptCommandUsable(string? initial, string expected, string description)
    {
        if (_bash is null)
        {
            Assert.Inconclusive("本机没有 bash(Windows 上需要 Git for Windows),跳过 shell 语义验证。");
            return;
        }

        (string promptCommand, bool runs) = RunHook(initial);

        Assert.AreEqual(expected, promptCommand, $"{description}:装完的 PROMPT_COMMAND 不对");
        Assert.IsTrue(runs, $"{description}:PROMPT_COMMAND 执行不了(多半是拼出了 ;;)—— [{promptCommand}]");
    }

    /// <summary>
    /// 守卫为假时(非 bash),除了那记清行不许有任何输出 —— 用户不该在屏幕上看见这段脚本。
    /// </summary>
    /// <remarks>
    /// 这里靠置空 <c>BASH_VERSION</c> 模拟:真正的 fish/csh 是在**解析阶段**就炸,
    /// 而那正是钩子把 bash 代码包进单引号 eval 的原因 —— 那条路本机无法复现,
    /// 这条用例守的是"守卫为假 = 不产生副作用"这一半。
    /// </remarks>
    [TestMethod]
    public void Hook_WhenNotBash_ProducesNothingButTheLineClear()
    {
        if (_bash is null)
        {
            Assert.Inconclusive("本机没有 bash,跳过。");
            return;
        }

        string script = Path.Combine(Path.GetTempPath(), $"vela-prompt-hook-{Guid.NewGuid():N}.sh");
        File.WriteAllText(
            script,
            $"BASH_VERSION=''\n{MainWindowViewModel.WorkingDirectoryReportHook}\nprintf 'AFTER<%s>\\n' \"${{PROMPT_COMMAND:-}}\"\n"
                .ReplaceLineEndings("\n"));
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = _bash!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(ToBashPath(script));
            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            Assert.IsEmpty(Extract(stdout, "AFTER"), "守卫为假却动了 PROMPT_COMMAND");
            // 清行之外一个可见字符都不许有。
            Assert.DoesNotContain(HookFunction, stdout, "守卫为假却把脚本内容回显了出来");
            Assert.StartsWith("\r[2K", stdout, "末尾那记清行不见了 —— 注入的那一行会留在屏幕上");
        }
        finally
        {
            File.Delete(script);
        }
    }
}
