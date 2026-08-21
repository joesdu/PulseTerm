namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>对象树上的节点类别。</summary>
internal enum SqlObjectKind
{
    /// <summary>数据库 / 目录。</summary>
    Database,

    /// <summary>Schema(PG / SQL Server / Oracle 有,MySQL 与 SQLite 没有)。</summary>
    Schema,

    /// <summary>表。</summary>
    Table,

    /// <summary>视图。</summary>
    View,

    /// <summary>物化视图(PG 上 <c>DbMaintenance</c> 里根本不存在,只能自己查)。</summary>
    MaterializedView,

    /// <summary>存储过程。</summary>
    Procedure,

    /// <summary>函数。</summary>
    Function,

    /// <summary>触发器。</summary>
    Trigger,

    /// <summary>序列。</summary>
    Sequence
}

/// <summary>对象树上的一个对象。</summary>
/// <param name="Kind">类别。</param>
/// <param name="Name">对象名(原样,<b>不做任何大小写规范化</b>)。</param>
/// <param name="Schema">所属 schema;方言无此概念时为空。</param>
/// <param name="Comment">对象注释。</param>
/// <param name="EstimatedRows">估算行数;拿不到时为 <see langword="null" />。</param>
/// <param name="IsSystem">
/// 是不是**服务端自带**的对象(系统库 / 系统 schema / 目录表)。
/// <para>
/// <b>为什么它必须是模型上的一格,而不是写进 <see cref="Comment" /> 的记号</b>:
/// 早先五个包各自往 <c>Comment</c> 里塞一个 <c>"@system"</c> 字符串,指望对象树认它 ——
/// 而对象树<b>一个字都没读</b>。于是 Oracle 的根上并排躺着 28 个 <c>ORACLE_MAINTAINED</c>
/// schema 与 2 个用户 schema、MySQL 的 <c>mysql</c> / <c>performance_schema</c> 与业务库混排、
/// SQL Server 的 <c>master</c>…<c>tempdb</c> 顶在最前面。真机计数见交付说明。
/// </para>
/// <para>
/// 记号写在注释里还有第二重伤:<c>Comment</c> 是要显示给用户的字段,
/// 一个系统库的"注释"于是变成了 <c>@system</c> 这四个字。
/// </para>
/// </param>
internal sealed record SqlObject(
    SqlObjectKind Kind,
    string Name,
    string Schema = "",
    string Comment = "",
    long? EstimatedRows = null,
    bool IsSystem = false)
{
    /// <summary>带 schema 的限定名(用于生成 SQL)。</summary>
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// 一列的元信息。
/// <para>
/// 这里的每一个字段都是 <c>DbMaintenance</c> 给不了或者会给错的
/// (见设计文档 §2.3):长度语义、是否自增、是否生成列、枚举取值、默认值是不是表达式。
/// 所以它由方言包直查系统表填,<b>不经 SqlSugar</b>。
/// </para>
/// </summary>
/// <param name="Name">列名(原样)。</param>
/// <param name="Ordinal">序号(1 起)。</param>
/// <param name="DataType">方言原生类型名(<c>varchar(50)</c> 这种完整形态,不是拆开的)。</param>
/// <param name="IsNullable">是否可空。</param>
/// <param name="IsPrimaryKey">是否主键成员。</param>
/// <param name="IsAutoIncrement">是否自增 / identity。</param>
/// <param name="IsGenerated">是否生成列 / 计算列 —— <b>回写时必须剔除</b>,带上它写库会报错。</param>
/// <param name="DefaultValue">默认值原文;无默认值为 <see langword="null" />。</param>
/// <param name="IsDefaultExpression">默认值是不是表达式(<c>CURRENT_TIMESTAMP</c> 与字符串 <c>'CURRENT_TIMESTAMP'</c> 必须分得开)。</param>
/// <param name="Comment">列注释。</param>
internal sealed record SqlColumn(
    string Name,
    int Ordinal,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey = false,
    bool IsAutoIncrement = false,
    bool IsGenerated = false,
    string? DefaultValue = null,
    bool IsDefaultExpression = false,
    string Comment = "")
{
    /// <summary>能不能被结果网格写回。生成列不行 —— 这是实测出来的地雷。</summary>
    public bool IsWritable => !IsGenerated;
}

/// <summary>
/// 一个索引。<c>DbMaintenance</c> 的 <c>GetIndexList</c> 只给名字(SQLite 上还给错),
/// 唯一性、列、类型全丢 —— 所以这些必须由方言包补。
/// </summary>
/// <param name="Name">索引名。</param>
/// <param name="Columns">按序的列名。</param>
/// <param name="IsUnique">是否唯一。</param>
/// <param name="IsPrimaryKey">是否主键索引。</param>
/// <param name="Kind">索引类型(BTREE / FULLTEXT / 聚集 / 筛选…)。</param>
/// <param name="Definition">索引定义原文(表达式索引、部分索引只有原文说得清)。</param>
internal sealed record SqlIndex(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique = false,
    bool IsPrimaryKey = false,
    string Kind = "",
    string Definition = "");

/// <summary>一条外键。<c>IDbMaintenance</c> 里**一个都没有**。</summary>
/// <param name="Name">约束名。</param>
/// <param name="Columns">本表列。</param>
/// <param name="ReferencedSchema">目标 schema。</param>
/// <param name="ReferencedTable">目标表。</param>
/// <param name="ReferencedColumns">目标列。</param>
/// <param name="OnDelete">删除时动作。</param>
/// <param name="OnUpdate">更新时动作。</param>
internal sealed record SqlForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete = "",
    string OnUpdate = "");

