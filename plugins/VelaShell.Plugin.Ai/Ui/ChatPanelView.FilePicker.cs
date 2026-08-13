using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 聊天面板的 <c>@</c> 文件引用部分:在输入框里键入 <c>@</c> 即列出所选会话的远端目录,
/// 上下键选择、回车/Tab 确认、目录可继续下钻;发送时把被引用文件的内容随消息一并送给模型。
/// 读取走 SFTP(<see cref="VelaShell.PluginSdk.RemoteFs.IRemoteFsApi" />),
/// 写回/编辑由 Agent 模式的 <c>write_remote_file</c> 工具负责(需审批)。
/// </summary>
public partial class ChatPanelView
{
    /// <summary>单条消息最多附带的文件数。</summary>
    private const int MaxAttachedFiles = 5;

    /// <summary>单个附带文件的读取上限。</summary>
    private const int MaxAttachBytes = 128 * 1024;

    /// <summary>候选列表最多显示多少条。</summary>
    private const int MaxCandidates = 50;

    private readonly List<RemoteFileEntry> _fileCandidates = [];
    private int _fileIndex;
    private int _fileTokenStart = -1;
    private bool _pickerSuspended;
    private CancellationTokenSource? _fileCts;
    private string? _cwdSessionId;
    private string _cwd = "";

    // ---------- 触发与候选列表 ----------

    private void OnInputTextChanged()
    {
        if (!_pickerSuspended)
        {
            _ = UpdateFilePickerAsync();
        }
    }

    /// <summary>按光标处的 <c>@token</c> 刷新候选列表;不在引用里就收起弹层。</summary>
    private async Task UpdateFilePickerAsync()
    {
        string text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        if (!FileReference.TryFindToken(text, caret, out int start, out _, out string reference))
        {
            CloseFilePicker();
            return;
        }
        if (SelectedSessionId is not { } sessionId)
        {
            CloseFilePicker();
            return;
        }
        _fileTokenStart = start;

        _fileCts?.Cancel();
        _fileCts?.Dispose();
        _fileCts = new();
        CancellationToken cancellationToken = _fileCts.Token;
        try
        {
            string cwd = await ResolveWorkingDirectoryAsync(sessionId, cancellationToken);
            (string directory, string filter) = FileReference.Split(reference, cwd);
            IReadOnlyList<RemoteFileEntry> entries = await _context.RemoteFs
                .ListDirectoryAsync(sessionId, directory, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            _fileCandidates.Clear();
            _fileCandidates.AddRange(entries
                .Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCandidates));
            _fileIndex = 0;
            RenderFileCandidates(directory);
        }
        catch (OperationCanceledException)
        {
            // 输入还在继续,这次列目录作废
        }
        catch (Exception ex)
        {
            _fileCandidates.Clear();
            FileList.Children.Clear();
            FilePopupHeader.Text = $"{_loc["Error"]}: {ex.Message}";
            FilePopup.IsOpen = true;
        }
    }

    private void RenderFileCandidates(string directory)
    {
        FileList.Children.Clear();
        FilePopupHeader.Text = _fileCandidates.Count == 0
            ? _loc.F("FilePickerEmpty", directory)
            : _loc.F("FilePickerHeader", directory);
        for (int i = 0; i < _fileCandidates.Count; i++)
        {
            FileList.Children.Add(BuildCandidateRow(_fileCandidates[i], i));
        }
        FilePopup.IsOpen = true;
        // 弹层不接管键盘:万一它抢到了焦点,立刻还给输入框,否则用户就打不了字了
        if (!InputBox.IsFocused)
        {
            InputBox.Focus();
        }
        HighlightCandidate();
    }

    private Border BuildCandidateRow(RemoteFileEntry entry, int index)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Viewbox icon = MakeIcon(entry.IsDirectory ? "Icon.folder" : "Icon.file",
            entry.IsDirectory ? "VelaAccent" : "VelaTextMuted", 11);
        icon.Margin = new Thickness(0, 0, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Classes = { "fileName" },
            Text = entry.IsDirectory ? entry.Name + "/" : entry.Name
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        if (!entry.IsDirectory)
        {
            var size = new TextBlock { Classes = { "dim" }, Text = FormatSize(entry.Size), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(size, 2);
            row.Children.Add(size);
        }

        var border = new Border { Classes = { "fileRow" }, Child = row, Tag = index };
        border.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            AcceptCandidate(index);
        };
        return border;
    }

    private void HighlightCandidate()
    {
        for (int i = 0; i < FileList.Children.Count; i++)
        {
            if (FileList.Children[i] is not Border row)
            {
                continue;
            }
            bool selected = i == _fileIndex;
            if (selected && !row.Classes.Contains("selected"))
            {
                row.Classes.Add("selected");
            }
            else if (!selected)
            {
                row.Classes.Remove("selected");
            }
            if (selected)
            {
                row.BringIntoView();
            }
        }
    }

