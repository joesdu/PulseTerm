using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 边跑边补:上一轮还没答完,用户就能接着发下一条 —— 消息不再被"正忙"挡掉,
/// 而是排进队列,由 <see cref="SteeringChatClient" /> 在模型下一步之前送进上下文。
/// </summary>
/// <remarks>
/// <para>
/// 这是 ClaudeCode 那种对话方式:交代完一件事,发现还有条件要补,不必等它答完、
/// 更不必按停止再重说一遍。补的这句会成为它<b>后续动作</b>的依据 —— Agent 正在跑工具时补一句
/// "只看最近一小时的",它读完当前工具结果就能照办。
/// </para>
/// <para>
/// 插话在三个地方留痕,缺一不可:输入框上方的排队芯片(还没送出去,点一下可撤回)、
/// 回复里的一张卡(送到了,而且看得出是插在哪一步之间的)、以及对话历史与库
/// (下一轮模型仍读得到,翻回旧会话也还在)。
/// </para>
/// <para>
/// 一轮结束时队列还没空,说明这些插话谁也没赶上(纯对话模式只发一次请求,或最后一步之后才排进来)。
/// 那就分两种处置:答完了 → 直接作为下一轮发出去;被停掉或出错了 → 原样放回输入框,
/// 该重试还是该改写由用户决定,别替他自动再发一次。
/// </para>
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>最多排几条。再多就该让用户先看看模型答成什么样了。</summary>
    private const int MaxQueuedMessages = 10;

    /// <summary>单枚排队芯片上显示几个字。</summary>
    private const int MaxQueuedChipChars = 42;

    // 插话状态(队列 / 本轮通道 / 已提交条数)按对话各持一份,见 Conversation。
    // 这里经代理落到当前那一轮的那一份:_steeringQueue / _steering / _steeringCommitted。

    // ---------- 入队 ----------

    /// <summary>
    /// 一轮进行中收到的新消息:排进队列而不是丢掉。
    /// </summary>
    /// <param name="text">消息正文。</param>
    /// <param name="fromUser">
    /// 是否为用户在输入框里键入的内容 —— 只有它才进 ↑↓ 历史、才做 <c>@</c> 文件引用展开
    /// (与 <see cref="SendAsync" /> 同一套口径)。
    /// </param>
    private async Task QueueWhileBusyAsync(string text, bool fromUser)
    {
        if (text.Length == 0 && _attachments.Count == 0)
        {
            return;
        }
        if (SteeringQueue.Count >= MaxQueuedMessages)
        {
            StatusText.Text = _loc.F("QueueFull", MaxQueuedMessages);
            return;
        }
        // 先清空输入框:展开远端引用要走一趟 SFTP,那期间输入框该已经是空的,
        // 否则用户会以为没发出去、又敲一次回车。
        InputBox.Text = "";
        CloseFilePicker();
        if (fromUser)
        {
            RememberInput(text);
        }
        string display = text + AttachmentTrace();
        try
        {
            // 引用在<b>入队这一刻</b>展开:排队期间远端文件还可能被改,
            // 用户按下回车时看到的那份才是他想发的那份。
            (string modelText, IReadOnlyList<string> _, IReadOnlyList<string> unreadable) = fromUser
                ? await ResolveAttachmentsAsync(text, Cts?.Token ?? CancellationToken.None)
                : (text, [], []);
            if (unreadable.Count > 0)
            {
                AddAttachmentFailureNote(unreadable);
            }
            var message = new ChatMessage(ChatRole.User, BuildUserContents(modelText));
            ClearAttachments();
            SteeringQueue.Enqueue(new SteeringMessage(display, text, message));
        }
        catch (OperationCanceledException)
        {
            // 展开途中用户按了停止:内容还给输入框,别把他敲的东西吞了
            RestoreToInput(text);
            return;
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Queueing a follow-up message failed: {ex.Message}");
            RestoreToInput(text);
            return;
        }
        RenderQueuedChips();
        // 展开远端引用要走一趟 SFTP,那期间这一轮可能已经答完了 —— 队里没人来取了,
        // 就地当作下一轮发出去,别让它一直挂在那儿。
        if (!Busy)
        {
            if (SteeringQueue.DrainMerged() is { } next)
            {
                RenderQueuedChips();
                await SendAsync(next.DisplayText, fromUser: false, prepared: next);
            }
            return;
        }
        StatusText.Text = _loc["Queued"];
    }

    // ---------- 送达 ----------

    /// <summary>
    /// 插话通道的送达回调。<b>在发请求的那个线程上被调</b>,所以只负责把活儿甩回 UI 线程。
    /// 带上 <paramref name="conv" />:这条回调不在原轮的异步流里,得显式说清楚补给哪份对话。
    /// </summary>
    private void OnSteeringDelivered(Conversation conv) => Dispatcher.UIThread.Post(() => _ = CommitSteeringAsync(conv));

    /// <summary>
    /// 把"已经送进请求"的插话补进界面、对话历史与库。
    /// </summary>
    /// <remarks>
    /// <b>只在 UI 线程调</b>,且必须可重入:送达回调与本轮收尾都会调它(收尾那次是兜底 ——
    /// 回调的 Post 可能还排在队里没跑到)。名单指针在任何 await 之前就推进,重入不会重复提交。
    /// </remarks>
    private async Task CommitSteeringAsync(Conversation conv)
    {
        // 送达回调经 Post 过来时并不在原轮的异步流里,代理会误落到"正显示的那份"——
        // 显式把本次读写钉在 conv 上(在原轮主线里调用时,这一步是无害的重复)。
        _turnScope.Value = conv;
        if (Steering is not { } steering)
        {
            return;
        }
        IReadOnlyList<SteeringMessage> delivered = steering.Delivered;
        if (delivered.Count <= SteeringCommitted)
        {
            return;
        }
        List<SteeringMessage> fresh = [.. delivered.Skip(SteeringCommitted)];
        SteeringCommitted = delivered.Count;
        foreach (SteeringMessage item in fresh)
        {
            // 卡先挂、历史后加:没有回复气泡可挂时退回一条普通用户气泡,
            // 而那条气泡要按"它将落到历史的哪个下标"登记(见 AddUserBubble)。
            AddSteeringCard(item.DisplayText);
            History.Add(item.Message);
        }
        RenderQueuedChips();
        UpdateEmptyState();
        // 序号在 PersistAsync 里是同步领的,所以即使这里与收尾那次交错,
        // 库里的先后仍与历史一致(本轮:原消息 → 插话 → 助手回复)。
        foreach (SteeringMessage item in fresh)
        {
            await PersistAsync("user", item.DisplayText);
        }
    }

    /// <summary>
    /// 在正在生成的那条回复里挂一张"你补充了"的卡。位置就是它真正被送进去的那一步之间 ——
    /// 读起来才对得上:模型是读到这句之后才接着往下做的。
    /// </summary>
    private void AddSteeringCard(string text)
    {
        if (ActiveBubble is not { } bubble)
        {
            // 没有回复气泡可挂(还没开口就送进去了):当作一条普通用户消息摆进消息流
            AddUserBubble(text);
            RequestAutoScroll(force: true);
            return;
        }
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(BuildCardHeader("Icon.user", "VelaAccent", _loc["Interjected"], null));
        stack.Children.Add(new SelectableTextBlock
        {
            Text = FileReference.Shorten(text),
            TextWrapping = TextWrapping.Wrap
        });
        bubble.AddCard(new Border { Classes = { "steeringCard" }, Child = stack });
        RequestAutoScroll(force: true);
    }

    // ---------- 收尾 ----------

    /// <summary>一轮开始:换一条通道,送达名单从头数。</summary>
    private void BeginSteering(SteeringChatClient steering)
    {
        Steering = steering;
        SteeringCommitted = 0;
    }

    /// <summary>一轮结束:通道作废(晚到的送达回调就此变成空转)。</summary>
    private void EndSteering()
    {
        Steering = null;
        SteeringCommitted = 0;
    }

    /// <summary>
    /// 把队里剩下的插话原样放回输入框(这一轮被停掉或出错时用)。
    /// 自动替他再发一次是不对的:刚失败的那次多半还会失败,而按停止本身就是"我要改主意"。
    /// </summary>
    private void RestoreQueuedToInput()
    {
        IReadOnlyList<SteeringMessage> left = SteeringQueue.DrainAll();
        if (left.Count == 0)
        {
            return;
        }
        RenderQueuedChips();
        RestoreToInput(string.Join("\n\n", left.Select(item => item.RawText)));
        StatusText.Text = _loc["QueueReturned"];
    }

    /// <summary>把一段文本放回输入框(已有草稿就接在后面,不覆盖)。</summary>
    private void RestoreToInput(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        InputBox.Text = InputBox.Document.TextLength == 0 ? text : $"{InputBox.Text}\n\n{text}";
        InputBox.CaretOffset = InputBox.Document.TextLength;
        InputBox.TextArea.Focus();
    }

    /// <summary>清空队列(换会话、关面板时用)。</summary>
    private void ClearQueuedMessages()
    {
        SteeringQueue.DrainAll();
        RenderQueuedChips();
    }

    // ---------- 芯片 ----------

    /// <summary>把排队中的插话画成一排可撤回的芯片(输入框上方,与附件芯片同一副长相)。</summary>
    private void RenderQueuedChips()
    {
        // 排队芯片是共享顶栏,只画正显示那份的队列;后台那份切回来时由 RefreshForeground 重画。
        if (!IsForeground)
        {
            return;
        }
        QueuedBar.Children.Clear();
        foreach (SteeringMessage item in SteeringQueue.Snapshot())
        {
            QueuedBar.Children.Add(BuildQueuedChip(item));
        }
        QueuedBar.IsVisible = QueuedBar.Children.Count > 0;
    }

    private Border BuildQueuedChip(SteeringMessage item)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(MakeIcon("Icon.timer", "VelaAccent", 10));
        row.Children.Add(new TextBlock
        {
            Classes = { "refChipText" },
            // 多行插话在芯片上压成一行:芯片是"还有这么一条排着"的提示,不是预览窗
            Text = Truncate(FileReference.Shorten(item.RawText).ReplaceLineEndings(" "), MaxQueuedChipChars),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        var chip = new Border
        {
            Classes = { "refChip" },
            Child = row,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(chip, $"{item.DisplayText}\n\n{_loc["QueuedTip"]}");
        chip.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (SteeringQueue.Remove(item))
            {
                RenderQueuedChips();
            }
        };
        return chip;
    }
}
