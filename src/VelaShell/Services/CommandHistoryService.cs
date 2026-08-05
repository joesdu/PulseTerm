using VelaShell.Core.Data;

namespace VelaShell.Services;

/// <summary>
/// 本地命令历史(命令补全建议的主数据源,plan.md #16):MRU 序、去重、封顶 500 条,
/// 经 <see cref="IAppDataStore" /> 持久化。仅记录经调用方回显校验过的提交
/// (密码等无回显输入不会到达这里)。所有成员都在 UI 线程访问。
/// </summary>
public sealed class CommandHistoryService(IAppDataStore? dataStore)
{
    private const string Collection = "command_history";
    private const string DocumentId = "history";
    private const int MaxEntries = 500;
    private const int MaxCommandLength = 500;

    private readonly List<string> _entries = [];

    // 落盘防抖:命令密集执行(脚本粘贴、快速连敲)时,每条都全量序列化 500 条历史
    // 纯属浪费;合并成静默 1 秒后一次落盘。窗口极小,丢失面可忽略(仅进程被杀时的最后一秒)。
    private bool _savePending;

    /// <summary>最近优先的历史快照。</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>从持久化存储加载命令历史(封顶上限条数);读取失败则从空开始,不影响主流程。</summary>
    public async Task LoadAsync()
    {
        if (dataStore is null)
        {
            return;
        }
        try
        {
            CommandHistoryData? data = await dataStore.GetAsync<CommandHistoryData>(Collection, DocumentId);
            if (data?.Commands is { Count: > 0 } commands)
            {
                _entries.Clear();
                _entries.AddRange(commands.Take(MaxEntries));
            }
        }
        catch
        {
            // 历史读不出来就从空开始,不影响主流程。
        }
    }

    /// <summary>记录一次已执行的命令(MRU 去重;过短/过长的忽略)。</summary>
    public void Record(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.Length is < 2 or > MaxCommandLength)
        {
            return;
        }
        _entries.Remove(trimmed);
        _entries.Insert(0, trimmed);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        ScheduleSave();
    }

    /// <summary>清空全部历史(设置页可挂此入口)。用户显式操作,立即落盘不防抖。</summary>
    public void Clear()
    {
        _entries.Clear();
        _ = SaveAsync();
    }

    private void ScheduleSave()
    {
        if (_savePending)
        {
            return; // 已有待落盘的防抖任务,本次改动搭它的便车。
        }
        _savePending = true;
        _ = FlushAfterQuietPeriodAsync();
    }

    private async Task FlushAfterQuietPeriodAsync()
    {
        // 无 ConfigureAwait(false):延迟结束后回到 UI 线程再快照 _entries(本类的线程契约)。
        await Task.Delay(TimeSpan.FromSeconds(1));
        _savePending = false;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (dataStore is null)
        {
            return;
        }
        try
        {
            await dataStore.UpsertAsync(Collection, DocumentId, new CommandHistoryData { Commands = [.. _entries] });
        }
        catch
        {
            // 历史落盘尽力而为。
        }
    }

    /// <summary>命令历史的持久化载体。</summary>
    public sealed class CommandHistoryData
    {
        /// <summary>按最近优先顺序持久化的命令列表。</summary>
        public List<string> Commands { get; set; } = [];
    }
}
