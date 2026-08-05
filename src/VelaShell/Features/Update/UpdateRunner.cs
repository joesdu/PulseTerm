using System.Diagnostics;

namespace VelaShell.Features.Update;

/// <summary>
/// 外置换版进程的入口。跑这段代码的不是安装好的应用,而是更新包里那个新版主程序的一份
/// 临时副本(见 <see cref="UpdateApplier.TryHandOffToExternalUpdater" />)——
/// Release 是自包含单文件发布,主程序自己就是一个完整可执行体,因此不必随包分发额外的
/// 更新器,也就没有"更新器自己怎么更新""小体积未签名 exe 被杀软当木马"这些麻烦。
/// <para>
/// 它做的事很少:等主进程退出 → 换版 → 拉起应用目录里的主程序 → 自己退出。
/// 换版发生在应用完全退出之后,应用目录里没有任何文件被占用,移动/覆盖必然成功,
/// 从根上消除了"旧文件删不掉"这一类残留。
/// </para>
/// <para>
/// <b>约束:这条路径绝不能触碰 Avalonia / Skia / SQLite 等本机依赖。</b>单文件发布只把托管
/// 程序集打进 exe,本机动态库仍散落在应用目录里,而这份副本待在系统临时目录中,身边什么都没有。
/// 因此本类只用 BCL,且必须在 <c>Program.Main</c> 最顶端、任何 Avalonia 初始化之前被调用。
/// </para>
/// </summary>
internal static class UpdateRunner
{
    /// <summary>触发外置换版模式的命令行开关。</summary>
    internal const string ApplyUpdateSwitch = "--apply-update";

    /// <summary>目标应用目录的命令行开关(其后紧跟目录路径)。</summary>
    internal const string TargetSwitch = "--target";

    /// <summary>待等待的主进程 PID 的命令行开关(其后紧跟 PID)。</summary>
    internal const string WaitPidSwitch = "--wait-pid";

    /// <summary>等待主进程退出的上限;超时即放弃换版,应用目录保持原样。</summary>
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>主进程退出后再等目标文件解除占用的上限(杀软扫描、句柄延迟释放)。</summary>
    private static readonly TimeSpan UnlockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 若命令行要求外置换版则执行之并返回 true(调用方随即结束进程),否则返回 false 让正常启动继续。
    /// 绝不抛出异常:换版失败已由 <see cref="UpdateApplier.SwapFromPayload" /> 回滚,
    /// 失败原因落盘后仍会把应用拉起来,用户至少还能用着旧版本。
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (!args.Contains(ApplyUpdateSwitch, StringComparer.Ordinal))
        {
            return false;
        }
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] External updater crashed: {ex}");
        }
        return true;
    }

    private static void Run(string[] args)
    {
        if (ValueOf(args, TargetSwitch) is not { } target || !Directory.Exists(target))
        {
            Trace.WriteLine("[VelaShell] External updater: missing or invalid --target, nothing to do.");
            return;
        }
        UpdateApplier applier = new(target);
        string? launcherName = Path.GetFileName(Environment.ProcessPath);
        string launcher = string.IsNullOrEmpty(launcherName) ? string.Empty : Path.Combine(target, launcherName);

        if (int.TryParse(ValueOf(args, WaitPidSwitch), out int pid) && pid > 0 && !WaitForProcessExit(pid))
        {
            // 主进程没退(用户取消了关闭、或卡在关闭流程里)。什么都别动:应用目录还是完整的,
            // 那个进程仍以旧版本正常运行,解包内容留待它下次启动时清理。
            Trace.WriteLine($"[VelaShell] External updater: process {pid} still alive after timeout, aborting swap.");
            return;
        }
        // Windows 上进程退出与映像句柄释放之间有窗口期(杀软扫描尤其明显),等它真正可写再动手。
        WaitForWritable(launcher);

        try
        {
            applier.SwapFromPayload();
            applier.ClearLastError();
        }
        catch (Exception ex)
        {
            // SwapFromPayload 内部已回滚到旧版本,这里只把原因留给应用去提示用户。
            Trace.WriteLine($"[VelaShell] External updater: swap failed: {ex}");
            applier.WriteLastError(ex.Message);
        }
        Relaunch(launcher);
    }

    /// <summary>取 <paramref name="name" /> 开关后紧跟的那个值;没有则返回 null。</summary>
    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>等指定进程退出;已经不存在视为已退出。超时返回 false。</summary>
    private static bool WaitForProcessExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit((int)ParentExitTimeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // 进程已经没了。
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] External updater: cannot wait on process {pid}: {ex.Message}");
            return true; // 拿不到句柄就别卡着,后面的可写探测会兜住。
        }
    }

    /// <summary>
    /// 轮询目标文件直到能以独占方式打开(即无人占用)。超时也照常继续:换版会自行失败并回滚,
    /// 强过在这里干等着不给用户任何结果。
    /// </summary>
    private static void WaitForWritable(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }
        DateTime deadline = DateTime.UtcNow + UnlockTimeout;
        int delayMs = 50;
        while (true)
        {
            try
            {
                using FileStream probe = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Trace.WriteLine($"[VelaShell] External updater: {path} still locked after timeout, proceeding anyway.");
                    return;
                }
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 1000);
            }
            catch
            {
                return; // 权限之类的问题不是"被占用",交给换版本身去报错。
            }
        }
    }

    /// <summary>把应用目录里的主程序拉起来(带 --after-update,它会等本进程让出单实例锁)。</summary>
    private static void Relaunch(string launcher)
    {
        if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher))
        {
            Trace.WriteLine("[VelaShell] External updater: launcher missing, cannot relaunch.");
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // zip 包不带权限位,tar 包解出来虽已带,这里再兜一道底保证主程序可执行。
                File.SetUnixFileMode(launcher, File.GetUnixFileMode(launcher)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            Process.Start(new ProcessStartInfo(launcher)
            {
                WorkingDirectory = Path.GetDirectoryName(launcher)!,
                UseShellExecute = false,
                ArgumentList = { "--after-update" }
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] External updater: relaunch failed: {ex}");
        }
    }
}
