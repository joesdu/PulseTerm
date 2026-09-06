using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Search;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.Services.Syntax;

namespace VelaShell.Views;

/// <summary>
/// SFTP「打开」的内置快速文本编辑器(AvaloniaEdit)。文件已由 FileBrowserViewModel 下载到
/// 本地临时副本;保存 = 按原编码写回临时文件 + 通过回调上传到服务器。窗口关闭时删除临时副本。
/// </summary>
public partial class RemoteFileEditorView : Window
{
    /// <summary>超过这个大小就不再加载进编辑器(内置编辑器面向的是配置文件,不是日志)。</summary>
    private const long LargeFileThresholdBytes = 5L * 1024 * 1024;

    private readonly string _localPath = string.Empty;
    private readonly Func<Task>? _uploadAsync;

    /// <summary>编辑修订号:每次内容变化 +1。</summary>
    /// <remarks>
    /// 脏状态曾经是一个 <c>bool</c>,保存成功就无条件清掉 —— 而"保存"是<b>异步</b>的:
    /// 保存文本 A(上传要几秒)→ 期间接着敲出 B → A 上传成功 → <c>_dirty = false</c>,
    /// B 于是被当成已保存,关窗不再提示,改动直接没了。改成修订号之后,上传成功只确认
    /// <b>它自己那一份快照的号</b>,号在此期间又涨过就仍然是脏的。
    /// </remarks>
    private long _revision;

    /// <summary>已确认落到远端的修订号。<c>_revision</c> 与它相等即为干净。</summary>
    private long _savedRevision;

    private Encoding _encoding = new UTF8Encoding(false);

    /// <summary>UTF-8 严格解码失败时的回落编码(当前会话的终端编码)。</summary>
    private readonly Encoding? _sessionEncoding;
    private bool _forceClose;
    private bool _saving;

    /// <summary>保存进行中又按了一次保存:等这一轮结束再补一轮,而不是把这次请求丢掉。</summary>
    private bool _resaveRequested;

    /// <summary>当前在跑的保存任务;关窗要等它落地(临时副本正被上传读着,不能先删)。</summary>
    private Task? _saveTask;

    /// <summary>内容是否有未落到远端的改动。</summary>
    private bool IsDirty => _revision != _savedRevision;

    /// <summary>脏状态(回归用例读它:"保存 A 期间敲出 B" 之后必须仍为脏)。</summary>
    internal bool IsDirtyForTest => IsDirty;

    /// <summary>发起一次保存并把任务交出去,便于回归用例按受控顺序推进。</summary>
    internal Task SaveForTestAsync()
    {
        BeginSave();
        return _saveTask ?? Task.CompletedTask;
    }

    /// <summary>
    /// 供设计器/XAML 使用的无参构造函数。
    /// </summary>
    public RemoteFileEditorView() => InitializeComponent();

    /// <summary>
    /// 创建编辑器窗口并加载本地临时副本内容。
    /// </summary>
    /// <param name="fileName">文件名,用于窗口标题显示。</param>
    /// <param name="remotePath">文件在服务器上的远程路径。</param>
    /// <param name="localPath">已下载到本地的临时副本路径。</param>
    /// <param name="uploadAsync">保存时用于将临时文件上传回服务器的回调。</param>
    /// <param name="sessionEncoding">
    /// UTF-8 严格解码失败时的回落编码(当前会话的终端编码);null 表示仍按 UTF-8 处理。
    /// </param>
    public RemoteFileEditorView(
        string fileName,
        string remotePath,
        string localPath,
        Func<Task> uploadAsync,
        Encoding? sessionEncoding = null)
        : this()
    {
        _localPath = localPath;
        _uploadAsync = uploadAsync;
        _sessionEncoding = sessionEncoding;
        Title = fileName;
        TitleText.Text = fileName;
        PathText.Text = remotePath;
        // AvaloniaEdit 自带查找/替换面板(Ctrl+F / Ctrl+H),只是默认没装。
        // 编辑远端配置时"找一处改一处"是最常做的事,没有它只能靠肉眼翻。
        SearchPanel.Install(Editor);
        _ = LoadFileAsync();
        Editor.TextChanged += (_, _) =>
        {
            _revision++;
            StatusText.Text = Strings.Get("Editor_Unsaved");
        };
    }

