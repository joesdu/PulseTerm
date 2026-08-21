using System.Collections.ObjectModel;
using System.Text;
using VelaShell.Plugin.Sql.Execution;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>
/// 网格里的一格。
/// <para>
/// 它是**类**而不是结构体,因为要支持就地编辑:DataGrid 双向绑定 <c>Text</c>,
/// 改过之后 <see cref="IsDirty" /> 变真,界面据此给未提交高亮(提交前随时能撤)。
/// </para>
/// </summary>
public sealed class SqlGridCell : ObservableObject
{
    private readonly SqlCellDisplay _display;
    private string _text;

    internal SqlGridCell(SqlCell raw, SqlCellDisplay display)
    {
        Raw = raw;
        _display = display;
        _text = display.Text;
        OriginalText = display.Text;
    }

    internal SqlCell Raw { get; }

    /// <summary>原始单元格(复制、写回要用原值,不是显示文本)。</summary>
    internal string OriginalText { get; }

    /// <summary>显示/编辑文本。</summary>
    public string Text
    {
        get => _text;
        set
        {
            SetProperty(ref _text, value);
            RaisePropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>样式类别。</summary>
    public SqlCellStyle Style => _display.Style;

    /// <summary>悬浮提示。</summary>
    public string Tooltip => _display.Tooltip;

    /// <summary>改过还没提交。</summary>
    public bool IsDirty => !string.Equals(_text, OriginalText, StringComparison.Ordinal);

    /// <summary>
    /// 写回时该用的值。**把界面的装饰形态翻译回真值**:
    /// <c>NULL</c> 这个字面量意味着设成数据库 NULL,<c>''</c> 意味着空串。
    /// 不翻译的话用户输入的 <c>NULL</c> 会被当成四个字符的字符串写进去。
    /// </summary>
    internal string? ValueForWrite => _text switch
    {
        "NULL" => null,
        "''" => "",
        _ => _text
    };

    /// <summary>写回前的原值(乐观并发的比对基准)。</summary>
    internal string? OriginalForWrite => Raw.Kind switch
    {
        SqlCellKind.Null => null,
        SqlCellKind.Text => Raw.Text,
        _ => OriginalText
    };

    /// <summary>撤销这一格的改动。</summary>
    public void Revert() => Text = OriginalText;
}

/// <summary>
/// 网格里的一行。用**索引器**暴露单元格,因为结果集的列数在运行期才知道 ——
/// DataGrid 的列绑定按 <c>[0]</c>、<c>[1]</c>… 走这个索引器。
/// </summary>
public sealed class SqlGridRow
{
    private readonly SqlGridCell[] _cells;

    internal SqlGridRow(SqlCell[] cells, Loc loc)
    {
        _cells = [.. cells.Select(c => new SqlGridCell(c, SqlCellFormat.Format(c, loc)))];
    }

    /// <summary>按列取单元格。越界返回一个空格,而不是抛 —— 绑定里抛异常只会得到一片空白和满屏日志。</summary>
    /// <param name="index">列序号。</param>
    /// <returns>单元格。</returns>
    public SqlGridCell this[int index] =>
        index >= 0 && index < _cells.Length ? _cells[index] : Empty;

    private static SqlGridCell Empty { get; } = new(SqlCell.Null(), new("", SqlCellStyle.Normal));

    /// <summary>列数。</summary>
    public int Count => _cells.Length;

    /// <summary>这一行有没有未提交的改动。</summary>
    public bool IsDirty => _cells.Any(c => c.IsDirty);

    internal IReadOnlyList<SqlGridCell> Cells => _cells;
}

/// <summary>
/// 结果网格。
/// <para>
/// 用官方 <c>Avalonia.Controls.DataGrid</c> 而不是自研,依据是实测(设计文档 §4.4):
/// 行虚拟化有(20 万行恒实现 21 行),列虚拟化没有但代价线性有界
/// (100 列 54MB/650ms、200 列 87MB/886ms)。行数才是会长到百万的那一维,它被虚拟化了。
/// </para>
/// </summary>
public sealed class SqlGridViewModel : ObservableObject
{
    private readonly Loc _loc;
    private string _status = "";
    private bool _truncated;
    private bool _editable;
    private string _readOnlyReason = "";

    internal SqlGridViewModel(Loc loc)
    {
        _loc = loc;
        Columns = [];
        Rows = [];
    }

    /// <summary>列(界面据此建 DataGrid 的列)。</summary>
    public ObservableCollection<SqlGridColumn> Columns { get; }

    /// <summary>行。</summary>
    public ObservableCollection<SqlGridRow> Rows { get; }

    /// <summary>底栏那一行状态。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>是不是因为达到取数上限而截断。</summary>
    public bool IsTruncated
    {
        get => _truncated;
        private set => SetProperty(ref _truncated, value);
    }

    /// <summary>能不能就地编辑。</summary>
    public bool IsEditable
    {
        get => _editable;
        internal set => SetProperty(ref _editable, value);
    }

    /// <summary>不能编辑时的原因(底栏如实显示,而不是让用户对着改不动的格子发懵)。</summary>
    public string ReadOnlyReason
    {
        get => _readOnlyReason;
        internal set => SetProperty(ref _readOnlyReason, value);
    }

    /// <summary>有没有未提交的改动。</summary>
    public bool HasPendingEdits => Rows.Any(r => r.IsDirty);

    /// <summary>装入一个结果集。</summary>
    /// <param name="result">结果集。</param>
    /// <param name="totalElapsedMs">整条语句耗时。</param>
    internal void Load(SqlResultSet result, long totalElapsedMs)
    {
        ArgumentNullException.ThrowIfNull(result);
        Columns.Clear();
        Rows.Clear();
        for (int i = 0; i < result.Columns.Count; i++)
        {
            SqlResultColumn column = result.Columns[i];
            Columns.Add(new(i, column.Name, column.ProviderTypeName, column.ClrTypeName));
        }
        foreach (SqlCell[] row in result.Rows)
        {
            Rows.Add(new(row, _loc));
        }
        IsTruncated = result.Truncated;
        Status = result.Truncated
            // 截断时**不报总数** —— 我们没查过总数,报一个就是编。
            ? _loc.Format("Sql_GridStatusTruncated", result.Rows.Count, totalElapsedMs)
            : _loc.Format("Sql_GridStatus", result.Rows.Count, result.Columns.Count, totalElapsedMs);
    }

    /// <summary>清空(执行失败时不该留着上一次的结果让人以为是这次的)。</summary>
    /// <param name="status">要显示的状态。</param>
    internal void Clear(string status)
    {
        Columns.Clear();
        Rows.Clear();
        IsTruncated = false;
        IsEditable = false;
        Status = status;
    }

    /// <summary>收集全部未提交的改动。</summary>
    /// <returns>改动列表。</returns>
    internal IReadOnlyList<SqlPendingEdit> CollectEdits()
    {
        List<SqlPendingEdit> edits = [];
        for (int r = 0; r < Rows.Count; r++)
        {
            SqlGridRow row = Rows[r];
            for (int c = 0; c < row.Count && c < Columns.Count; c++)
            {
                SqlGridCell cell = row[c];
                if (cell.IsDirty)
                {
                    edits.Add(new(r, Columns[c].Header, cell.OriginalForWrite, cell.ValueForWrite));
                }
            }
        }
        return edits;
    }

    /// <summary>取某行某列的原值(写回时拼 WHERE 用)。</summary>
    /// <param name="rowIndex">行号。</param>
    /// <param name="columnName">列名。</param>
    /// <returns>原值。</returns>
    internal string? OriginalValue(int rowIndex, string columnName)
    {
        int index = -1;
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Header, columnName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        return index < 0 || rowIndex < 0 || rowIndex >= Rows.Count ? null : Rows[rowIndex][index].OriginalForWrite;
    }

    /// <summary>撤销全部未提交的改动。</summary>
    internal void RevertAll()
    {
        foreach (SqlGridRow row in Rows)
        {
            foreach (SqlGridCell cell in row.Cells)
            {
                cell.Revert();
            }
        }
        RaisePropertyChanged(nameof(HasPendingEdits));
    }

    /// <summary>
    /// 把选中的行导成 TSV(粘进 Excel 就是表格)。
    /// <para>NULL 导成空、二进制导成十六进制 —— 与界面显示的装饰形态**不同**,
    /// 因为粘出去的东西是要再被机器读的。</para>
    /// </summary>
    /// <param name="rows">要导的行;为空则导全部。</param>
    /// <param name="withHeader">带不带表头。</param>
    /// <returns>TSV 文本。</returns>
    internal string ToDelimitedText(IReadOnlyList<SqlGridRow>? rows, bool withHeader)
    {
        IReadOnlyList<SqlGridRow> source = rows is { Count: > 0 } ? rows : [.. Rows];
        var sb = new StringBuilder();
        if (withHeader)
        {
            sb.AppendLine(string.Join('\t', Columns.Select(c => c.Header)));
        }
        foreach (SqlGridRow row in source)
        {
            sb.AppendLine(string.Join('\t', row.Cells.Select(c => SqlCellFormat.ForClipboard(c.Raw))));
        }
        return sb.ToString();
    }
}

/// <summary>网格的一列。</summary>
/// <param name="Index">列序号(绑定路径用)。</param>
/// <param name="Header">列名。</param>
/// <param name="ProviderTypeName">驱动报的数据源类型名。</param>
/// <param name="ClrTypeName">CLR 类型名。</param>
public sealed record SqlGridColumn(int Index, string Header, string ProviderTypeName, string ClrTypeName)
{
    /// <summary>
    /// 列头的悬浮提示。
    /// <para>
    /// 这里给的是**驱动报的类型,不等于建表时的类型** —— 实测 MySQL 上
    /// <c>VARBINARY(32)</c> 和 <c>BLOB</c> 都叫 <c>BLOB</c>、<c>LONGTEXT</c> 和 <c>VARCHAR</c>
    /// 都叫 <c>VARCHAR</c>。要准确类型得看对象树里那一列(它走方言包直查系统表)。
    /// </para>
    /// </summary>
    public string TypeTooltip => $"{ProviderTypeName} · {ClrTypeName}";
}
