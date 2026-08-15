using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 聊天面板的历史会话部分:视图切换(聊天 / 设置 / 历史)、会话列表与加载、
/// 以及输入框的 ↑↓ 消息回溯。数据来自 <see cref="ChatHistoryStore" />(插件私有时序库)。
/// </summary>
public partial class ChatPanelView
{
    /// <summary>中部区域三选一。</summary>
    private enum PanelView
    {
        /// <summary>消息流。</summary>
        Chat,

        /// <summary>设置页。</summary>
        Settings,

        /// <summary>历史会话列表。</summary>
        History
    }

    private List<string> _inputHistory = [];
    private int _inputHistoryIndex = -1;
    private string _inputDraft = "";
    private bool _clearHistoryArmed;

    // ---------- 视图切换 ----------

    /// <summary>某个视图开关被点:选中即切到该视图,取消即回聊天。</summary>
    private void OnViewToggled(ToggleButton toggle, PanelView view)
    {
        if (_switchingView)
        {
            return;
        }
        SetActiveView(toggle.IsChecked == true ? view : PanelView.Chat);
    }

    /// <summary>切换中部视图,并把两个开关按钮的选中态对齐(避免互相触发)。</summary>
    private void SetActiveView(PanelView view)
    {
        _switchingView = true;
        try
        {
            ChatScroll.IsVisible = view == PanelView.Chat;
            SettingsHost.IsVisible = view == PanelView.Settings;
            HistoryHost.IsVisible = view == PanelView.History;
            SettingsToggle.IsChecked = view == PanelView.Settings;
            HistoryToggle.IsChecked = view == PanelView.History;
        }
        finally
        {
            _switchingView = false;
        }
        if (view != PanelView.History)
        {
            ResetClearHistoryButton();
        }
    }

    // ---------- 会话列表 ----------

    /// <summary>重建历史会话列表(最近更新在前;当前会话高亮)。搜索是本地筛,不重复查库。</summary>
    private async Task RefreshHistoryListAsync()
    {
        _historySessions = await _historyStore.ListSessionsAsync();
        RenderHistoryList();
    }

