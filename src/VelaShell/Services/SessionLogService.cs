namespace VelaShell.Services;

/// <summary>
/// 会话日志(设置 → 常规 → 数据与存储):开启后把每个会话的原始终端输出追加写入
/// %LocalAppData%\VelaShell\logs\session-*.log(含 ANSI 序列,同 script(1) 的产物);
/// 启动时按“日志保留天数”清理过期文件。
/// </summary>
public static class SessionLogService
{
    /// <summary>会话日志目录:%LocalAppData%\VelaShell\logs。</summary>
    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelaShell", "logs");

    /// <summary>为一个会话开启日志;返回 null 表示无法创建日志文件(不影响会话)。</summary>
    public static SessionLogWriter? CreateWriter(string sessionName)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string safeName = string.Concat(sessionName.Select(c =>
                char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
            if (safeName.Length > 40)
            {
                safeName = safeName[..40];
            }
            string path = Path.Combine(LogDirectory,
                $"session-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            return new(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>删除超过保留天数的 session-*.log(启动时后台执行,失败静默)。</summary>
    public static void CleanupExpired(int retentionDays)
    {
        if (retentionDays < 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    return;
                }
                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string file in Directory.EnumerateFiles(LogDirectory, "session-*.log"))
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

/// <summary>单个会话的追加式日志写入器;写入在调用线程(读线程)上串行,异常即自禁用。</summary>
public sealed class SessionLogWriter : IDisposable
{
    private readonly Lock _gate = new();
    private FileStream? _stream;

    /// <summary>以追加模式打开日志文件,准备写入会话原始输出。</summary>
    /// <param name="path">日志文件的完整路径。</param>
    internal SessionLogWriter(string path)
    {
        _stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    /// <summary>刷新并关闭底层日志文件流。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Flush();
            }
            catch
            {
                // ignore
            }
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>把一段原始终端字节追加写入日志;写入失败即自禁用,不影响会话。</summary>
    /// <param name="data">待写入的原始字节。</param>
    public void Write(byte[] data)
    {
        lock (_gate)
        {
            try
            {
                _stream?.Write(data, 0, data.Length);
            }
            catch
            {
                // 磁盘满/文件被删等:停止记录,不影响会话。
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