    private async Task LoadFileAsync()
    {
        // 磁盘读取放后台线程:同步 ReadAllBytes 在构造(UI 线程)里读大文件会卡住
        // 窗口打开;读完回 UI 线程装配编辑器。修订号基线取在赋值之后(赋值自己也会
        // 触发一次 TextChanged)。
        // 保留原文件的 BOM/编码:UTF-8(无 BOM)为缺省,识别 UTF-8 BOM 与 UTF-16 LE/BE。
        byte[] bytes;
        // 读盘期间锁住编辑器。窗口是先显示、后台再读文件的,这中间用户完全可以开始打字,
        // 而读完那一下 `Editor.Text = …` 会把敲进去的内容整段冲掉 —— 且不留痕迹。
        // 读失败时**保持只读**:内容压根没进来,让人编辑再保存等于拿空白覆盖远端文件。
        Editor.IsReadOnly = true;
        try
        {
            // 大文件保护:整份读进内存 + 交给 AvaloniaEdit 建文档,几十 MB 的日志能把
            // 窗口卡住半分钟,而"用内置编辑器打开一个 200MB 的 access.log"多半是误点。
            // 超过阈值就只提示、不加载,让用户改用外部编辑器。
            var info = new FileInfo(_localPath);
            if (info.Exists && info.Length > LargeFileThresholdBytes)
            {
                Editor.IsReadOnly = true;
                StatusText.Text = Strings.Format(
                    "Editor_TooLarge",
                    (info.Length / (1024.0 * 1024)).ToString("F1"),
                    (LargeFileThresholdBytes / (1024.0 * 1024)).ToString("F0"));
                return;
            }
            bytes = await Task.Run(() => File.ReadAllBytes(_localPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
            return;
        }
        EditorEncodingDetector.Result detected = EditorEncodingDetector.Detect(bytes, _sessionEncoding);
        _encoding = detected.Encoding;
        Editor.Text = EditorEncodingDetector.Decode(bytes, detected);
        // 赋值本身会触发 TextChanged 把修订号推上去,所以基线在赋值之后才能取。
        _savedRevision = _revision;
        Editor.IsReadOnly = false;
        ApplySyntaxHighlighting();
        StatusText.Text = detected.FellBackToSessionEncoding
            // 明说回落到了哪个编码:猜错时用户得看得见,才知道该怎么办
            // (而不是保存之后才发现整篇中文变成了 �)。
            ? Strings.Format("Editor_LoadedStatusFallback", bytes.Length.ToString("N0"), _encoding.WebName)
            : Strings.Format("Editor_LoadedStatus", bytes.Length.ToString("N0"));
    }

    /// <summary>
    /// 按文件类型着色。类型判定要用**远端文件名**而不是本地临时副本的名字 ——
    /// 临时副本可能没有扩展名。首行同时交给判定器,以便识别没有扩展名的脚本
    /// (服务器上 /usr/local/bin 下大量如此),那种情况只有 shebang 能说明它是什么。
    /// </summary>
    private void ApplySyntaxHighlighting()
    {
        try
        {
            Editor.SyntaxHighlighting = SyntaxHighlightingService.Resolve(
                Title, FirstLineOf(Editor.Text), ActualThemeVariant);
        }
        catch (Exception)
        {
            // 高亮只是锦上添花:定义有问题时退化为纯文本,绝不能让文件打不开。
            Editor.SyntaxHighlighting = null;
        }
    }

    private static string? FirstLineOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        int end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }


    /// <summary>发起一次保存并记住这个任务(关窗要等它)。</summary>
    private void BeginSave() => _saveTask = SaveAsync();

    private async Task SaveAsync()
    {
        if (_uploadAsync is null)
        {
            return;
        }
        if (_saving)
        {
            // 上传途中又按了保存:记下来,等这轮走完再补一轮。直接返回等于把这次
            // Ctrl+S 静静吃掉 —— 用户以为存上了,其实没有。
            _resaveRequested = true;
            return;
        }
        _saving = true;
        try
        {
            do
            {
                _resaveRequested = false;
                // 快照与它的修订号必须在同一刻取:上传成功后只确认这一号,
                // 期间敲出来的新内容仍然算脏。
                long snapshot = _revision;
                string text = Editor.Text;
                StatusText.Text = Strings.Get("Editor_Saving");
                try
                {
                    await File.WriteAllTextAsync(_localPath, text, _encoding);
                    await _uploadAsync();
                }
                catch (Exception ex)
                {
                    StatusText.Text = Strings.Format("Editor_SaveFailed", ex.Message);
                    return; // 失败不自动重试:改动还在编辑器里,由用户决定下一步。
                }
                _savedRevision = snapshot;
                StatusText.Text = IsDirty
                    // 存上的是快照那一份,而之后又改过 —— 状态栏必须照实说"还有未保存的"。
                    ? Strings.Get("Editor_Unsaved")
                    : Strings.Format("Editor_SavedStatus", DateTime.Now.ToString("HH:mm:ss"));
            } while (_resaveRequested);
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// 处理键盘输入:Ctrl+S 触发保存;Esc 关闭窗口(编辑器搜索面板等内层控件先消费
    /// 各自的 Esc,冒泡到这里才关窗;未保存改动由 <see cref="OnClosing" /> 确认守卫兜底)。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            BeginSave();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            // 推迟关闭:同步 Close 会让本轮按键的后续路由(KeyUp)打到已销毁的窗口刷
            // "PlatformImpl is null" 警告。OnClosing 的未保存守卫在推迟后的 Close 里照常触发。
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// 窗口关闭时若存在未保存改动,取消关闭并弹出确认丢弃对话框。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }
        if (_saving)
        {
            // 保存还在跑就关窗有两重问题:OnClosed 会删掉整个临时目录,而上传正读着
            // 那个文件;上传即便成功,结果也没人再看得到。先等它落地,再走正常的脏检查。
            e.Cancel = true;
            StatusText.Text = Strings.Get("Editor_Saving");
            _ = WaitForSaveThenCloseAsync();
            base.OnClosing(e);
            return;
        }
        if (IsDirty)
        {
            e.Cancel = true;
            _ = ConfirmDiscardAndCloseAsync();
        }
        base.OnClosing(e);
    }

    private async Task WaitForSaveThenCloseAsync()
    {
        if (_saveTask is { } task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // SaveAsync 自己已经把失败写进状态栏了;这里只是等它结束。
            }
        }
        // 推迟一拍再关:此刻仍在被取消的那次 Close 的调用栈上。
        this.PostClose();
    }

    private async Task ConfirmDiscardAndCloseAsync()
    {
        bool discard = await MessageDialog.ConfirmAsync(this, Strings.Get("Editor_UnsavedTitle"),
                           Strings.Get("Editor_UnsavedBody"), Strings.Get("Editor_DiscardAndClose"),
                           kind: MessageDialogKind.Warning, danger: true);
        if (discard)
        {
            _forceClose = true;
            Close();
        }
    }

    /// <summary>
    /// 窗口关闭后清理本地临时副本所在的独占子目录。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        // 清理本地临时副本(整个独占子目录)。
        try
        {
            string? dir = Path.GetDirectoryName(_localPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // 尽力而为;残留交给应用退出清理。
        }
        base.OnClosed(e);
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => BeginSave();

    // 推迟关闭:同步 Close 会让本轮点击的后续路由打到已销毁的窗口刷 "PlatformImpl is null"
    // 警告(见 WindowCloseExtensions)。OnClosing 的未保存守卫照常触发。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void Maximize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Header_DoubleTapped(object? sender, TappedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }
}
