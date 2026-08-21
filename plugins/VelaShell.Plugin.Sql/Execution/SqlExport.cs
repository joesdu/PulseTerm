using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using VelaShell.Plugin.Sql.Ui;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>导出格式。</summary>
internal enum SqlExportFormat
{
    /// <summary>CSV(逗号分隔,RFC 4180 转义)。</summary>
    Csv,

    /// <summary>TSV(制表符分隔,粘进 Excel 就是表格)。</summary>
    Tsv,

    /// <summary>JSON 数组,每行一个对象。</summary>
    Json,

    /// <summary>可执行的 <c>INSERT</c> 语句。</summary>
    Insert
}

/// <summary>
/// 把结果集导出成文件。
/// <para>
/// <b>导出用的是原值,不是界面上的装饰形态。</b> 界面把 NULL 画成字面量 <c>NULL</c>、
/// 把空串画成 <c>''</c>,是为了让人一眼分得清(§7.3);但导出的东西是要**再被机器读**的,
/// 带着那两个记号就成了脏数据 —— 一列全是 <c>NULL</c> 字符串的 CSV 谁也不敢用。
/// </para>
/// <para>
/// 四种格式的取舍:CSV/TSV 是给别的工具吃的,JSON 是给程序吃的,
/// <c>INSERT</c> 是给**另一个数据库**吃的 —— 最后这种最容易写错,所以它走方言包出标识符转义。
/// </para>
/// </summary>
internal static class SqlExport
{
    /// <summary>导出。</summary>
    /// <param name="grid">结果网格。</param>
    /// <param name="format">格式。</param>
    /// <param name="pack">方言包(<see cref="SqlExportFormat.Insert" /> 要用它出转义)。</param>
    /// <param name="tableName">目标表名(仅 <see cref="SqlExportFormat.Insert" /> 用)。</param>
    /// <returns>文件内容。</returns>
    public static string Render(
        SqlGridViewModel grid,
        SqlExportFormat format,
        Metadata.IDialectPack? pack = null,
        string tableName = "exported")
    {
        ArgumentNullException.ThrowIfNull(grid);
        return format switch
        {
            SqlExportFormat.Csv => Delimited(grid, ',', quote: true),
            SqlExportFormat.Tsv => Delimited(grid, '\t', quote: false),
            SqlExportFormat.Json => Json(grid),
            SqlExportFormat.Insert => Inserts(grid, pack, tableName),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的导出格式。")
        };
    }

    /// <summary>该用什么扩展名。</summary>
    /// <param name="format">格式。</param>
    /// <returns>扩展名(含点)。</returns>
    public static string Extension(SqlExportFormat format) => format switch
    {
        SqlExportFormat.Csv => ".csv",
        SqlExportFormat.Tsv => ".tsv",
        SqlExportFormat.Json => ".json",
        _ => ".sql"
    };

    /// <summary>
    /// 该用什么编码落盘。
    /// <para>
    /// <b>这不是一个可以一刀切的选择,BOM 在这四种格式里有三种不同的正确答案:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>CSV/TSV 要带 BOM。</b> Excel 在中文 Windows 上打开无 BOM 的 UTF-8 CSV
    ///     会按 GBK 解,中文列名直接成乱码 —— 而 CSV 的头号消费者就是 Excel。
    ///   </item>
    ///   <item>
    ///     <b>JSON 不能带 BOM。</b> RFC 8259 §8.1 的原话是实现
    ///     <i>MUST NOT</i> 在 JSON 文本开头加 BOM;带了它,严格的解析器会在第一个字符上报错。
    ///   </item>
    ///   <item>
    ///     <b>.sql 不带。</b> 它是要喂给命令行客户端的,BOM 会被某些客户端当成语句的一部分。
    ///   </item>
    /// </list>
    /// <para>
    /// <c>Encoding.UTF8</c> 这个**静态属性**是带 BOM 的(<c>encoderShouldEmitUTF8Identifier: true</c>),
    /// 而 <c>new UTF8Encoding(false)</c> 不带 —— 这两者长得几乎一样,是这一段最容易写反的地方。
    /// </para>
    /// </summary>
    /// <param name="format">格式。</param>
    /// <returns>编码。</returns>
    public static Encoding EncodingFor(SqlExportFormat format) => format switch
    {
        // 带 BOM:Excel 靠它认出 UTF-8。
        SqlExportFormat.Csv or SqlExportFormat.Tsv => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        // 不带 BOM。
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };

