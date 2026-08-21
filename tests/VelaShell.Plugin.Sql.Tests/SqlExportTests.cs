using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.Plugin.Sql.Ui;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 导出。
/// <para>
/// <b>核心断言只有一条</b>:导出的是**原值**,不是界面上的装饰形态。
/// 界面把 NULL 画成字面量 <c>NULL</c>、把空串画成 <c>''</c> 是为了让人分得清(§7.3);
/// 但导出的东西要**再被机器读**——一列全是 <c>NULL</c> 字符串的 CSV 谁也不敢用。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlExportTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>CSV:NULL 与空串都导成空字段,含逗号/引号/换行的值按 RFC 4180 转义。</summary>
    [TestMethod]
    public void CSV_导出原值并按RFC4180转义()
    {
        SqlGridViewModel grid = Grid(
            ["id", "name", "memo"],
            [SqlCell.FromText("1", 1), SqlCell.FromText("a,b\"c", 5), SqlCell.Null()],
            [SqlCell.FromText("2", 1), SqlCell.FromText("", 0), SqlCell.FromText("行1\n行2", 6)]);

        string csv = SqlExport.Render(grid, SqlExportFormat.Csv);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual("id,name,memo", lines[0].TrimEnd('\r'));
        StringAssert.Contains(csv, "\"a,b\"\"c\"", "含逗号与引号的值要包引号且引号加倍。");
        StringAssert.Contains(csv, "1,\"a,b\"\"c\",", "NULL 要导成**空字段**,不是字面量 NULL。");
        Assert.IsFalse(csv.Contains(",NULL", StringComparison.Ordinal), "CSV 里不该出现字面量 NULL。");
        Assert.IsFalse(csv.Contains("''", StringComparison.Ordinal), "CSV 里不该出现界面用的空串记号 ''。");
    }

    /// <summary>
    /// JSON 是唯一能把 NULL 与空串**如实分开**的格式,所以它必须用上这一点。
    /// </summary>
    [TestMethod]
    public void JSON_区分NULL与空串()
    {
        SqlGridViewModel grid = Grid(
            ["a", "b"],
            [SqlCell.Null(), SqlCell.FromText("", 0)]);

        string json = SqlExport.Render(grid, SqlExportFormat.Json);

        StringAssert.Contains(json, "\"a\": null", "NULL 要写成 JSON null。");
        StringAssert.Contains(json, "\"b\": \"\"", "空串要写成空字符串,与 null 分开。");
    }

    /// <summary>二进制导成十六进制,而不是 <c>System.Byte[]</c> 这种没用的东西。</summary>
    [TestMethod]
    public void 二进制导成十六进制()
    {
        SqlGridViewModel grid = Grid(["blob"], [SqlCell.FromBinary([0x01, 0xAB], 2)]);

        StringAssert.Contains(SqlExport.Render(grid, SqlExportFormat.Csv), "0x01AB");
        StringAssert.Contains(SqlExport.Render(grid, SqlExportFormat.Json), "0x01AB");
        StringAssert.Contains(SqlExport.Render(grid, SqlExportFormat.Insert), "X'01AB'");
    }

    /// <summary>
    /// <c>INSERT</c> 脚本:标识符走方言包转义,值里的单引号加倍,NULL 写成裸 <c>NULL</c>。
    /// <para>它是给**另一个数据库**吃的,所以转义必须对 —— 这也是四种格式里最容易写错的一种。</para>
    /// </summary>
    [TestMethod]
    public void INSERT_标识符转义且值加倍单引号()
    {
        SqlGridViewModel grid = Grid(
            ["id", "na'me"],
            [SqlCell.FromText("1", 1), SqlCell.FromText("O'Brien", 7)],
            [SqlCell.FromText("2", 1), SqlCell.Null()]);

        string sql = SqlExport.Render(grid, SqlExportFormat.Insert, new SqlitePack(), "or\"ders");

        StringAssert.Contains(sql, "INSERT INTO \"or\"\"ders\"", "表名要按方言转义。");
        StringAssert.Contains(sql, "\"na'me\"", "列名要按方言转义。");
        StringAssert.Contains(sql, "'O''Brien'", "值里的单引号要加倍。");
        StringAssert.Contains(sql, "NULL)", "NULL 要写成裸 NULL,不是字符串 'NULL'。");
        Assert.IsFalse(sql.Contains("'NULL'", StringComparison.Ordinal));
    }

    /// <summary>TSV:值里的制表符要换掉,否则列会错位。</summary>
    [TestMethod]
    public void TSV_值里的制表符不会撑破列()
    {
        SqlGridViewModel grid = Grid(["a", "b"], [SqlCell.FromText("x\ty", 3), SqlCell.FromText("z", 1)]);

        string tsv = SqlExport.Render(grid, SqlExportFormat.Tsv);
        string dataLine = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r');

        Assert.AreEqual(2, dataLine.Split('\t').Length, "一行两列就该只有一个制表符。");
    }

    /// <summary>扩展名要与格式对得上 —— 存成 .csv 的 JSON 会让下游工具直接读错。</summary>
    [TestMethod]
    public void 扩展名与格式对应()
    {
        Assert.AreEqual(".csv", SqlExport.Extension(SqlExportFormat.Csv));
        Assert.AreEqual(".tsv", SqlExport.Extension(SqlExportFormat.Tsv));
        Assert.AreEqual(".json", SqlExport.Extension(SqlExportFormat.Json));
        Assert.AreEqual(".sql", SqlExport.Extension(SqlExportFormat.Insert));
    }

    /// <summary>
    /// 落盘编码:CSV/TSV 带 BOM,JSON 与 .sql 不带。
    /// <para>
    /// <b>这条测的是真写到磁盘上的头三个字节</b>,不是 <c>Render</c> 返回的字符串 ——
    /// BOM 是编码器加的,字符串里根本看不见它,所以只断言文本内容的用例对这个错完全免疫。
    /// </para>
    /// <para>
    /// 两个方向都是真错:JSON 带了 BOM,RFC 8259 §8.1 明说不许(严格解析器在第一个字符上报错);
    /// CSV 少了 BOM,中文 Windows 上的 Excel 会按 GBK 解,中文列名当场乱码。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 落盘编码_只有CSV与TSV带BOM()
    {
        SqlGridViewModel grid = Grid(["城市"], [SqlCell.FromText("北京", 2)]);
        byte[] bom = [0xEF, 0xBB, 0xBF];

        foreach ((SqlExportFormat format, bool wantBom) in new[]
                 {
                     (SqlExportFormat.Csv, true),
                     (SqlExportFormat.Tsv, true),
                     (SqlExportFormat.Json, false),
                     (SqlExportFormat.Insert, false)
                 })
        {
            string path = Path.Combine(Path.GetTempPath(), $"exp-{Guid.NewGuid():N}{SqlExport.Extension(format)}");
            try
            {
                await File.WriteAllTextAsync(
                    path, SqlExport.Render(grid, format, null, "t"), SqlExport.EncodingFor(format));
                byte[] head = (await File.ReadAllBytesAsync(path))[..3];

                Assert.AreEqual(
                    wantBom, head.SequenceEqual(bom),
                    $"{format} 的 BOM 判断反了(前三字节 {Convert.ToHexString(head)})。");

                // 无论带不带 BOM,内容都必须能按 UTF-8 原样读回来。
                StringAssert.Contains(await File.ReadAllTextAsync(path), "北京");
            }
            finally
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
        }
    }

    private static SqlGridViewModel Grid(string[] columns, params SqlCell[][] rows)
    {
        var grid = new SqlGridViewModel(Localization);
        var result = new SqlResultSet(
            [.. columns.Select(c => new SqlResultColumn(c, "String", "text"))],
            rows,
            Truncated: false,
            ElapsedMs: 0);
        grid.Load(result, 0);
        return grid;
    }
}
