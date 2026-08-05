using System.Text;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 传输日志(设置 → 文件传输 → 日志记录):每天一个 transfer-yyyyMMdd.log,
/// 一行一条(时间 / 方向 / 本地路径 / 远程路径 / 结果)。写入失败静默。
/// </summary>
public static class TransferLogService
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// 解析配置的日志目录("~" 与相对路径均以用户目录为基准,见 <see cref="UserPathResolver" />);
    /// 空则退回默认 %LocalAppData%\VelaShell\logs。
    /// </summary>
    private static string ResolveDirectory(string? configured) =>
        UserPathResolver.Resolve(configured, SessionLogService.LogDirectory);

    /// <summary>向当天的 transfer 日志追加一条传输记录(写入失败静默忽略)。</summary>
    public static void Append(string? configuredDirectory, TransferType type, string localPath, string remotePath, TransferStatus status)
    {
        try
        {
            string dir = ResolveDirectory(configuredDirectory);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd}.log");
            string line = string.Join('\t',
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                              type == TransferType.Upload ? "UPLOAD" : "DOWNLOAD",
                              localPath,
                              remotePath,
                              status.ToString().ToUpperInvariant()) +
                          Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响传输本身。
        }
    }

    /// <summary>删除超过保留天数的 transfer-*.log(启动时后台执行)。</summary>
    public static void CleanupExpired(string? configuredDirectory, int retentionDays)
    {
        if (retentionDays < 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                string dir = ResolveDirectory(configuredDirectory);
                if (!Directory.Exists(dir))
                {
                    return;
                }
                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string file in Directory.EnumerateFiles(dir, "transfer-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // 单个文件占用/无权限,跳过。
                    }
                }
            }
            catch
            {
                // 清理失败不影响启动。
            }
        });
    }
}