    private Border BuildHistoryRow(ChatSessionSummary session)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2 };
        var titleText = new TextBlock
        {
            Classes = { "historyTitle" },
            Text = session.Title.Length > 0 ? session.Title : _loc["Untitled"]
        };
        text.Children.Add(titleText);
        text.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            Text = $"{session.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {_loc.F("MessageCount", session.MessageCount)}"
        });
        grid.Children.Add(text);

        // 行尾三枚小按钮:重命名 / 导出 / 删除
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        Button rename = IconAction("Icon.pencil", _loc["Rename"], () => BeginRename(session, titleText));
        Button export = IconAction("Icon.download", _loc["Export"], () => _ = ExportAsync(session));
        Button delete = IconAction("Icon.trash-2", _loc["Delete"], () => { });
        tools.Children.Add(rename);
        tools.Children.Add(export);
        tools.Children.Add(delete);
        Grid.SetColumn(tools, 1);
        grid.Children.Add(tools);
        // 这三枚都不该把点击冒泡成"打开这个会话"
        foreach (Button button in (Button[])[rename, export, delete])
        {
            button.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        }

        var row = new Border { Classes = { "historyRow" }, Child = grid };
        if (session.Id == _conversationId)
        {
            row.Classes.Add("current");
        }
        delete.Click += async (_, e) =>
        {
            e.Handled = true;
            await _historyStore.DeleteAsync(session.Id);
            if (session.Id == _conversationId)
            {
                StartNewChat();
                SetActiveView(PanelView.History);
            }
            await RefreshHistoryListAsync();
        };
        row.PointerPressed += (_, _) => _ = LoadConversationAsync(session);
        return row;
    }

    /// <summary>清空历史:第一次点是"确认吗",第二次才真删(不弹对话框,插件面板里更轻)。</summary>
    private async Task OnClearHistoryClickedAsync()
    {
        if (!_clearHistoryArmed)
        {
            _clearHistoryArmed = true;
            ClearHistoryButton.Content = _loc["ConfirmClear"];
            if (FindBrush("VelaError") is { } warn)
            {
                ClearHistoryButton.Foreground = warn;
                ClearHistoryButton.BorderBrush = warn;
            }
            return;
        }
        ResetClearHistoryButton();
        await _historyStore.ClearAsync();
        _inputHistory.Clear();
        StartNewChat();
        SetActiveView(PanelView.History);
        await RefreshHistoryListAsync();
    }

    private void ResetClearHistoryButton()
    {
        _clearHistoryArmed = false;
        ClearHistoryButton.Content = _loc["ClearHistory"];
        ClearHistoryButton.ClearValue(ForegroundProperty);
        ClearHistoryButton.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty);
    }

    // ---------- 加载历史会话 ----------

    /// <summary>
    /// 切换到某个历史会话:终止在途请求,用库里的消息重建消息流与模型上下文。
    /// 会话身份(id 与创建时刻)沿用摘要里的原值 —— 继续聊天时更新的还是同一条摘要。
    /// </summary>
    private async Task LoadConversationAsync(ChatSessionSummary session)
    {
        try
        {
            _cts?.Cancel();
            IReadOnlyList<ChatEntry> entries = await _historyStore.LoadAsync(session.Id);
            _history.Clear();
            MessagesPanel.Children.Clear();
            ResetUsage();
            _alwaysApproved.Clear(); // 放行记忆只在同一段对话里有效
            _userBubbleIndex.Clear();
            ClearSuggestions(); // 载入的是别的会话,上一段的建议不再相关
            _conversationId = session.Id;
            _conversationStartedAt = session.CreatedAt;
            _persistedCount = entries.Count;
            _inputHistoryIndex = -1;
            ResetMessageWindow();
            for (int index = 0; index < entries.Count; index++)
            {
                // 分帧建:2000 条一口气建完会把界面按住好几秒
                await YieldEveryBatchAsync(index);
                ChatEntry entry = entries[index];
                if (entry.Role == "user")
                {
                    AddUserBubble(entry.Text);
                    _history.Add(new(ChatRole.User, entry.Text));
                }
                else
                {
                    var bubble = new AssistantBubble(this);
                    MessagesPanel.Children.Add(bubble.Root);
                    // 先摆思考与工具卡(存过的话),再摆正文 —— 与直播时的先后一致
                    if (entry.Meta is { } meta)
                    {
                        bubble.Restore(meta);
                    }
                    bubble.AppendText(entry.Text);
                    bubble.FinishStreaming(
                        entry.Meta?.Model,
                        entry.At.ToLocalTime(),
                        entry.Meta is { ElapsedMs: > 0 } m ? TimeSpan.FromMilliseconds(m.ElapsedMs) : null);
                    _history.Add(new(ChatRole.Assistant, entry.Text));
                }
            }
            TrimMessageWindow();
            // 载入的这段会话已经用掉了这些序号,新消息要从它们之后续
            _sequenceHighWater = _persistedCount;
            // 上次压过的摘要一并恢复,免得一进来就重压一次(那是一次白花的请求)
            await RestoreSummaryAsync();
            StatusText.Text = _loc.F("HistoryLoaded", entries.Count);
            SetActiveView(PanelView.Chat);
            RequestAutoScroll(force: true);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Loading conversation failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    // ---------- 输入框 ↑↓ 历史 ----------

    /// <summary>
    /// 后台补齐 ↑↓ 可翻的历史输入。失败只是翻不了历史,不该让面板打不开,
    /// 也不覆盖用户在这期间已经发过的新输入(那些由 RememberInput 顶在前面)。
    /// </summary>
    private async Task LoadInputHistoryAsync()
    {
        try
        {
            IReadOnlyList<string> stored = await _historyStore.RecentUserInputsAsync();
            var merged = new List<string>(_inputHistory);
            foreach (string item in stored)
            {
                if (!merged.Contains(item, StringComparer.Ordinal))
                {
                    merged.Add(item);
                }
            }
            _inputHistory = merged;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Loading input history failed: {ex.Message}");
        }
    }

    /// <summary>记住这次发送的内容(去重后置顶,上限 100 条)。</summary>
    private void RememberInput(string text)
    {
        _inputHistory.RemoveAll(item => string.Equals(item, text, StringComparison.Ordinal));
        _inputHistory.Insert(0, text);
        if (_inputHistory.Count > 100)
        {
            _inputHistory.RemoveRange(100, _inputHistory.Count - 100);
        }
        _inputHistoryIndex = -1;
        _inputDraft = "";
    }

    /// <summary>
    /// ↑/↓ 回溯已发送的消息:索引 -1 表示"当前草稿",往上翻越走越旧,
    /// 翻回 -1 时恢复草稿。返回是否消费了这次按键。
    /// </summary>
    private bool RecallInput(bool older)
    {
        if (_inputHistory.Count == 0)
        {
            return false;
        }
        int next = older ? _inputHistoryIndex + 1 : _inputHistoryIndex - 1;
        if (next >= _inputHistory.Count)
        {
            return true; // 已到最旧:吃掉按键,不让光标跑
        }
        if (next < -1)
        {
            return false;
        }
        if (_inputHistoryIndex == -1 && older)
        {
            _inputDraft = InputBox.Text ?? "";
        }
        _inputHistoryIndex = next;
        InputBox.Text = next == -1 ? _inputDraft : _inputHistory[next];
        InputBox.CaretOffset = InputBox.Text?.Length ?? 0;
        return true;
    }

    /// <summary>光标是否在第一行(其前没有换行)—— 多行编辑时 ↑ 仍归 TextBox。</summary>
    private bool CaretOnFirstLine()
    {
        string text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretOffset, 0, text.Length);
        return text.AsSpan(0, caret).IndexOf('\n') < 0;
    }

    /// <summary>光标是否在最后一行(其后没有换行)。</summary>
    private bool CaretOnLastLine()
    {
        string text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretOffset, 0, text.Length);
        return text.AsSpan(caret).IndexOf('\n') < 0;
    }

    private Avalonia.Styling.ControlTheme? FindTheme(string key)
        => this.TryFindResource(key, out object? value) && value is Avalonia.Styling.ControlTheme theme ? theme : null;
}
