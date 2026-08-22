using System.Diagnostics.CodeAnalysis;
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
/// 读取走 SFTP(<see cref="IRemoteFsApi" />),
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

    /// <summary>连打时先攒一下再列目录(毫秒):一次停顿只发一次 SFTP 请求。</summary>
    private const int PickerDebounceMs = 180;

    /// <summary>目录列举结果的缓存寿命(毫秒):够一次连续补全用,又不至于一直看着旧目录。</summary>
    private const long DirectoryCacheTtlMs = 5000;

    /// <summary>缓存的目录数上限(超了整体清空:补全过程只会用到最近那几个目录)。</summary>
    private const int MaxCachedDirectories = 32;

    private readonly List<RemoteFileEntry> _fileCandidates = [];

    /// <summary>目录列举缓存,键 = 会话 + 目录。仅用于补全期间的过滤,带 TTL。</summary>
    private readonly Dictionary<(string Session, string Directory), (IReadOnlyList<RemoteFileEntry> Entries, long At)>
        _directoryCache = [];

    private int _fileIndex;
    private int _fileTokenStart = -1;
    private bool _pickerSuspended;

    /// <summary>
    /// 面板生命周期内唯一的取消源:只在 <see cref="Detach" />(面板真的关了)时取消。
    /// </summary>
    /// <remarks>
    /// 曾经这里是"每敲一个键就取消上一次、另起一个",于是长路径退格时每个字符都要取消一次
    /// 正在飞的 SFTP 列目录 —— 一次取消要沿十来层异步栈回卷,既刷屏(调试器里每层报一次
    /// first-chance 异常)又实打实地卡输入。现在改为【代次判废】:新请求只把
    /// <see cref="_pickerGeneration" /> 加一,旧请求跑完自己发现过期就安静丢弃结果
    /// (结果仍进缓存,不浪费),不再互相取消。
    /// </remarks>
    private CancellationTokenSource? _fileCts;

    /// <summary>补全请求代次:只有最新一次的结果准许上屏。</summary>
    private int _pickerGeneration;

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
    /// <param name="immediate">
    /// 跳过防抖:补全落定后继续下钻目录时用 —— 那是一次明确的用户动作,不该再等一拍。
    /// </param>
    private async Task UpdateFilePickerAsync(bool immediate = false)
    {
        string text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretOffset, 0, text.Length);
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
        int generation = ++_pickerGeneration;

        _fileCts ??= new();
        CancellationToken cancellationToken = _fileCts.Token;
        try
        {
            string cwd = await ResolveWorkingDirectoryAsync(sessionId, cancellationToken);
            (string directory, string filter) = FileReference.Split(reference, cwd);

            // 只是过滤词变了(用户在同一个目录里接着敲文件名)——缓存里就有,本地筛,不碰网络。
            if (TryGetCachedEntries(sessionId, directory, out IReadOnlyList<RemoteFileEntry>? cached))
            {
                ShowCandidates(cached, filter, directory);
                return;
            }
            if (!immediate)
            {
                // 防抖:连打时每一键都会走到这儿,但只有最后一键能过下面这道代次检查。
                await Task.Delay(PickerDebounceMs);
                if (generation != _pickerGeneration)
                {
                    return;
                }
            }
            IReadOnlyList<RemoteFileEntry> entries = await _context.RemoteFs
                .ListDirectoryAsync(sessionId, directory, cancellationToken);
            CacheEntries(sessionId, directory, entries);
            if (generation != _pickerGeneration)
            {
                return; // 已被后来的输入顶掉:结果留在缓存里,但不上屏
            }
            ShowCandidates(entries, filter, directory);
        }
        catch (OperationCanceledException)
        {
            // 面板已关闭(Detach):这次列目录作废
        }
        catch (Exception ex)
        {
            if (generation != _pickerGeneration)
            {
                return;
            }
            _fileCandidates.Clear();
            FileList.Children.Clear();
            FilePopupHeader.Text = $"{_loc["Error"]}: {ex.Message}";
            FilePopup.IsOpen = true;
        }
    }

    /// <summary>按过滤词筛出候选并上屏(目录在前、同类按名排)。</summary>
    private void ShowCandidates(IReadOnlyList<RemoteFileEntry> entries, string filter, string directory)
    {
        _fileCandidates.Clear();
        _fileCandidates.AddRange(entries
            .Where(e => filter.Length == 0 || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates));
        _fileIndex = 0;
        RenderFileCandidates(directory);
    }

    private bool TryGetCachedEntries(string sessionId, string directory,
        [NotNullWhen(true)] out IReadOnlyList<RemoteFileEntry>? entries)
    {
        entries = null;
        if (!_directoryCache.TryGetValue((sessionId, directory), out (IReadOnlyList<RemoteFileEntry> Entries, long At) hit))
        {
            return false;
        }
        if (Environment.TickCount64 - hit.At > DirectoryCacheTtlMs)
        {
            _directoryCache.Remove((sessionId, directory));
            return false;
        }
        entries = hit.Entries;
        return true;
    }

    private void CacheEntries(string sessionId, string directory, IReadOnlyList<RemoteFileEntry> entries)
    {
        if (_directoryCache.Count >= MaxCachedDirectories)
        {
            _directoryCache.Clear();
        }
        _directoryCache[(sessionId, directory)] = (entries, Environment.TickCount64);
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
        if (!InputBox.TextArea.IsFocused)
        {
            InputBox.TextArea.Focus();
        }
        HighlightCandidate();
    }

    private Border BuildCandidateRow(RemoteFileEntry entry, int index)
    {
        var row = new Grid { ColumnDefinitions = [with("Auto,*,Auto")] };
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
        int caret = Math.Clamp(InputBox.CaretOffset, 0, text.Length);
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
            InputBox.CaretOffset = _fileTokenStart + replacement.Length;
        }
        finally
        {
            _pickerSuspended = false;
        }
        if (entry.IsDirectory)
        {
            _ = UpdateFilePickerAsync(immediate: true); // 继续下钻:用户刚点了确认,别再等防抖
        }
        else
        {
            CloseFilePicker();
        }
    }

    /// <summary>
    /// 收起弹层。只判废在飞的补全请求(代次加一),<b>不</b>取消 <see cref="_fileCts" /> ——
    /// 那是面板级的,取消掉后面就没得用了;况且每敲一个非引用字符都会走到这里。
    /// </summary>
    private void CloseFilePicker()
    {
        _pickerGeneration++;
        FilePopup.IsOpen = false;
        _fileCandidates.Clear();
        _fileTokenStart = -1;
    }

    /// <summary>
    /// 退格/删除键落在一条<b>已完成</b>的 <c>@</c> 引用边上时,整块一起删。
    /// 返回 true 表示这次按键已被接管。
    /// </summary>
    /// <remarks>
    /// 选中的文件在输入框里是一整块(Claude Code / OpenCode 里那枚芯片的等价物),
    /// 删就整块删:一次退格 = 一次补全刷新(而且刷新后已不在引用里,连目录都不用列),
    /// 而不是像以前那样一个字符一次、每次都发一趟 SFTP。
    /// </remarks>
    private bool HandleReferenceBlockDelete(KeyEventArgs e)
    {
        if (e.Key is not (Key.Back or Key.Delete) || e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }
        string text = InputBox.Text ?? "";
        if (text.Length == 0 || InputBox.SelectionLength > 0)
        {
            return false; // 有选区时按常规删选区
        }
        int caret = Math.Clamp(InputBox.CaretOffset, 0, text.Length);
        int start, end;
        if (e.Key == Key.Back)
        {
            if (!FileReference.TryFindCompletedReferenceBefore(text, caret, out start))
            {
                return false;
            }
            end = caret;
        }
        else
        {
            // 前向删除:光标停在块左边时同样整块删(与退格对称)
            int blockEnd = FindCompletedReferenceEnd(text, caret);
            if (blockEnd < 0)
            {
                return false;
            }
            start = caret;
            end = blockEnd;
        }
        _pickerSuspended = true;
        try
        {
            InputBox.Text = string.Concat(text.AsSpan(0, start), text.AsSpan(end));
            InputBox.CaretOffset = start;
        }
        finally
        {
            _pickerSuspended = false;
        }
        CloseFilePicker();
        e.Handled = true;
        return true;
    }

    /// <summary>光标处若正好起头一条已完成引用,返回它的右边界(含尾空格);否则 -1。</summary>
    private static int FindCompletedReferenceEnd(string text, int caret)
    {
        if (caret >= text.Length || text[caret] != '@')
        {
            return -1;
        }
        for (int end = caret + 1; end <= text.Length; end++)
        {
            if (FileReference.TryFindCompletedReferenceBefore(text, end, out int start) && start == caret)
            {
                // 引号形态先在闭引号处就命中,尾空格属于这块,一并带走
                return end < text.Length && text[end] == ' ' ? end + 1 : end;
            }
            if (end < text.Length && text[end] == '\n')
            {
                return -1;
            }
        }
        return -1;
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
    /// 返回展开后的文本、成功附带的路径,以及<b>没能读到</b>的路径;读失败/二进制/超限
    /// 都以文字说明,不打断发送。
    /// </summary>
    /// <remarks>
    /// 单独回报读失败的那几个,是因为气泡里已经用芯片列出了引用的文件(Copilot 的做法),
    /// 再补一张"已附带 N 个文件"的卡片纯属重复;真正需要提醒用户的只有"这个没读到"。
    /// </remarks>
    private async Task<(string ModelText, IReadOnlyList<string> Attached, IReadOnlyList<string> Failed)>
        ResolveAttachmentsAsync(string text, CancellationToken cancellationToken)
    {
        List<string> references = FileReference.Parse(text);
        if (references.Count == 0)
        {
            return (text, [], []);
        }
        if (SelectedSessionId is not { } sessionId)
        {
            return (text + $"\n\n[{_loc["NoSession"]}]", [], references);
        }
        string cwd = await ResolveWorkingDirectoryAsync(sessionId, cancellationToken);
        var builder = new StringBuilder(text);
        var attached = new List<string>();
        var failed = new List<string>();
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
                failed.Add(path);
            }
        }
        if (references.Count > MaxAttachedFiles)
        {
            builder.Append($"\n\n[{_loc.F("AttachLimit", MaxAttachedFiles)}]");
            failed.AddRange(references.Skip(MaxAttachedFiles).Select(r => FileReference.Expand(r, cwd)));
        }
        return (builder.ToString(), attached, failed);
    }

    /// <summary>没读到的引用在消息流里补一行提示(读到的已经在气泡的芯片里了,不再重复报)。</summary>
    private void AddAttachmentFailureNote(IReadOnlyList<string> paths)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Classes = { "dim" },
            Text = _loc.F("AttachFailedList", string.Join(", ", paths.Select(FileReference.DisplayName)))
        });
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
