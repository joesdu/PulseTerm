using Avalonia.Controls;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 上下文压缩的面板侧接线:发送前检查是否该压,压完在消息流里留一道可展开的分隔条。
/// 算法本身在 <see cref="ContextCompactor" />(不碰 UI,可单测)。
/// </summary>
public partial class ChatPanelView
{
    /// <summary>当前的滚动摘要(空 = 还没压过)。它代表 <see cref="_summarizedThrough" /> 之前的那些消息。</summary>
    private string _contextSummary = "";

    /// <summary>摘要覆盖到 <c>_history</c> 的哪个下标为止(不含)。</summary>
    private int _summarizedThrough;

    private void ResetCompaction()
    {
        _contextSummary = "";
        _summarizedThrough = 0;
    }

    /// <summary>
    /// 发送前调用:接近窗口就先把早期对话折成摘要。
    /// 压缩失败(网络问题、模型不配合)不抛也不拦 —— 装配那一步还有"直接丢最早几条"兜底。
    /// </summary>
    private async Task CompactIfNeededAsync(ResolvedModel provider, CancellationToken cancellationToken)
    {
        if (!_settings.CompactContext
            || !ContextCompactor.ShouldCompact(_history, _summarizedThrough, _contextSummary,
                provider.MaxInputTokens, provider.MaxTokens))
        {
            return;
        }
        int cut = ContextCompactor.PlanCutPoint(_history, _summarizedThrough, provider.MaxInputTokens, provider.MaxTokens);
        if (cut <= _summarizedThrough)
        {
            return;
        }
        try
        {
            StatusText.Text = _loc["Compacting"];
            // 裸客户端:压缩这一问不该带工具,也不该进对话历史
            IChatClient client = await _store.CreateClientAsync(provider, cancellationToken: cancellationToken);
            CompactionResult? result = await Task.Run(
                () => ContextCompactor.CompactAsync(client, _history, _summarizedThrough, _contextSummary, cut,
                    _context.Host.Locale, cancellationToken), cancellationToken);
            if (result is not { } compaction)
            {
                return;
            }

            _contextSummary = compaction.Summary;
            _summarizedThrough = compaction.Through;
            // 压缩自身的用量也算进累计(是真花的钱),但不动"上一轮上下文"那个读数
            if (compaction.Usage is { } usage)
            {
                _totalInputTokens += usage.InputTokenCount ?? 0;
                _totalOutputTokens += usage.OutputTokenCount ?? 0;
            }
            await _historyStore.SaveSummaryAsync(_conversationId, _contextSummary, _summarizedThrough, cancellationToken);
            ShowCompactionMarker(compaction.FoldedMessages);
            UpdateUsageText();
        }
        catch (OperationCanceledException)
        {
            throw; // 用户按了停止,交给外层统一收尾
        }
        catch (Exception ex)
        {
            // 压不动就照常发:装配那一步会按窗口丢掉最早几条,不至于卡住对话
            _context.Log.Warn($"Context compaction failed, falling back to trimming: {ex.Message}");
        }
        finally
        {
            StatusText.Text = "";
        }
    }

    /// <summary>
    /// 在消息流里放一道"已压缩"分隔条,点开能读到摘要原文。
    /// 压缩必须看得见 —— 模型从此刻起看到的是摘要而不是原文,用户有权知道这件事。
    /// </summary>
    private void ShowCompactionMarker(int foldedMessages)
    {
        var collapsible = new Collapsible(this, _loc.F("Compacted", foldedMessages),
            iconKey: "AiIcon.scissors", iconBrushKey: "VelaAccent");
        collapsible.SetBody(_contextSummary);
        var host = new Border { Classes = { "compactionMarker" }, Child = collapsible.Root };
        MessagesPanel.Children.Add(host);
        RequestAutoScroll(force: true);
    }

    /// <summary>载入历史会话时把上次压缩的结果一并恢复,免得一进来就重压一次。</summary>
    private async Task RestoreSummaryAsync()
    {
        (string summary, int through) = await _historyStore.LoadSummaryAsync(_conversationId);
        // 摘要覆盖范围不能超过实际条数(会话被编辑/删除截断过就会这样),对不上就整个作废
        _contextSummary = through > 0 && through <= _history.Count ? summary : "";
        _summarizedThrough = _contextSummary.Length > 0 ? through : 0;
    }
}
