using System.Diagnostics;

namespace VelaShell.Services;

/// <summary>
/// 命令补全链路的临时诊断日志:设 VELASHELL_SUGGEST_DIAG=1 时把各环节写到
/// %TEMP%\velashell-suggest.log(定位"弹层不弹"这类只在真机出现的问题)。
/// 默认完全关闭,零开销。
/// </summary>
public static class SuggestDiag
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "velashell-suggest.log");
    private static readonly Lock Gate = new();

    /// <summary>
    /// 诊断开关是否开启。热路径(每键)调用方必须先查它再拼 Log 的实参——
    /// C# 会急切求值实参,不设防的话关着开关每键也白白分配几个字符串。
    /// </summary>
    public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable("VELASHELL_SUGGEST_DIAG") == "1";

    /// <summary>在诊断开关开启时,把补全链路某个环节的详情写入日志文件与调试输出;关闭时零开销。</summary>
    public static void Log(string stage, string detail)
    {
        if (IsEnabled)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{stage}] {detail}{Environment.NewLine}");
                }
            }
            catch
            {
                // 诊断日志绝不影响主流程。
            }
            Debug.WriteLine($"[suggest:{stage}] {detail}");
        }
    }
}
