using VelaShell.PluginSdk;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 盯着开发期插件目录,重新构建之后自动触发一次重载。
/// </summary>
/// <remarks>
/// <para>
/// 从 <see cref="PluginManager" /> 拆出来的一簇(Q-01):文件监视器、去抖定时器、
/// "哪些文件变化才算数"的过滤 —— 三件事彼此紧密,与插件的装载/激活毫不相干。
/// </para>
/// <para>
/// 本类型只回答"该重载了",<b>不决定重载谁</b> —— 那要看每个插件入口程序集的时间戳,
/// 是管理器的事。
/// </para>
/// </remarks>
public sealed class PluginDevWatcher : IDisposable
{
    /// <summary>
    /// 最后一次变更之后静默多久才动手。
    /// </summary>
    /// <remarks>
    /// 一次 <c>dotnet build</c> 会连着写十几个文件,每个都触发一次重载纯属自找麻烦;
    /// 1.5 秒也给链接器写完 pdb 留出余地。
    /// </remarks>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(1500);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action _onRebuilt;
    private readonly Action<string> _log;
    private Timer? _debounce;
    private bool _stopped;

    /// <summary>构造。</summary>
    /// <param name="onRebuilt">检测到重新构建时触发(已去抖)。</param>
    /// <param name="log">日志输出。</param>
    public PluginDevWatcher(Action onRebuilt, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(onRebuilt);
        ArgumentNullException.ThrowIfNull(log);
        _onRebuilt = onRebuilt;
        _log = log;
    }

    /// <summary>
    /// 这个路径的变化值不值得触发一次重载。
    /// </summary>
    /// <remarks>
    /// 只认入口程序集(<c>.dll</c>)与清单。一次构建会在 obj/bin 里翻出成百个中间文件
    /// (<c>.pdb</c>、<c>.cache</c>、<c>.txt</c>、临时文件),全都放行的话去抖窗口
    /// 会被无关变更一直顶着、永远等不到静默。
    /// </remarks>
    /// <param name="fullPath">变化的文件路径。</param>
    /// <returns>该触发时为 true。</returns>
    public static bool ShouldReactTo(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }
        string name = Path.GetFileName(fullPath);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || name.Equals(PluginManifestReader.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>开始监视给定的开发期插件根目录(不存在的跳过)。</summary>
    /// <param name="roots">开发期插件根目录。</param>
    public void Start(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        // 去抖定时器在挂监视器之前建好:留到事件回调里懒建的话,两个几乎同时到达的
        // 变更事件会各建一个,其中一个从此没人 Dispose。
        _debounce = new(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += (_, e) => _log($"Development watcher on '{root}' failed: {e.GetException().Message}");
                _watchers.Add(watcher);
                _log($"Watching development plugin root '{root}' for rebuilds.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log($"Could not watch development plugin root '{root}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 手动记一次变更(供测试驱动去抖,不必真的动文件系统)。
    /// </summary>
    /// <param name="fullPath">变化的文件路径。</param>
    /// <returns>该变更被接受(会在去抖之后触发)时为 true。</returns>
    internal bool Notify(string fullPath)
    {
        if (_stopped || !ShouldReactTo(fullPath))
        {
            return false;
        }
        try
        {
            _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 正在停机。
            return false;
        }
        return true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Notify(e.FullPath);

    private void Fire()
    {
        if (_stopped)
        {
            return;
        }
        _onRebuilt();
    }

    /// <summary>停掉全部监视器与去抖定时器。幂等。</summary>
    public void Dispose()
    {
        _stopped = true;
        foreach (FileSystemWatcher watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 停机路径:尽力而为。
            }
        }
        _watchers.Clear();
        _debounce?.Dispose();
        _debounce = null;
    }
}