    /// <summary>弹层开着时的按键:返回 true 表示这次按键归弹层。</summary>
    private bool HandleFilePickerKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up when _fileCandidates.Count > 0:
                _fileIndex = e.Key == Key.Down
                    ? (_fileIndex + 1) % _fileCandidates.Count
                    : (_fileIndex - 1 + _fileCandidates.Count) % _fileCandidates.Count;
                HighlightCandidate();
                e.Handled = true;
                return true;
            case Key.Enter or Key.Tab when _fileCandidates.Count > 0:
                AcceptCandidate(_fileIndex);
                e.Handled = true;
                return true;
            case Key.Escape:
                CloseFilePicker();
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 选中一个候选:把 <c>@token</c> 整段换成候选的完整路径。
    /// 目录 → 补 <c>/</c> 并继续列出下一层;文件 → 补一个空格收尾并收起弹层。
    /// 路径含空格时改用 <c>@"..."</c> 形式,引号在目录下钻期间保持敞开。
    /// </summary>
    private void AcceptCandidate(int index)
    {
        if (index < 0 || index >= _fileCandidates.Count || _fileTokenStart < 0)
        {
            return;
        }
        RemoteFileEntry entry = _fileCandidates[index];
        string text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        if (_fileTokenStart >= caret)
        {
            return;
        }
        string path = entry.IsDirectory ? entry.FullPath.TrimEnd('/') + "/" : entry.FullPath;
        bool quote = FileReference.NeedsQuoting(path);
        string replacement = quote
            ? entry.IsDirectory ? $"@\"{path}" : $"@\"{path}\" "
            : entry.IsDirectory ? $"@{path}" : $"@{path} ";

        _pickerSuspended = true;
        try
        {
            InputBox.Text = string.Concat(text.AsSpan(0, _fileTokenStart), replacement, text.AsSpan(caret));
            InputBox.CaretIndex = _fileTokenStart + replacement.Length;
        }
        finally
        {
            _pickerSuspended = false;
        }
        if (entry.IsDirectory)
        {
            _ = UpdateFilePickerAsync(); // 继续下钻
        }
        else
        {
            CloseFilePicker();
        }
    }

    private void CloseFilePicker()
    {
        _fileCts?.Cancel();
        FilePopup.IsOpen = false;
        _fileCandidates.Clear();
        _fileTokenStart = -1;
    }

    private async Task<string> ResolveWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_cwdSessionId == sessionId && _cwd.Length > 0)
        {
            return _cwd;
        }
        try
        {
            _cwd = await _context.RemoteFs.GetWorkingDirectoryAsync(sessionId, cancellationToken);
        }
        catch
        {
            _cwd = "/";
        }
        _cwdSessionId = sessionId;
        return _cwd;
    }

    // ---------- 发送时展开 ----------

    /// <summary>
    /// 把消息里 <c>@</c> 引用的文件读出来附在消息后面(给模型看的那一份)。
    /// 返回展开后的文本与成功附带的路径;读失败/二进制/超限都以文字说明,不打断发送。
    /// </summary>
    private async Task<(string ModelText, IReadOnlyList<string> Attached)> ResolveAttachmentsAsync(
        string text, CancellationToken cancellationToken)
    {
        List<string> references = FileReference.Parse(text);
        if (references.Count == 0)
        {
            return (text, []);
        }
        if (SelectedSessionId is not { } sessionId)
        {
            return (text + $"\n\n[{_loc["NoSession"]}]", []);
        }
        string cwd = await ResolveWorkingDirectoryAsync(sessionId, cancellationToken);
        var builder = new StringBuilder(text);
        var attached = new List<string>();
        builder.Append("\n\n").Append(_loc["AttachIntro"]);
        foreach (string reference in references.Take(MaxAttachedFiles))
        {
            string path = FileReference.Expand(reference, cwd);
            try
            {
                byte[] bytes = await _context.RemoteFs.ReadAllBytesAsync(sessionId, path, MaxAttachBytes, cancellationToken);
                if (FileReference.LooksBinary(bytes))
                {
                    builder.Append($"\n\n===== {path} =====\n[{_loc["AttachBinary"]}]");
                    continue;
                }
                builder.Append($"\n\n===== {path} =====\n```\n")
                       .Append(Encoding.UTF8.GetString(bytes))
                       .Append("\n```");
                attached.Add(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                builder.Append($"\n\n===== {path} =====\n[{_loc["AttachFailed"]}: {ex.Message}]");
            }
        }
        if (references.Count > MaxAttachedFiles)
        {
            builder.Append($"\n\n[{_loc.F("AttachLimit", MaxAttachedFiles)}]");
        }
        return (builder.ToString(), attached);
    }

    /// <summary>在消息流里补一张"已附带 N 个文件"的小卡片。</summary>
    private void AddAttachmentCard(IReadOnlyList<string> paths)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Classes = { "dim" }, Text = _loc.F("AttachedCount", paths.Count) });
        foreach (string path in paths)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(MakeIcon("Icon.file-text", "VelaTextMuted", 11));
            row.Children.Add(new TextBlock { Classes = { "fileName" }, Text = path });
            stack.Children.Add(row);
        }
        MessagesPanel.Children.Add(new Border { Classes = { "toolCard" }, Child = stack });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
    };
}
