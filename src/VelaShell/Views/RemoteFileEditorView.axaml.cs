using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;
using VelaShell.Services.Syntax;

namespace VelaShell.Views;

/// <summary>
/// SFTP「打开」的内置快速文本编辑器(AvaloniaEdit)。文件已由 FileBrowserViewModel 下载到
/// 本地临时副本;保存 = 按原编码写回临时文件 + 通过回调上传到服务器。窗口关闭时删除临时副本。
/// </summary>
public partial class RemoteFileEditorView : Window
{
    private readonly string _localPath = string.Empty;
    private readonly Func<Task>? _uploadAsync;
    private bool _dirty;
    private Encoding _encoding = new UTF8Encoding(false);
    private bool _forceClose;
    private bool _saving;

    /// <summary>
    /// 供设计器/XAML 使用的无参构造函数。
    /// </summary>
    public RemoteFileEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 创建编辑器窗口并加载本地临时副本内容。
    /// </summary>
    /// <param name="fileName">文件名,用于窗口标题显示。</param>
    /// <param name="remotePath">文件在服务器上的远程路径。</param>
    /// <param name="localPath">已下载到本地的临时副本路径。</param>
    /// <param name="uploadAsync">保存时用于将临时文件上传回服务器的回调。</param>
    public RemoteFileEditorView(string fileName, string remotePath, string localPath, Func<Task> uploadAsync)
        : this()
    {
        _localPath = localPath;
        _uploadAsync = uploadAsync;
        Title = fileName;
        TitleText.Text = fileName;
        PathText.Text = remotePath;
        _ = LoadFileAsync();
        Editor.TextChanged += (_, _) =>
        {
            _dirty = true;
            StatusText.Text = Strings.Get("Editor_Unsaved");
        };
    }

    private async Task LoadFileAsync()
    {
        // 磁盘读取放后台线程:同步 ReadAllBytes 在构造(UI 线程)里读大文件会卡住
        // 窗口打开;读完回 UI 线程装配编辑器。TextChanged 在读取完成后才可能触发
        // 用户编辑,先把 _dirty 复位放在赋值之后。
        // 保留原文件的 BOM/编码:UTF-8(无 BOM)为缺省,识别 UTF-8 BOM 与 UTF-16 LE/BE。
        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => File.ReadAllBytes(_localPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
            return;
        }
        _encoding = DetectEncoding(bytes);
        Editor.Text = _encoding.GetString(bytes, PreambleLength(bytes, _encoding), bytes.Length - PreambleLength(bytes, _encoding));
        _dirty = false;
        ApplySyntaxHighlighting();
        StatusText.Text = Strings.Format("Editor_LoadedStatus", bytes.Length.ToString("N0"));
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

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return new UTF8Encoding(true);
        }
        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode;
        }
        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode;
        }
        return new UTF8Encoding(false);
    }

    private static int PreambleLength(byte[] bytes, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();
        if (preamble.Length == 0 || bytes.Length < preamble.Length)
        {
            return 0;
        }
        for (int i = 0; i < preamble.Length; i++)
        {
            if (bytes[i] != preamble[i])
            {
                return 0;
            }
        }
        return preamble.Length;
    }

    private async Task SaveAsync()
    {
        if (_saving || _uploadAsync is null)
        {
            return;
        }
        _saving = true;
        StatusText.Text = Strings.Get("Editor_Saving");
        try
        {
            await File.WriteAllTextAsync(_localPath, Editor.Text, _encoding);
            await _uploadAsync();
            _dirty = false;
            StatusText.Text = Strings.Format("Editor_SavedStatus", DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            StatusText.Text = Strings.Format("Editor_SaveFailed", ex.Message);
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
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = SaveAsync();
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
        if (_dirty && !_forceClose)
        {
            e.Cancel = true;
            _ = ConfirmDiscardAndCloseAsync();
        }
        base.OnClosing(e);
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

    private void Save_Click(object? sender, RoutedEventArgs e) => _ = SaveAsync();

    // 推迟关闭:同步 Close 会让本轮点击的后续路由打到已销毁的窗口刷 "PlatformImpl is null"
    // 警告(见 WindowCloseExtensions)。OnClosing 的未保存守卫照常触发。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    private void Maximize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Header_DoubleTapped(object? sender, TappedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
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
