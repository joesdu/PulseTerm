using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>历史会话列表上的三件小事:按标题搜索、重命名、导出 Markdown。</summary>
public partial class ChatPanelView
{
    /// <summary>已取回的会话摘要(搜索是本地筛,不重复查库)。</summary>
    private IReadOnlyList<ChatSessionSummary> _historySessions = [];

    /// <summary>按当前搜索词过滤后重画列表。</summary>
    private void RenderHistoryList()
    {
        string filter = HistorySearchBox.Text?.Trim() ?? "";
        List<ChatSessionSummary> shown = filter.Length == 0
            ? [.. _historySessions]
            : [.. _historySessions.Where(s => s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        HistoryList.Children.Clear();
        HistoryHeader.Text = _historySessions.Count == 0
            ? _loc["NoHistory"]
            : filter.Length > 0
                ? _loc.F("HistoryFiltered", shown.Count, _historySessions.Count)
                : _loc.F("HistoryCount", _historySessions.Count);
        ClearHistoryButton.IsVisible = _historySessions.Count > 0;
        foreach (ChatSessionSummary session in shown)
        {
            HistoryList.Children.Add(BuildHistoryRow(session));
        }
    }

    /// <summary>把某个会话改名:标题就地变成输入框,回车确认、失焦或 Esc 取消。</summary>
    private void BeginRename(ChatSessionSummary session, TextBlock titleText)
    {
        var editor = new TextBox
        {
            Text = session.Title,
            FontSize = titleText.FontSize,
            MinHeight = 22,
            Padding = new Avalonia.Thickness(4, 0)
        };
        if (titleText.Parent is not Panel host)
        {
            return;
        }
        int index = host.Children.IndexOf(titleText);
        host.Children[index] = editor;
        editor.Focus();
        editor.SelectAll();

        bool done = false;
        async Task FinishAsync(bool commit)
        {
            if (done)
            {
                return;
            }
            done = true;
            if (commit && editor.Text is { } name && name.Trim().Length > 0 && name.Trim() != session.Title)
            {
                await _historyStore.RenameAsync(session.Id, name, session.CreatedAt, session.MessageCount);
            }
            await RefreshHistoryListAsync();
        }

        editor.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                await FinishAsync(commit: true);
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                await FinishAsync(commit: false);
            }
        };
        editor.LostFocus += async (_, _) => await FinishAsync(commit: true);
    }

    /// <summary>把一个会话导出成 Markdown 文件(含思考与工具调用 —— 那才是完整的排查记录)。</summary>
    private async Task ExportAsync(ChatSessionSummary session)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        try
        {
            IReadOnlyList<ChatEntry> entries = await _historyStore.LoadAsync(session.Id);
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _loc["Export"],
                SuggestedFileName = SafeFileName(session.Title) + ".md",
                DefaultExtension = "md"
            });
            if (file is null)
            {
                return;
            }
            await using Stream stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(BuildMarkdown(session, entries));
            StatusText.Text = _loc["Exported"];
        }
        catch (Exception ex)
        {
            _context.Log.Error("Exporting conversation failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private string BuildMarkdown(ChatSessionSummary session, IReadOnlyList<ChatEntry> entries)
    {
        var md = new StringBuilder();
        md.Append("# ").AppendLine(session.Title.Length > 0 ? session.Title : _loc["Untitled"]);
        md.AppendLine();
        md.Append("> ").Append(session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
          .Append(" · ").AppendLine(_loc.F("MessageCount", session.MessageCount));
        md.AppendLine();

        foreach (ChatEntry entry in entries)
        {
            bool user = entry.Role == "user";
            md.Append("## ").AppendLine(user ? _loc["You"] : _loc["AssistantRole"]);
            md.AppendLine();
            if (!user && entry.Meta is { } meta)
            {
                if (meta.Model.Length > 0)
                {
                    md.Append("`").Append(meta.Model).Append('`');
                    if (meta.ElapsedMs > 0)
                    {
                        md.Append(" · ").Append(FormatDuration(TimeSpan.FromMilliseconds(meta.ElapsedMs)));
                    }
                    md.AppendLine().AppendLine();
                }
                if (meta.Thinking.Length > 0)
                {
                    md.Append("<details><summary>").Append(_loc["Thinking"]).AppendLine("</summary>").AppendLine();
                    md.AppendLine("```").AppendLine(meta.Thinking).AppendLine("```").AppendLine();
                    md.AppendLine("</details>").AppendLine();
                }
                foreach (ChatToolCall tool in meta.Tools ?? [])
                {
                    md.Append("<details><summary>🔧 ").Append(tool.Name).AppendLine("</summary>").AppendLine();
                    md.AppendLine("```json").AppendLine(tool.Arguments).AppendLine("```");
                    md.AppendLine("```").AppendLine(tool.Result).AppendLine("```").AppendLine();
                    md.AppendLine("</details>").AppendLine();
                }
            }
            md.AppendLine(entry.Text).AppendLine();
        }
        return md.ToString();
    }

    /// <summary>把标题清成能当文件名的样子。</summary>
    private static string SafeFileName(string title)
    {
        string name = string.Concat((title.Length > 0 ? title : "chat")
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return name.Length > 60 ? name[..60] : name;
    }
}
