using System.Globalization;
using VelaShell.Plugin.Sql.Execution;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>一格在界面上的显示形态(文本 + 该用哪种样式)。</summary>
/// <param name="Text">显示文本。</param>
/// <param name="Style">样式类别(AXAML 里按它挂 Classes)。</param>
/// <param name="Tooltip">悬浮提示;没有时为空。</param>
public readonly record struct SqlCellDisplay(string Text, SqlCellStyle Style, string Tooltip = "");

/// <summary>单元格样式类别。</summary>
public enum SqlCellStyle
{
    /// <summary>普通值。</summary>
    Normal,

    /// <summary>数据库 NULL —— 灰斜体。</summary>
    Null,

    /// <summary>空串 —— 与 NULL 必须看得出区别。</summary>
    Empty,

    /// <summary>二进制。</summary>
    Binary,

    /// <summary>读取失败。</summary>
    Error
}

/// <summary>
/// 把 <see cref="SqlCell" /> 变成界面上的一格。
/// <para>
/// <b>这是"数据工具的原罪"那条纪律的落点</b>(设计文档 §7.3):
/// NULL、空串、二进制、超长文本四者必须看得出区别。把它们都渲染成空白或都渲染成
/// <c>System.Byte[]</c>,用户就没法判断这一格到底是什么 —— 而他正要据此决定改不改它。
/// </para>
/// </summary>
internal static class SqlCellFormat
{
    /// <summary>格式化一格。</summary>
    /// <param name="cell">单元格。</param>
    /// <param name="loc">文案表。</param>
    /// <returns>显示形态。</returns>
    public static SqlCellDisplay Format(SqlCell cell, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        switch (cell.Kind)
        {
            case SqlCellKind.Null:
                // 字面量 NULL,配灰斜体。**不能**显示成空白 —— 那样就和空串混了。
                return new("NULL", SqlCellStyle.Null);

            case SqlCellKind.Error:
                // 一格读失败不让整页失败,但要如实说出来(§7.8)。
                return new(loc.Format("Sql_CellUnreadable", Shorten(cell.Error ?? "", 60)), SqlCellStyle.Error, cell.Error ?? "");

            case SqlCellKind.Binary:
            {
                byte[] bytes = cell.Bytes ?? [];
                string hex = Convert.ToHexString(bytes[..Math.Min(bytes.Length, 8)]);
                string size = HumanSize(cell.FullLength);
                return new($"0x{hex}{(cell.IsTruncated ? "…" : "")} ({size})", SqlCellStyle.Binary);
            }

            case SqlCellKind.Text:
            {
                string text = cell.Text ?? "";
                if (text.Length == 0)
                {
                    // 空串显示成 '' —— 一个看得见的记号,而不是什么都不画。
                    return new("''", SqlCellStyle.Empty);
                }
                // 单行化:值里的换行会把行高撑开,一屏就只剩几行了。
                string single = text.Replace("\r\n", "↵", StringComparison.Ordinal)
                                    .Replace('\n', '↵')
                                    .Replace('\r', '↵');
                return cell.IsTruncated
                    ? new(single + "…", SqlCellStyle.Normal, loc.Format("Sql_CellTruncated", cell.FullLength))
                    : new(single, SqlCellStyle.Normal, single.Length > 80 ? single : "");
            }

            default:
                return new("", SqlCellStyle.Normal);
        }
    }

    /// <summary>复制到剪贴板时用的形态:**不做任何装饰**,NULL 复制成空,值复制成原样。</summary>
    /// <param name="cell">单元格。</param>
    /// <returns>可粘贴的文本。</returns>
    public static string ForClipboard(SqlCell cell) => cell.Kind switch
    {
        SqlCellKind.Null => "",
        SqlCellKind.Binary => "0x" + Convert.ToHexString(cell.Bytes ?? []),
        SqlCellKind.Text => cell.Text ?? "",
        _ => ""
    };

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB"
    };

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
