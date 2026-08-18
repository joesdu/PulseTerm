using System.Collections.ObjectModel;
using System.Globalization;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>控制台输出里的一行(带语义类别,界面按它上色)。</summary>
/// <param name="Text">整行文本。</param>
/// <param name="Kind">语义类别。</param>
public sealed record RedisConsoleLine(string Text, RedisReplyLineKind Kind)
{
    /// <summary>是否是用户敲的那一行(界面据此加提示符样式)。</summary>
    public bool IsCommand => Kind == RedisReplyLineKind.Command;

    /// <summary>是否是错误。</summary>
    public bool IsError => Kind == RedisReplyLineKind.Error;

    /// <summary>是否是插件自己的说明。</summary>
    public bool IsNote => Kind == RedisReplyLineKind.Note;

    /// <summary>是否是数值(整数或浮点)—— 界面用另一种颜色,与字符串区分开。</summary>
    public bool IsNumeric => Kind is RedisReplyLineKind.Integer or RedisReplyLineKind.Double;

    /// <summary>
    /// 是否是空值。<c>(nil)</c> 与 <c>(empty array)</c> 都算 —— 它们是"没有"这一类,
    /// 与"有一个空字符串"是不同的事实。
    /// </summary>
    public bool IsNil => Kind == RedisReplyLineKind.Nil;
}

/// <summary>
/// 内置控制台。
/// <para>
/// 重度用户永远会回到命令行 —— 客户端的价值是**降低往返摩擦**,不是取代它。
/// 所以控制台不是"高级功能",是底部抽屉的第一个页签。
/// </para>
/// <para>
/// 三件事在这里落地:提示符如实反映连接状态(库/只读/不可用命令)、
/// 补全数据来自服务端(<c>COMMAND DOCS</c>)、危险命令过闸门(确认框贴在本面板上)。
/// </para>
/// </summary>
public sealed class RedisConsoleViewModel : ObservableObject
{
    private readonly RedisConnection _connection;
    private readonly RedisConfirmation _confirmation;
    private readonly Loc _loc;
    private readonly RedisStore? _store;
    private readonly string _connectionKey;
    private readonly List<string> _history = [];
    private int _historyCursor = -1;

    /// <summary>输出行数上限。超出后从头丢弃 —— 一个永不截断的控制台迟早把内存吃光。</summary>
    private const int MaxLines = 2000;