    private static string Delimited(SqlGridViewModel grid, char separator, bool quote)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, grid.Columns.Select(c => quote ? Csv(c.Header) : c.Header)));
        foreach (SqlGridRow row in grid.Rows)
        {
            sb.AppendLine(string.Join(separator, row.Cells.Select(cell =>
            {
                // NULL 导成**空字段**(CSV 里没有别的表达方式);空串也是空字段 ——
                // 这两者在 CSV 里本来就分不开,那是格式的限制,不是我们的偷懒。
                // 要区分就用 JSON。
                string value = SqlCellFormat.ForClipboard(cell.Raw);
                return quote ? Csv(value) : value.Replace('\t', ' ');
            })));
        }
        return sb.ToString();
    }

    /// <summary>RFC 4180:含分隔符、引号或换行时用双引号包起来,内部引号加倍。</summary>
    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;

    private static string Json(SqlGridViewModel grid)
    {
        var buffer = new MemoryStream();
        // Encoder 这一项不是可选的美化。**默认的 JavaScriptEncoder 会把所有非 ASCII 转义**,
        // 于是"北京"导出来是 "北京" —— 合法 JSON、也能原样读回来,但人和 grep 都读不了它,
        // 而数据库导出的头号用途恰恰是拿去看、拿去 diff。
        // 用 Create(UnicodeRanges.All) 而不是 UnsafeRelaxedJsonEscaping:前者只放开非 ASCII,
        // 仍然转义 < > & 这些 HTML 敏感字符;后者连它们一起放开,导出的文件要是被谁塞进网页就成了注入面。
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        }))
        {
            writer.WriteStartArray();
            foreach (SqlGridRow row in grid.Rows)
            {
                writer.WriteStartObject();
                for (int i = 0; i < grid.Columns.Count && i < row.Count; i++)
                {
                    writer.WritePropertyName(grid.Columns[i].Header);
                    SqlCell cell = row[i].Raw;
                    // JSON 是唯一能把 NULL 与空串**如实分开**的格式,所以这里要用上它:
                    // null 写成 JSON null,空串写成 ""。
                    switch (cell.Kind)
                    {
                        case SqlCellKind.Null:
                            writer.WriteNullValue();
                            break;
                        case SqlCellKind.Binary:
                            writer.WriteStringValue("0x" + Convert.ToHexString(cell.Bytes ?? []));
                            break;
                        default:
                            writer.WriteStringValue(cell.Text ?? "");
                            break;
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Inserts(SqlGridViewModel grid, Metadata.IDialectPack? pack, string tableName)
    {
        var sb = new StringBuilder();
        string table = pack?.QuoteIdentifier(tableName) ?? tableName;
        string columns = string.Join(", ", grid.Columns.Select(c => pack?.QuoteIdentifier(c.Header) ?? c.Header));
        foreach (SqlGridRow row in grid.Rows)
        {
            string values = string.Join(", ", row.Cells.Select(cell => cell.Raw.Kind switch
            {
                SqlCellKind.Null => "NULL",
                SqlCellKind.Binary => "X'" + Convert.ToHexString(cell.Raw.Bytes ?? []) + "'",
                // 单引号加倍是所有方言通行的字符串转义。
                // 注意这里导出的是**给人再执行**的脚本,不是我们自己要发的语句 ——
                // 我们自己发的一律参数化(§5.4.4)。
                _ => "'" + (cell.Raw.Text ?? "").Replace("'", "''", StringComparison.Ordinal) + "'"
            }));
            sb.Append(CultureInfo.InvariantCulture, $"INSERT INTO {table} ({columns}) VALUES ({values});");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
