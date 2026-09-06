using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 应用级诊断日志:把 <c>Trace</c> 落盘到 <c>~/.velashell/logs</c>,并单独记录未处理异常。
/// </summary>
/// <remarks>
/// 之前 Release 构建下没有任何 <c>Trace</c> 监听器,用户反馈"闪退"时一点可取证的东西都没有。
/// 这里挂一个按天滚动的文件监听器,再把未处理异常单独写成 <c>crash-*.txt</c>,
/// 下次启动由消息中心提示(不弹窗 —— 启动弹窗很烦)。
/// <para>全部方法都不抛异常:诊断设施自己把应用带崩是最糟糕的结局。</para>
/// </remarks>
public static class DiagnosticLog
{
    private const string LogPrefix = "velashell-";
    private const string CrashPrefix = "crash-";
    private const string SeenMarkerName = "crash.seen";

    private static RollingFileTraceListener? _listener;

    /// <summary>日志目录;尚未初始化时为 null。</summary>
    public static string? Directory { get; private set; }

    /// <summary>
    /// 初始化诊断日志:建目录、挂 <c>Trace</c> 监听器、写一行启动横幅,并在后台清理过期日志。
    /// 重复调用只有第一次生效。
    /// </summary>
    /// <param name="logsDirectory">日志目录(<c>VelaShellStoragePaths.LogsDirectory</c>)。</param>
    /// <param name="retainDays">日志保留天数,超过即删。</param>
    public static void Initialize(string logsDirectory, int retainDays = 7)
    {
        if (_listener is not null || string.IsNullOrWhiteSpace(logsDirectory))
        {
            return;
        }
        try
        {
            System.IO.Directory.CreateDirectory(logsDirectory);
            Directory = logsDirectory;
            _listener = new(logsDirectory, LogPrefix);
            Trace.Listeners.Add(_listener);
            Trace.AutoFlush = true;
            Trace.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"[Startup] VelaShell {Version()} pid={Environment.ProcessId} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture}"));

            // 清理是目录枚举 + 删除,属于 IO:放后台,不占启动路径。
            _ = Task.Run(() => Prune(logsDirectory, retainDays));
        }
        catch
        {
            // 目录建不出来(只读介质 / 受限环境)就当没有日志,绝不能挡住启动。
            Directory = null;
            _listener = null;
        }
    }

    /// <summary>
    /// 写一份崩溃记录。文件名带秒级时间戳,内容是类别 + 时间 + 异常全文。
    /// </summary>
    /// <param name="kind">崩溃类别(如 <c>UnhandledException</c>)。</param>
    /// <param name="detail">异常对象或其它可 <c>ToString</c> 的现场信息。</param>
    /// <returns>写出的文件路径;未初始化或写失败时为 null。</returns>
    public static string? WriteCrash(string kind, object? detail)
    {
        if (Directory is not { } directory)
        {
            return null;
        }
        try
        {
            string path = Path.Combine(
                directory,
                string.Create(CultureInfo.InvariantCulture, $"{CrashPrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"));
            File.WriteAllText(
                path,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{kind}{Environment.NewLine}{DateTime.Now:O}{Environment.NewLine}VelaShell {Version()}{Environment.NewLine}{Environment.NewLine}{detail}{Environment.NewLine}"));
            Trace.WriteLine($"[Crash] {kind}: written to {path}");
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 取出一份"上次运行留下、且还没提示过"的崩溃记录;取走即标记为已看,不会重复提示。
    /// </summary>
    /// <param name="path">最新的未提示崩溃文件路径。</param>
    /// <returns>存在未提示的崩溃记录时为 true。</returns>
    public static bool TryTakeUnseenCrash(out string path)
    {
        path = "";
        if (Directory is not { } directory)
        {
            return false;
        }
        try
        {
            string marker = Path.Combine(directory, SeenMarkerName);
            DateTime seenUpTo = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;

            FileInfo? newest = null;
            foreach (string file in System.IO.Directory.EnumerateFiles(directory, CrashPrefix + "*.txt"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > seenUpTo && (newest is null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc))
                {
                    newest = info;
                }
            }
            if (newest is null)
            {
                return false;
            }
            // 标记文件的写入时间就是"看到哪儿了"的水位线。
            File.WriteAllText(marker, newest.Name);
            path = newest.FullName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用系统文件管理器打开日志目录。目录不存在或平台不支持时静默返回 false。</summary>
    /// <returns>是否成功发起打开。</returns>
    public static bool OpenLogsDirectory()
    {
        if (Directory is not { } directory || !System.IO.Directory.Exists(directory))
        {
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DiagnosticLog] 打开日志目录失败:{ex.Message}");
            return false;
        }
    }

    /// <summary>删除超过保留期的日志与崩溃文件。</summary>
    internal static void Prune(string directory, int retainDays)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retainDays));
            foreach (string file in System.IO.Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);
                bool ours = name.StartsWith(LogPrefix, StringComparison.Ordinal)
                            || name.StartsWith(CrashPrefix, StringComparison.Ordinal);
                if (ours && File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败无关紧要:下次启动再试。
        }
    }

    private static string Version() =>
        typeof(DiagnosticLog).Assembly.GetName().Version?.ToString() ?? "unknown";
}