/// <summary>一张表/视图的完整结构。</summary>
/// <param name="Object">对象本身。</param>
/// <param name="Columns">列。</param>
/// <param name="Indexes">索引。</param>
/// <param name="ForeignKeys">外键。</param>
internal sealed record SqlTableSchema(
    SqlObject Object,
    IReadOnlyList<SqlColumn> Columns,
    IReadOnlyList<SqlIndex> Indexes,
    IReadOnlyList<SqlForeignKey> ForeignKeys)
{
    /// <summary>
    /// 主键列,<b>按键序</b>。<b>网格回写的定位依据</b>——拿错主键 = UPDATE 打到别的行。
    /// <para>
    /// <b>为什么优先从主键索引取而不是直接筛 <see cref="SqlColumn.IsPrimaryKey" /></b>:
    /// 后者给出的是**列序**,而键序可以与列序不同(<c>PRIMARY KEY (b, a)</c> 而 <c>a</c> 声明在前)。
    /// 回写只做等值合取,顺序不影响正确性;但显示主键、将来生成 DDL 时错序就是在说谎。
    /// 主键索引的 <c>Columns</c> 是按 key_ordinal 排的,那才是真正的键序。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PrimaryKey
    {
        get
        {
            string[] byColumnOrder = [.. Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name)];
            SqlIndex? pkIndex = Indexes.FirstOrDefault(i => i.IsPrimaryKey && i.Columns.Count > 0);
            if (pkIndex is null)
            {
                return byColumnOrder;
            }
            // 只有当两边是同一组列时才信索引的顺序 —— 对不上说明有一边不完整,
            // 那时宁可用列序(至少它来自我们刚查过的那份列清单)。
            return pkIndex.Columns.Count == byColumnOrder.Length
                   && pkIndex.Columns.All(c => byColumnOrder.Contains(c, StringComparer.OrdinalIgnoreCase))
                ? pkIndex.Columns
                : byColumnOrder;
        }
    }

    /// <summary>可写列(剔除生成列)。</summary>
    public IReadOnlyList<SqlColumn> WritableColumns => [.. Columns.Where(c => c.IsWritable)];

    /// <summary>
    /// 能不能就地编辑:必须能唯一定位一行。有主键最好,退而求其次用唯一索引;
    /// 都没有就只读(§7.5)。
    /// </summary>
    /// <param name="keyColumns">用来定位的列;不可编辑时为空。</param>
    /// <param name="reason">不可编辑的原因键(文案表里的键名)。</param>
    /// <returns>可否编辑。</returns>
    public bool TryGetRowKey(out IReadOnlyList<string> keyColumns, out string reason)
    {
        if (PrimaryKey.Count > 0)
        {
            keyColumns = PrimaryKey;
            reason = "";
            return true;
        }
        SqlIndex? unique = Indexes.FirstOrDefault(i => i.IsUnique && i.Columns.Count > 0);
        if (unique is not null)
        {
            keyColumns = unique.Columns;
            reason = "";
            return true;
        }
        keyColumns = [];
        reason = "Sql_GridReadOnlyNoKey";
        return false;
    }
}