    internal RedisConsoleViewModel(
        RedisConnection connection,
        RedisConfirmation confirmation,
        Loc loc,
        RedisStore? store = null,
        string connectionKey = "")
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _store = store;
        _connectionKey = connectionKey;
        RunCommand = new(RunAsync, () => Input.Trim().Length > 0 && !IsRunning);
        ClearCommand = new(() =>
        {
            Lines.Clear();
            return Task.CompletedTask;
        });
        Lines.Add(new(loc.Format("Redis_ConsoleWelcome", connection.Info.Version), RedisReplyLineKind.Note));
        if (!connection.Guard.MetadataFromServer)
        {
            // 元数据来自内置兜底表这件事必须说出来 —— 用户据此知道补全为什么少、
            // 以及"这条命令被判成写"的依据是什么。
            Lines.Add(new(loc["Redis_ConsoleFallbackMetadata"], RedisReplyLineKind.Note));
        }
    }

    /// <summary>输出行。</summary>
    public ObservableCollection<RedisConsoleLine> Lines { get; } = [];

    /// <summary>补全候选。</summary>
    public ObservableCollection<RedisCommandHint> Completions { get; } = [];

    /// <summary>
    /// 提示符。**如实反映连接状态** —— 只读模式下带一个标记,
    /// 否则用户会一直纳闷"我敲的写命令怎么都被拒"。
    /// </summary>
    public string Prompt =>
        _connection.Settings.SupportsDatabases
            ? _connection.Guard.ReadOnly
                ? $"{Endpoint}[db{_connection.Database}:ro]>"
                : $"{Endpoint}[db{_connection.Database}]>"
            : _connection.Guard.ReadOnly
                ? $"{Endpoint}[cluster:ro]>"
                : $"{Endpoint}[cluster]>";

    /// <summary>端点文本(提示符左半边)。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>输入行。</summary>
    public string Input
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RunCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(InputHint));
            RefreshCompletions();
        }
    } = string.Empty;

    /// <summary>
    /// 输入框下的一行提示:当前命令的档位与说明,或"这条命令在这条通道上跑不了"。
    /// **在敲下回车之前**就把结论给出来,而不是等他敲完再拒。
    /// </summary>
    public string InputHint
    {
        get
        {
            string command = RedisCommandGuard.Normalize(Input);
            if (command.Length == 0)
            {
                return string.Empty;
            }
            if (RedisConnection.IsUnsupportedOnThisTransport(command))
            {
                return _loc.Format("Redis_ConsoleUnsupported", command);
            }
            RedisCommandVerdict verdict = _connection.Guard.Evaluate(command);
            string summary = _connection.CommandHints
                .FirstOrDefault(hint => hint.Name == command)?.Summary ?? string.Empty;
            string risk = verdict switch
            {
                { Allowed: false, Reason: "readonly" } => _loc["Redis_ConsoleBlockedReadOnly"],
                { Allowed: false } => _loc["Redis_ConsoleBlockedProduction"],
                { Risk: RedisCommandRisk.Destructive } => _loc["Redis_RiskDestructive"],
                { Risk: RedisCommandRisk.Dangerous } => _loc["Redis_RiskDangerous"],
                { Risk: RedisCommandRisk.Write } => _loc["Redis_RiskWrite"],
                _ => string.Empty
            };
            return string.IsNullOrEmpty(summary) ? risk : $"{risk}  {summary}".Trim();
        }
    }

    /// <summary>是否正在执行。</summary>
    public bool IsRunning
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>最近一次执行耗时的显示文本。</summary>
    public string LastElapsed
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>执行。</summary>
    public AsyncCommand RunCommand { get; }

    /// <summary>清屏。</summary>
    public AsyncCommand ClearCommand { get; }

    /// <summary>控制台里 <c>SELECT</c> 切库后触发,让浏览器跟上。</summary>
    public event Action<int>? DatabaseSelected;

    /// <summary>↑ 调历史。</summary>
    public void HistoryBack()
    {
        if (_history.Count == 0)
        {
            return;
        }
        _historyCursor = _historyCursor < 0 ? _history.Count - 1 : Math.Max(0, _historyCursor - 1);
        Input = _history[_historyCursor];
    }

    /// <summary>↓ 调历史(走到末尾即清空输入,与 shell 一致)。</summary>
    public void HistoryForward()
    {
        if (_history.Count == 0 || _historyCursor < 0)
        {
            return;
        }
        _historyCursor++;
        if (_historyCursor >= _history.Count)
        {
            _historyCursor = -1;
            Input = string.Empty;
            return;
        }
        Input = _history[_historyCursor];
    }

    /// <summary>
    /// 从时序库把历史读回来(面板首次加载时调一次)。
    /// <para>
    /// 读回来的放在**当前会话历史之前** —— ↑ 的语义是"往更早翻",顺序反了就全乱了。
    /// </para>
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task RestoreHistoryAsync()
    {
        if (_store is null)
        {
            return;
        }
        IReadOnlyList<string> saved = await _store.LoadHistoryAsync(_connectionKey).ConfigureAwait(true);
        if (saved.Count == 0)
        {
            return;
        }
        var merged = new List<string>(saved.Count + _history.Count);
        foreach (string line in saved)
        {
            merged.Remove(line);
            merged.Add(line);
        }
        foreach (string line in _history)
        {
            merged.Remove(line);
            merged.Add(line);
        }
        _history.Clear();
        _history.AddRange(merged);
        _historyCursor = -1;
    }

    /// <summary>把一条命令填进输入框(浏览器右键"在控制台里生成命令"用)。</summary>
    /// <param name="command">命令行。</param>
    public void Prefill(string command)
    {
        Input = command;
        _historyCursor = -1;
    }

    /// <summary>数据库或只读状态变了 → 刷新提示符。</summary>
    public void RefreshPrompt()
    {
        RaisePropertyChanged(nameof(Prompt));
        RaisePropertyChanged(nameof(InputHint));
    }

    private async Task RunAsync()
    {
        string line = Input.Trim();
        if (line.Length == 0)
        {
            return;
        }
        string command = RedisCommandGuard.Normalize(line);
        RedisCommandVerdict verdict = _connection.Guard.Evaluate(command);
        if (!verdict.Allowed)
        {
            Append(new(_loc.Format("Redis_ConsolePrompt", Prompt, line), RedisReplyLineKind.Command));
            Append(RedisReplyFormatter.Note(verdict.Reason is "readonly"
                ? _loc.Format("Redis_BlockedByReadOnly", command)
                : _loc.Format("Redis_BlockedByProduction", command)));
            Input = string.Empty;
            return;
        }
        if (verdict.NeedsConfirmation
            && !await ConfirmAsync(command, line, verdict).ConfigureAwait(true))
        {
            return;
        }

        _history.Remove(line);
        _history.Add(line);
        _historyCursor = -1;
        // 落时序库:下次打开这条连接时 ↑ 还能翻到,而"谁在什么时候敲了什么"也就此可回溯。
        if (_store is not null)
        {
            await _store.AppendHistoryAsync(_connectionKey, line).ConfigureAwait(true);
        }
        Append(new(_loc.Format("Redis_ConsolePrompt", Prompt, line), RedisReplyLineKind.Command));
        Input = string.Empty;
        IsRunning = true;
        try
        {
            RedisConsoleResult result = await _connection.ExecuteConsoleAsync(line).ConfigureAwait(true);
            foreach (RedisReplyLine reply in result.Lines)
            {
                Append(reply);
            }
            LastElapsed = $"{result.Elapsed.TotalMilliseconds.ToString("0.#", CultureInfo.CurrentCulture)} ms";
            if (result.SelectedDatabase is { } database)
            {
                RefreshPrompt();
                DatabaseSelected?.Invoke(database);
            }
        }
        catch (Exception ex)
        {
            Append(RedisReplyFormatter.Error(ex.Message));
        }
        finally
        {
            IsRunning = false;
        }
    }

    private Task<bool> ConfirmAsync(string command, string line, RedisCommandVerdict verdict) =>
        _confirmation.AskAsync(
            _loc.Format("Redis_ConfirmTitle", command),
            verdict.Risk == RedisCommandRisk.Destructive
                ? _loc["Redis_ConfirmDestructiveBody"]
                : _loc.Format("Redis_ConfirmDangerousBody", command),
            line,
            _loc["Redis_ConfirmRun"],
            _loc["Redis_Cancel"],
            verdict.Risk == RedisCommandRisk.Destructive,
            verdict.NeedsTypedConfirmation ? ConfirmationPhrase : null);

    /// <summary>
    /// "毁"档要键入的串:<c>&lt;端点&gt;/db&lt;库&gt;</c>。
    /// 用端点而不是"YES":同时开着生产与开发时,一句 YES 分不出你在哪台机器上按的。
    /// </summary>
    public string ConfirmationPhrase =>
        _connection.Settings.SupportsDatabases
            ? $"{Endpoint}/db{_connection.Database}"
            : Endpoint;

    private void Append(RedisReplyLine line)
    {
        Lines.Add(new(line.Text, line.Kind));
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    private void RefreshCompletions()
    {
        Completions.Clear();
        string text = Input.Trim();
        // 只在还在敲**第一个词**时补全:参数位上弹命令名只会挡住视线。
        if (text.Length == 0 || text.Contains(' ', StringComparison.Ordinal))
        {
            return;
        }
        foreach (RedisCommandHint hint in _connection.Complete(text))
        {
            Completions.Add(hint);
        }
    }
}
