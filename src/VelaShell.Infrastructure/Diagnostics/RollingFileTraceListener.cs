using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 按天滚动的文件 <see cref="TraceListener" />:把全仓已有的 <c>Trace.WriteLine</c> 原样落盘。
/// </summary>
/// <remarks>
/// <para>
/// 不引入 Serilog / NLog:全仓 65 处诊断输出已经是 <c>Trace.WriteLine</c>,挂一个监听器
/// 零改动就全部落盘;插件宿主也已经把同一个 <c>logs</c> 目录当作诊断目录,排障时一处找齐。
/// </para>
/// <para>
/// <b>任何情况下都不抛异常</b>:<see cref="TraceListener" /> 抛出会连带把
/// <c>Trace.WriteLine</c> 的调用方一起炸掉 —— 而调用方往往正在处理另一个异常。
/// 磁盘满、目录只读、文件被占,统统吞掉:日志写不进去是小事,把应用带崩不是。
/// </para>
/// </remarks>
public sealed class RollingFileTraceListener : TraceListener
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly Lock _gate = new();
    private readonly StringBuilder _pending = new();
    private DateTime _currentDay = DateTime.MinValue;
    private string? _currentPath;

    /// <summary>创建监听器。</summary>
    /// <param name="directory">日志目录(须已存在)。</param>
    /// <param name="prefix">文件名前缀,最终文件名为 <c>{prefix}yyyyMMdd.log</c>。</param>
    public RollingFileTraceListener(string directory, string prefix)
    {
        _directory = directory;
        _prefix = prefix;
    }

    /// <summary>当前正在写入的文件路径(尚未写过任何一行时为 null)。</summary>
    public string? CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _currentPath;
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? message) => Append(message, newLine: false);

    /// <inheritdoc />
    public override void WriteLine(string? message) => Append(message, newLine: true);

    private void Append(string? message, bool newLine)
    {
        if (message is null)
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                // 一行一个时间戳。Write(不换行)只攒着,等到 WriteLine 才成行落盘 ——
                // 否则 Trace.Write 的分段输出会被拆成一堆各带时间戳的碎行。
                _pending.Append(message);
                if (!newLine)
                {
                    return;
                }
                string line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {_pending}");
                _pending.Clear();
                File.AppendAllText(ResolvePath(), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 见类型注释:日志写失败绝不能影响调用方。
        }
    }

    /// <summary>按当天日期解析目标文件,跨天自动换文件。</summary>
    private string ResolvePath()
    {
        DateTime today = DateTime.Now.Date;
        if (_currentPath is null || today != _currentDay)
        {
            _currentDay = today;
            _currentPath = Path.Combine(
                _directory,
                string.Create(CultureInfo.InvariantCulture, $"{_prefix}{today:yyyyMMdd}.log"));
        }
        return _currentPath;
    }
}
