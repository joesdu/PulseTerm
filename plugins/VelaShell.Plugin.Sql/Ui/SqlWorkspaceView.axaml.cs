using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>
/// 数据库工作台面板的视图。
/// <para>
/// 视图只做三件视图该做的事:装配、把**键盘手势**接到视图模型上、
/// 以及**按运行期列数动态建 DataGrid 的列** —— 后者没法在 AXAML 里声明,
/// 因为结果集的列在运行期才知道。
/// </para>
/// </summary>
public sealed partial class SqlWorkspaceView : UserControl
{
    private readonly SqlWorkspaceViewModel _viewModel;
    private readonly ContextMenu? _treeMenu;
    private SqlQueryTabViewModel? _boundTab;
    private TextEditor? _editor;
    private DataGrid? _grid;

    /// <summary>用给定的视图模型初始化。</summary>
    /// <param name="viewModel">视图模型。</param>
    public SqlWorkspaceView(SqlWorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ObjectTree.DoubleTapped += OnTreeDoubleTapped;
        ObjectTree.SelectionChanged += OnTreeSelectionChanged;
        // 右键菜单从 AXAML 上摘下来自己拿着:它只在选中项是表 / 视图时才挂回去(见 SyncTreeMenu)。
        // 开局没有选中项,所以先摘掉。
        _treeMenu = ObjectTree.ContextMenu;
        ObjectTree.ContextMenu = null;
        // 右键要先把选中项挪到光标底下 —— 用**按下**而不是 ContextRequested,见 OnTreePointerPressed。
        ObjectTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // 首个标签是构造时就建好的,所以这里要主动接一次 —— 否则要等用户切标签才生效。
        Dispatcher.UIThread.Post(BindActiveTab, DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.ActiveTab))
        {
            Dispatcher.UIThread.Post(BindActiveTab, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 把当前标签接到编辑器与网格上。
    /// <para>
    /// 编辑器不用绑定而是手工同步:AvaloniaEdit 的文本在 <c>Document</c> 上,
    /// 不是一个可直接双向绑定的 <c>Text</c> 属性。手工同步反而更直白。
    /// </para>
    /// </summary>
    private void BindActiveTab()
    {
        if (_boundTab is { } previous)
        {
            previous.Grid.Columns.CollectionChanged -= OnGridColumnsChanged;
            previous.PropertyChanged -= OnTabPropertyChanged;
        }
        _boundTab = _viewModel.ActiveQueryTab;
        _editor = this.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault();
        _grid = this.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault();
        if (_boundTab is null)
        {
            return;
        }

        if (_editor is not null)
        {
            _editor.Text = _boundTab.Sql;
            _editor.TextChanged -= OnEditorTextChanged;
            _editor.TextChanged += OnEditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
            _editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        }
        // 文件选择器只有控件层拿得到 —— 用一个委托注进去,视图模型因此不必认识 TopLevel。
        _boundTab.SaveFilePicker = SaveFileAsync;
        _boundTab.Grid.Columns.CollectionChanged += OnGridColumnsChanged;
        _boundTab.PropertyChanged += OnTabPropertyChanged;
        RebuildGridColumns();
    }

    /// <summary>弹"另存为"。取消时返回 <see langword="null" />。</summary>
    private async Task<string?> SaveFileAsync(string suggestedName, string extension)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }
        IStorageFile? file = await storage.SaveFilePickerAsync(new()
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.')
        });
        return file?.TryGetLocalPath();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 执行完之后如果拿到了出错行,把光标带过去 —— §7.4 那套三步定位算法的最后一步
        // 就是"把用户带到出错的那一行",算出来却不用等于白算。
        if (e.PropertyName != nameof(SqlQueryTabViewModel.ErrorLine)
            || _boundTab?.ErrorLine is not { } line
            || _editor is null)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (line >= 1 && line <= _editor.Document.LineCount)
            {
                _editor.TextArea.Caret.Line = line;
                _editor.ScrollToLine(line);
            }
        });
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_boundTab is not null && _editor is not null)
        {
            _boundTab.Sql = _editor.Text;
        }
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_boundTab is not null && _editor is not null)
        {
            _boundTab.CaretOffset = _editor.CaretOffset;
        }
    }

    private void OnGridColumnsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RebuildGridColumns);

    /// <summary>
    /// 按结果集的列重建 DataGrid 的列。
    /// <para>
    /// 绑定路径是 <c>[i].Text</c> —— 走 <see cref="SqlGridRow" /> 的索引器。
    /// 列数运行期才知道,所以只能在这里建,AXAML 里声明不了。
    /// </para>
    /// </summary>
    private void RebuildGridColumns()
    {
        if (_grid is null || _boundTab is null)
        {
            return;
        }
        _grid.Columns.Clear();
        foreach (SqlGridColumn column in _boundTab.Grid.Columns)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Header,
                // 90px 是个够用的起点:太窄看不见值,太宽一屏放不下几列。
                // 用户拖过的宽度由 DataGrid 自己记着(同一个结果集内)。
                Width = new DataGridLength(90),
                // 双向:网格可编辑时用户改的值要回到单元格上(IsDirty 随之变真)。
                // 网格不可编辑时 DataGrid 自己会挡住编辑,这里不必分叉。
                Binding = new Binding($"[{column.Index}].Text") { Mode = BindingMode.TwoWay }
            });
        }
    }

    /// <summary>
    /// 复制选中的行为 TSV(粘进 Excel 就是表格)。
    /// <para>
    /// **用原值而不是界面上的装饰形态**:NULL 复制成空、二进制复制成十六进制。
    /// 界面把 NULL 画成字面量 <c>NULL</c>、把空串画成 <c>''</c> 是为了让人看得出区别;
    /// 但粘出去的东西是要再被机器读的,带着那两个记号就成了脏数据。
    /// </para>
    /// </summary>
    private void CopySelection()
    {
        if (_grid is null || _boundTab is null)
        {
            return;
        }
        IReadOnlyList<SqlGridRow> rows = [.. _grid.SelectedItems.OfType<SqlGridRow>()];
        string text = _boundTab.Grid.ToDelimitedText(rows, withHeader: true);
        if (text.Length == 0)
        {
            return;
        }
        _viewModel.CopyToClipboard(text);
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ObjectTree.SelectedItem is SqlTreeNode node)
        {
            _viewModel.Tree.Selected = node;
        }
        SyncTreeMenu();
    }

    /// <summary>
    /// 按选中项决定**这棵树此刻有没有右键菜单**。
    /// <para>
    /// 菜单里统共两项(打开数据 / 查看结构),两项都只对表 / 视图 / 物化视图成立。
    /// 在库 / schema / 分类 / 列上把它整个摘掉,而不是弹出来把两项置灰:
    /// 两项全灰剩下的是一个空框,"这里本来应该有什么"比什么都不弹更难解释;
    /// 而这个仓库反复反对的正是"摆一个不起作用的控件" —— 灰掉的菜单项是同一类东西,
    /// 它占着位置、暗示这里有能做的事,点下去却什么都不发生。
    /// </para>
    /// <para>
    /// 实现上是**装卸 <c>ContextMenu</c> 属性本身**,而不是在 <c>ContextMenu.Opening</c> 里
    /// <c>Cancel</c>。后者试过,不成:Avalonia 12 上 <c>Opening</c> 只在弹窗真正开起来的那条
    /// 路径上抛,<c>Open(control)</c> 直接置 <c>IsOpen</c> 时一次都不抛 ——
    /// 也就是说那道闸门既拦不住程序化入口,headless 里还验不了。装卸属性没有这些含糊:
    /// <c>ContextMenu</c> 为 <see langword="null" /> 时 Avalonia 连
    /// <c>ContextRequested</c> 的处理器都不会挂,右键彻底没有反应,而且一眼看得出来。
    /// </para>
    /// </summary>
    private void SyncTreeMenu() =>
        ObjectTree.ContextMenu = ObjectTree.SelectedItem is SqlTreeNode { CanOpenData: true } ? _treeMenu : null;

    /// <summary>
    /// 右键按下时先把选中项挪到光标底下的节点上。
    /// <para>
    /// 菜单挂不挂、以及两个菜单项动作打开谁,读的都是同一个 <c>SelectedItem</c>;
    /// 不同步就会出现"右键分类节点,弹出来的却是上一次选中那张表的菜单"。
    /// </para>
    /// <para>
    /// 挂在**隧道相的 PointerPressed** 上,而不是 <c>ContextRequested</c>:
    /// 后者是右键**抬起**才发的,而 <see cref="ContextMenu" /> 自己就挂在它的冒泡相上,
    /// 处理器顺序由注册先后决定,抢不稳;更要命的是路由表在 <c>RaiseEvent</c> 一开始就建好了,
    /// 在事件途中才把 <c>ContextMenu</c> 挂回去,这一轮根本不会被调用 —— 表现就是
    /// "第一次右键一张表没菜单,再右键一次才有"。按下永远早于抬起,所以放在这里没有这个窗口。
    /// </para>
    /// </summary>
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ObjectTree).Properties.IsRightButtonPressed)
        {
            return;
        }
        if (e.Source is Visual source
            && source.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault() is
                { DataContext: SqlTreeNode node })
        {
            ObjectTree.SelectedItem = node;
        }
    }

    /// <summary>右键菜单:打开数据。</summary>
    private void OnMenuOpenData(object? sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is SqlTreeNode node)
        {
            _viewModel.OpenData(node);
        }
    }

    /// <summary>右键菜单:查看结构。</summary>
    private void OnMenuOpenStructure(object? sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is SqlTreeNode node)
        {
            _viewModel.OpenStructure(node);
        }
    }

    /// <summary>
    /// 双击 = 表 / 视图打开数据(带服务端 LIMIT,不是 <c>select *</c>),
    /// 库 / schema / 分类展开收起。分派在
    /// <see cref="SqlWorkspaceViewModel.ActivateNode" /> 里,这里只负责"用户双击了谁"。
    /// </summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ObjectTree.SelectedItem is SqlTreeNode node)
        {
            _viewModel.ActivateNode(node);
        }
    }

    /// <summary>
    /// 面板级快捷键(§7.7)。
    /// <list type="bullet">
    ///   <item><c>Ctrl+Enter</c> —— 执行光标所在语句。</item>
    ///   <item><c>Ctrl+Shift+Enter</c> —— 执行全部。</item>
    ///   <item><c>Esc</c> —— 取消正在跑的查询(确认框开着时先关框)。</item>
    ///   <item><c>F5</c> —— 刷新对象树当前节点。</item>
    ///   <item><c>Ctrl+P</c> —— 聚焦对象过滤框。</item>
    /// </list>
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        SqlQueryTabViewModel? tab = _viewModel.ActiveQueryTab;

        // 确认框开着时键盘只服务于它 —— 别让 Esc 在关框的同时又去取消查询。
        if (tab?.HasConfirmation == true)
        {
            if (e.Key == Key.Escape)
            {
                tab.RejectCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when control && shift:
                tab?.ExecuteAllCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Enter when control:
                tab?.ExecuteCurrentCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Escape when tab?.IsBusy == true:
                tab.CancelCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.C when control:
                CopySelection();
                e.Handled = true;
                return;
            case Key.F5 when _viewModel.ActiveTab is SqlStructureTabViewModel structure:
                structure.RefreshCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.F5 when _viewModel.ActiveTab is SqlOpsTabViewModel ops:
                ops.RefreshCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.F5:
                _viewModel.RefreshTreeCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.P when control:
                FilterBox.Focus();
                FilterBox.SelectAll();
                e.Handled = true;
                return;
            default:
                return;
        }
    }
}
