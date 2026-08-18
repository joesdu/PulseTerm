using System.Globalization;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 写入路径的界面侧:值编辑、成员增删改、TTL、重命名、删除,以及只读开关。
/// <para>
/// 每一条写操作都先过 <see cref="RedisCommandGuard" />:被只读模式拦住就给出
/// "为什么 + 怎么解除",而不是灰一个按钮让用户猜。危险与毁灭档走面板内的确认框。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>面板内的确认闸门(护栏的呈现出口)。</summary>
    public RedisConfirmation Confirmation { get; } = new();

    // ── 只读开关 ──────────────────────────────────────────────────

    /// <summary>切换只读模式。</summary>
    public AsyncCommand ToggleReadOnlyCommand { get; private set; } = null!;

    /// <summary>当前是否只读(与闸门同源,切换后立即生效)。</summary>
    public bool IsReadOnlyNow => _connection.Guard.ReadOnly;

    private async Task ToggleReadOnlyAsync()
    {
        if (!_connection.Guard.ReadOnly)
        {
            // 开只读不需要确认:那是往安全的方向走。
            SetReadOnly(true);
            return;
        }
        // 关只读要过一次脑子,生产环境尤其 —— 这一下之后每个按钮都真的会改数据。
        // 生产环境要**手打确认串**:文案里就是这么承诺的,不给输入框等于文案在骗人。
        bool confirmed = await Confirmation.AskAsync(
            Loc["Redis_ReadOnlyOffTitle"],
            IsProduction ? Loc["Redis_ReadOnlyOffProductionBody"] : Loc["Redis_ReadOnlyOffBody"],
            $"{Endpoint}  db{CurrentDatabase}",
            Loc["Redis_ReadOnlyOffConfirm"],
            Loc["Redis_Cancel"],
            destructive: IsProduction,
            expectedText: IsProduction ? Console.ConfirmationPhrase : null).ConfigureAwait(true);
        if (confirmed)
        {
            SetReadOnly(false);
        }
    }

    private void SetReadOnly(bool value)
    {
        _connection.Guard.ReadOnly = value;
        RaisePropertyChanged(nameof(IsReadOnlyNow));
        RaisePropertyChanged(nameof(CanWrite));
        Console.RefreshPrompt();
    }

    /// <summary>写操作是否可用(界面据此启用/禁用编辑控件)。</summary>
    public bool CanWrite => !_connection.Guard.ReadOnly;

    // ── 字符串值编辑 ──────────────────────────────────────────────

    /// <summary>字符串编辑框里的文本(与 <see cref="StringValue" /> 分开:后者是服务端的现值)。</summary>
    public string StringDraft
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(IsStringDirty));
            SaveStringCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>草稿与现值不同(界面据此显示保存按钮)。</summary>
    public bool IsStringDirty => !string.Equals(StringDraft, StringValue, StringComparison.Ordinal);

    /// <summary>
    /// 字符串值是否可编辑。**被截断的值一律只读** ——
    /// 让用户编辑"前 256 KB"再整体写回,等于用一次保存把后面几 MB 静默删掉。
    /// </summary>
    public bool CanEditString => IsStringSelected && CanWrite && TruncationNotice.Length == 0
                                 // 十六进制是**转储排版**(偏移 + ASCII 侧栏),不是可回写的表示。
                                 // 允许在上面编辑就得去猜哪些字符是数据、哪些是排版 —— 那是在赌。
                                 && ValueFormat != RedisValueFormat.Hex;

    /// <summary>保存字符串值。</summary>
    public AsyncCommand SaveStringCommand { get; private set; } = null!;

    private async Task SaveStringAsync()
    {
        if (Selected is not { Key: { } key } || !CanEditString)
        {
            return;
        }
        if (!TryEncodeDraft(out byte[] bytes))
        {
            // 转义写坏了:说清位置,**不写**。宁可让用户改一处笔误,也不要写进一段
            // 谁也说不清的字节。
            return;
        }
        await GuardedAsync("SET", async () =>
        {
            // keepTtl:用户改的是"值",不是"这个键还能活多久"。
            //
            // 字节按**当前形态**解回,而不是无脑 UTF8.GetBytes(草稿)。后者对二进制值是一次
            // 静默的数据损坏:界面显示的是转义形式,照着编码写回去存的就是那串
            // 反斜杠字面量本身 —— 十个字节的 gzip 头会变成四十个字节的 ASCII。
            await _connection.SetStringAsync(key, bytes).ConfigureAwait(true);
            StringValue = StringDraft;
            RaisePropertyChanged(nameof(IsStringDirty));
            await ReloadSelectedAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    // ── 成员编辑(哈希 / 列表 / 集合 / 有序集合)────────────────────

    /// <summary>当前选中的行。</summary>
    public RedisElementRow? SelectedElement
    {
        get;
        set
        {
            SetProperty(ref field, value);
            EditLabel = field?.Label ?? string.Empty;
            EditValue = field?.Value ?? string.Empty;
            EditScore = field?.ScoreText ?? string.Empty;
            RaisePropertyChanged(nameof(HasSelectedElement));
            SaveElementCommand.RaiseCanExecuteChanged();
            RemoveElementCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>有选中的行。</summary>
    public bool HasSelectedElement => SelectedElement is not null;

    /// <summary>编辑条里的标签(字段名 / 索引 / 成员)。</summary>
    public string EditLabel
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>编辑条里的值。</summary>
    public string EditValue
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>编辑条里的分值(仅有序集合)。</summary>
    public string EditScore
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>新增行:标签。</summary>
    public string NewLabel
    {
        get;
        set
        {
            SetProperty(ref field, value);
            AddElementCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>新增行:值。</summary>
    public string NewValue
    {
        get;
        set
        {
            SetProperty(ref field, value);
            AddElementCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>新增行:分值。</summary>
    public string NewScore
    {
        get;
        set
        {
            SetProperty(ref field, value);
            AddElementCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>
    /// 列表的"移除"要额外说明语义:**列表没有按索引删除的原语**,
    /// 删的是第一个等于该值的元素。这句话必须出现在界面上,不能只写在代码注释里。
    /// </summary>
    public string ElementRemoveNote => Selected?.Type is "list" ? Loc["Redis_ListRemoveNote"] : string.Empty;

    /// <summary>有语义说明要显示。</summary>
    public bool HasElementRemoveNote => ElementRemoveNote.Length > 0;

    /// <summary>
    /// 把当前键的读取命令填进控制台。
    /// <para>
    /// 这是把"点点点"与"敲命令"缝在一起的关键一针:重度用户永远会回到命令行,
    /// 而从浏览器带着键名跳过去,省掉的正是最烦的那一步 —— 手抄键名。
    /// </para>
    /// </summary>
    public AsyncCommand SendToConsoleCommand { get; private set; } = null!;

    private Task SendToConsoleAsync()
    {
        if (Selected is not { Key: { } key } info)
        {
            return Task.CompletedTask;
        }
        string command = info.Type switch
        {
            "string" => "GET",
            "hash" => "HGETALL",
            "list" => "LRANGE",
            "set" => "SMEMBERS",
            "zset" => "ZRANGE",
            "stream" => "XRANGE",
            _ => "TYPE"
        };
        // 键名用转义形式并加引号:二进制键与带空格的键都能直接执行。
        string suffix = info.Type switch
        {
            "list" => " 0 -1",
            "zset" => " 0 -1 WITHSCORES",
            "stream" => " - +",
            _ => string.Empty
        };
        Console.Prefill($"{command} \"{key.Display}\"{suffix}");
        IsDrawerOpen = true;
        return SwitchTabAsync(RedisDrawerTab.Console);
    }

    /// <summary>新增行的标签框是否有意义(列表按索引追加,不需要标签)。</summary>
    public bool NewLabelApplies => Selected?.Type is "hash";

    /// <summary>分值框是否有意义。</summary>
    public bool ScoreApplies => Selected?.Type is "zset";

    /// <summary>保存选中行的改动。</summary>
    public AsyncCommand SaveElementCommand { get; private set; } = null!;

    /// <summary>移除选中行。</summary>
    public AsyncCommand RemoveElementCommand { get; private set; } = null!;

    /// <summary>新增一行。</summary>
    public AsyncCommand AddElementCommand { get; private set; } = null!;

    /// <summary>
    /// 成员的字段名/值必须是**能原样往返的文本**,否则拒绝写入。
    /// <para>
    /// 成员表这一路的读写至今仍是字符串:读出来时二进制被转义成 \xNN 显示,
    /// 写回去却是把那串反斜杠字面量按 UTF-8 编码 —— 与字符串值编辑器刚修掉的是同一个
    /// 静默损坏。字符串值那边已经改成「字节 + 可逆形态」了,成员表的形态开关还没做,
    /// 所以这里先**明确挡住**:宁可暂时不能在成员表里改二进制成员,
    /// 也绝不让一次保存把它换成一串反斜杠字面量。
    /// </para>
    /// </summary>
    /// <param name="label">服务端读回的字段名/索引。</param>
    /// <param name="value">服务端读回的值。</param>
    /// <returns>可以安全写入。</returns>
    private bool EnsureElementIsTextSafe(string label, string value)
    {
        if (!LooksEscaped(label) && !LooksEscaped(value))
        {
            return true;
        }
        StatusMessage = Loc["Redis_BinaryMemberReadOnly"];
        return false;
    }

    /// <summary>
    /// 文本里含 <c>\xNN</c> 形式的转义 —— 那正是二进制成员被转义后的样子。
    /// <para>
    /// 判据刻意保守:一个**真的**含有反斜杠-x-两位十六进制的纯文本成员会被误挡。
    /// 这个方向的错(少让改一次)可以自己发现并绕开;反过来那个方向的错(多写坏一次)
    /// 用户根本看不见。
    /// </para>
    /// </summary>
    private static bool LooksEscaped(string text)
    {
        for (int i = 0; i + 3 < text.Length; i++)
        {
            if (text[i] == '\\' && text[i + 1] == 'x'
                && Uri.IsHexDigit(text[i + 2]) && Uri.IsHexDigit(text[i + 3]))
            {
                return true;
            }
        }
        return false;
    }

    private async Task SaveElementAsync()
    {
        if (Selected is not { Key: { } key } info || SelectedElement is not { } row || !CanWrite)
        {
            return;
        }
        if (!EnsureElementIsTextSafe(row.Label, row.Value))
        {
            return;
        }
        await GuardedAsync(WriteCommandFor(info.Type), async () =>
        {
            switch (info.Type)
            {
                case "hash":
                    // 改了字段名等于"删旧的、加新的" —— 如实按两步做,而不是假装有 rename。
                    if (!string.Equals(row.Label, EditLabel, StringComparison.Ordinal))
                    {
                        await _connection.DeleteHashFieldAsync(key, row.Label).ConfigureAwait(true);
                    }
                    await _connection.SetHashFieldAsync(key, EditLabel, EditValue).ConfigureAwait(true);
                    break;
                case "list" when long.TryParse(row.Label, NumberStyles.Integer, CultureInfo.InvariantCulture, out long index):
                    await _connection.SetListItemAsync(key, index, EditValue).ConfigureAwait(true);
                    break;
                case "set":
                    if (!string.Equals(row.Label, EditLabel, StringComparison.Ordinal))
                    {
                        await _connection.RemoveSetMemberAsync(key, row.Label).ConfigureAwait(true);
                        await _connection.AddSetMemberAsync(key, EditLabel).ConfigureAwait(true);
                    }
                    break;
                case "zset":
                    if (!double.TryParse(EditScore, NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
                    {
                        StatusMessage = Loc["Redis_ScoreInvalid"];
                        return;
                    }
                    if (!string.Equals(row.Label, EditLabel, StringComparison.Ordinal))
                    {
                        await _connection.RemoveSortedMemberAsync(key, row.Label).ConfigureAwait(true);
                    }
                    await _connection.SetSortedMemberAsync(key, EditLabel, score).ConfigureAwait(true);
                    break;
                default:
                    return;
            }
            await ReloadSelectedAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RemoveElementAsync()
    {
        if (Selected is not { Key: { } key } info || SelectedElement is not { } row || !CanWrite)
        {
            return;
        }
        await GuardedAsync(WriteCommandFor(info.Type), async () =>
        {
            switch (info.Type)
            {
                case "hash":
                    await _connection.DeleteHashFieldAsync(key, row.Label).ConfigureAwait(true);
                    break;
                case "list":
                    await _connection.RemoveListValueAsync(key, row.Value).ConfigureAwait(true);
                    break;
                case "set":
                    await _connection.RemoveSetMemberAsync(key, row.Label).ConfigureAwait(true);
                    break;
                case "zset":
                    await _connection.RemoveSortedMemberAsync(key, row.Label).ConfigureAwait(true);
                    break;
                default:
                    return;
            }
            SelectedElement = null;
            await ReloadSelectedAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task AddElementAsync()
    {
        if (Selected is not { Key: { } key } info || !CanWrite)
        {
            return;
        }
        await GuardedAsync(WriteCommandFor(info.Type), async () =>
        {
            switch (info.Type)
            {
                case "hash" when NewLabel.Length > 0:
                    await _connection.SetHashFieldAsync(key, NewLabel, NewValue).ConfigureAwait(true);
                    break;
                case "list" when NewValue.Length > 0:
                    await _connection.PushListAsync(key, NewValue, atHead: false).ConfigureAwait(true);
                    break;
                case "set" when NewValue.Length > 0:
                    await _connection.AddSetMemberAsync(key, NewValue).ConfigureAwait(true);
                    break;
                case "zset" when NewValue.Length > 0:
                    if (!double.TryParse(NewScore, NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
                    {
                        StatusMessage = Loc["Redis_ScoreInvalid"];
                        return;
                    }
                    await _connection.SetSortedMemberAsync(key, NewValue, score).ConfigureAwait(true);
                    break;
                default:
                    return;
            }
            NewLabel = string.Empty;
            NewValue = string.Empty;
            NewScore = string.Empty;
            await ReloadSelectedAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private bool CanAddElement() => Selected?.Type switch
    {
        "hash" => CanWrite && NewLabel.Trim().Length > 0,
        "list" or "set" => CanWrite && NewValue.Trim().Length > 0,
        "zset" => CanWrite && NewValue.Trim().Length > 0 && NewScore.Trim().Length > 0,
        _ => false
    };

    private static string WriteCommandFor(string type) => type switch
    {
        "hash" => "HSET",
        "list" => "LSET",
        "set" => "SADD",
        "zset" => "ZADD",
        _ => "SET"
    };

    // ── TTL ───────────────────────────────────────────────────────

    /// <summary>TTL 输入框。</summary>
    public string TtlDraft
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(TtlPreview));
            ApplyTtlCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>
    /// TTL 输入的实时回显:把用户输入换算成绝对时间点 + 剩余时长。
    /// **换算给他看**,而不是等他按下去才发现自己填的是分钟还是秒。
    /// </summary>
    public string TtlPreview
    {
        get
        {
            if (TtlDraft.Trim().Length == 0)
            {
                return string.Empty;
            }
            if (!RedisTtl.TryParse(TtlDraft, DateTimeOffset.Now, out TimeSpan ttl))
            {
                return Loc["Redis_TtlInvalid"];
            }
            DateTimeOffset expiry = DateTimeOffset.Now + ttl;
            return Loc.Format("Redis_TtlPreview",
                expiry.ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                RedisTtl.Describe(ttl));
        }
    }

    /// <summary>应用 TTL。</summary>
    public AsyncCommand ApplyTtlCommand { get; private set; } = null!;

    /// <summary>去掉过期时间。</summary>
    public AsyncCommand PersistCommand { get; private set; } = null!;

    private async Task ApplyTtlAsync()
    {
        if (Selected is not { Key: { } key } || !RedisTtl.TryParse(TtlDraft, DateTimeOffset.Now, out TimeSpan ttl))
        {
            return;
        }
        await GuardedAsync("EXPIRE", async () =>
        {
            await _connection.ExpireAsync(key, ttl).ConfigureAwait(true);
            TtlDraft = string.Empty;
            await ReloadSelectedAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task PersistAsync()
    {
        if (Selected is not { Key: { } key })
        {
            return;
        }
        await GuardedAsync("PERSIST", async () =>
        {
            await _connection.PersistAsync(key).ConfigureAwait(true);
            await ReloadSelectedAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    // ── 重命名 / 删除 ─────────────────────────────────────────────

    /// <summary>重命名输入框。</summary>
    public string RenameDraft
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RenameCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>重命名。</summary>
    public AsyncCommand RenameCommand { get; private set; } = null!;

    /// <summary>删除当前键。</summary>
    public AsyncCommand DeleteKeyCommand { get; private set; } = null!;

    private async Task RenameAsync()
    {
        if (Selected is not { Key: { } key } || RenameDraft.Trim().Length == 0)
        {
            return;
        }
        var target = new RedisKeyName(RenameDraft.Trim());
        await GuardedAsync("RENAME", async () =>
        {
            // 先试不覆盖。**RENAME 会静默覆盖目标键** —— 那是一次无声的数据丢失,
            // 所以覆盖必须是用户明确点过的。
            if (await _connection.RenameAsync(key, target, overwrite: false).ConfigureAwait(true))
            {
                await AfterKeyRenamedAsync(target).ConfigureAwait(true);
                return;
            }
            bool overwrite = await Confirmation.AskAsync(
                Loc.Format("Redis_RenameExistsTitle", target.Display),
                Loc["Redis_RenameExistsBody"],
                $"RENAME {key.Display} {target.Display}",
                Loc["Redis_Overwrite"],
                Loc["Redis_Cancel"],
                destructive: true).ConfigureAwait(true);
            if (!overwrite)
            {
                return;
            }
            await _connection.RenameAsync(key, target, overwrite: true).ConfigureAwait(true);
            await AfterKeyRenamedAsync(target).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task AfterKeyRenamedAsync(RedisKeyName target)
    {
        RenameDraft = string.Empty;
        // 键名变了,树上那一支已经不成立 —— 重扫一遍比就地改树可靠得多
        // (改树要同时维护计数、排序与索引,而重扫是幂等的)。
        await ScanAsync(restart: true).ConfigureAwait(true);
        StatusMessage = Loc.Format("Redis_RenamedTo", target.Display);
    }

    private async Task DeleteKeyAsync()
    {
        if (Selected is not { Key: { } key })
        {
            return;
        }
        bool confirmed = await Confirmation.AskAsync(
            Loc.Format("Redis_DeleteKeyTitle", key.Display),
            Loc["Redis_DeleteKeyBody"],
            $"UNLINK {key.Display}",
            Loc["Redis_Delete"],
            Loc["Redis_Cancel"],
            destructive: true).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        await GuardedAsync("UNLINK", async () =>
        {
            await _connection.DeleteAsync([key]).ConfigureAwait(true);
            SelectedRow = null;
            await ScanAsync(restart: true).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    // ── 共用闸门 ──────────────────────────────────────────────────

    /// <summary>
    /// 过闸门后执行一次写操作。
    /// <para>
    /// 被拦住时给出"为什么 + 怎么解除"(而不是灰一个按钮),需要确认的先问,
    /// 失败一律落到状态条上而不是抛给界面。
    /// </para>
    /// </summary>
    private async Task GuardedAsync(string command, Func<Task> action)
    {
        RedisCommandVerdict verdict = _connection.Guard.Evaluate(command);
        if (!verdict.Allowed)
        {
            StatusMessage = verdict.Reason is "readonly"
                ? Loc.Format("Redis_BlockedByReadOnly", command)
                : Loc.Format("Redis_BlockedByProduction", command);
            return;
        }
        if (verdict.NeedsConfirmation)
        {
            bool ok = await Confirmation.AskAsync(
                Loc.Format("Redis_ConfirmTitle", command),
                verdict.Risk == RedisCommandRisk.Destructive
                    ? Loc["Redis_ConfirmDestructiveBody"]
                    : Loc.Format("Redis_ConfirmDangerousBody", command),
                command,
                Loc["Redis_ConfirmRun"],
                Loc["Redis_Cancel"],
                verdict.Risk == RedisCommandRisk.Destructive,
                verdict.NeedsTypedConfirmation ? Console.ConfirmationPhrase : null).ConfigureAwait(true);
            if (!ok)
            {
                return;
            }
        }
        try
        {
            StatusMessage = string.Empty;
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"'{command}' failed.", ex);
        }
    }

    /// <summary>写完之后重读一次当前键(元信息 + 内容),让界面反映服务端的真实状态。</summary>
    private async Task ReloadSelectedAsync()
    {
        if (Selected?.Key is { } key)
        {
            await LoadKeyAsync(key).ConfigureAwait(true);
        }
    }

    /// <summary>在构造末尾接线编辑相关命令(与只读/选中状态联动)。</summary>
    private void InitializeEditing()
    {
        ToggleReadOnlyCommand = new(ToggleReadOnlyAsync);
        SaveStringCommand = new(SaveStringAsync, () => CanEditString && IsStringDirty);
        UseTextFormatCommand = new(() => SwitchValueFormatAsync(RedisValueFormat.Text), () => CanUseTextFormat);
        UseEscapedFormatCommand = new(() => SwitchValueFormatAsync(RedisValueFormat.Escaped));
        UseHexFormatCommand = new(() => SwitchValueFormatAsync(RedisValueFormat.Hex));
        SaveElementCommand = new(SaveElementAsync, () => CanWrite && HasSelectedElement);
        RemoveElementCommand = new(RemoveElementAsync, () => CanWrite && HasSelectedElement);
        AddElementCommand = new(AddElementAsync, CanAddElement);
        ApplyTtlCommand = new(ApplyTtlAsync,
            () => CanWrite && RedisTtl.TryParse(TtlDraft, DateTimeOffset.Now, out _));
        PersistCommand = new(PersistAsync, () => CanWrite);
        RenameCommand = new(RenameAsync, () => CanWrite && RenameDraft.Trim().Length > 0);
        DeleteKeyCommand = new(DeleteKeyAsync, () => CanWrite);
        SendToConsoleCommand = new(SendToConsoleAsync, () => HasSelection);
    }

    /// <summary>选中的键变了 → 编辑区的草稿与可用性全部重算。</summary>
    private void ResetEditingForSelection()
    {
        StringDraft = StringValue;
        SelectedElement = null;
        EditLabel = string.Empty;
        EditValue = string.Empty;
        EditScore = string.Empty;
        NewLabel = string.Empty;
        NewValue = string.Empty;
        NewScore = string.Empty;
        TtlDraft = string.Empty;
        RenameDraft = string.Empty;
        RaisePropertyChanged(nameof(CanEditString));
        RaisePropertyChanged(nameof(IsStringDirty));
        RaisePropertyChanged(nameof(ElementRemoveNote));
        RaisePropertyChanged(nameof(HasElementRemoveNote));
        RaisePropertyChanged(nameof(NewLabelApplies));
        RaisePropertyChanged(nameof(ScoreApplies));
        SendToConsoleCommand.RaiseCanExecuteChanged();
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        RaiseFavoriteState();
        SaveStringCommand.RaiseCanExecuteChanged();
        AddElementCommand.RaiseCanExecuteChanged();
        ApplyTtlCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        DeleteKeyCommand.RaiseCanExecuteChanged();
    }
}
