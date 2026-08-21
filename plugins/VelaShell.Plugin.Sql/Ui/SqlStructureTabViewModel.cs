using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using VelaShell.Plugin.Sql.Metadata;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>结构页上的一行(列 / 索引 / 外键共用同一种扁平形态,界面才不用三套模板)。</summary>
/// <param name="Name">名字。</param>
/// <param name="Detail">主要信息。</param>
/// <param name="Extra">附加信息(注释、定义、动作)。</param>
public sealed record SqlStructureRow(string Name, string Detail, string Extra);

/// <summary>
/// 一张表的结构页:列、索引、外键、建表 DDL。
/// <para>
/// <b>这一页存在的理由</b>:结果网格的列头只给得出**驱动报的类型**,而实测那不等于建表时的类型——
/// MySQL 上 <c>VARBINARY(32)</c> 和 <c>BLOB</c> 都叫 <c>BLOB</c>、<c>LONGTEXT</c> 和 <c>VARCHAR</c>
/// 都叫 <c>VARCHAR</c>(§7.3)。要看准确类型只能来这一页,因为它走方言包直查系统表。
/// </para>
/// </summary>
public sealed class SqlStructureTabViewModel : SqlTabViewModel
{
    private readonly SqlSession _session;
    private readonly SqlObject _target;
    private readonly Loc _loc;
    private string _status = "";
    private string _ddl = "";
    private SqlConfirmationRequest? _confirmation;
    private TaskCompletionSource<bool>? _confirmationAnswer;
    private readonly HashSet<string> _constraintIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这张表所在的库;空表示连接串里那个。
    /// <para>
    /// PG / SQL Server 上目录表是每库一份的,不带这一格就会拿"另一个库"的元数据连接
    /// 去查这张表的列 —— 查不到,而结构页会画成一张没有列的表。
    /// </para>
    /// </summary>
    private readonly string _catalog = "";

    /// <summary>
    /// 取这张表该用的元数据连接。
    /// <para>
    /// <b>本页每一条语句都必须经这里再走 <c>UseAsync</c>。</b> 早先 DDL、<c>SHOW CREATE</c>、
    /// 行数估算三处是直接 <c>_session.Metadata.Raw.CreateCommand()</c> 的 ——
    /// 那一刻就绕过了连接上的"一次只跑一条"闸门(<c>SqlConnection._gate</c>)。
    /// 而对象树的展开是即发即忘的:用户在结构页点"应用"的同时树正在查表清单,
    /// 两条命令落在同一根连接上,Npgsql 直接抛
    /// <c>A command is already in progress</c> —— 而它看起来像"这条 DDL 有问题"。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>元数据连接。</returns>
    private Task<SqlConnection> ConnectionAsync(CancellationToken cancellationToken) =>
        _session.MetadataForAsync(_catalog, cancellationToken);

    internal SqlStructureTabViewModel(SqlSession session, SqlObject target, Loc loc, string catalog = "")
    {
        _session = session;
        _target = target;
        _loc = loc;
        _catalog = catalog;
        Title = loc.Format("Sql_StructureTabTitle", target.Name);
        RefreshCommand = new(() => LoadAsync(CancellationToken.None));
        AddColumnCommand = new(AddColumnAsync);
        DropColumnCommand = new(DropColumnAsync, () => SelectedColumn is not null);
        CreateIndexCommand = new(CreateIndexAsync);
        DropIndexCommand = new(DropIndexAsync, () => SelectedIndex is { } row && !IsConstraintIndex(row.Name));
        ConfirmCommand = new(() => AnswerConfirmation(true));
        RejectCommand = new(() => AnswerConfirmation(false));
        NewColumnType = TypeChoices.Count > 0 ? TypeChoices[0] : "";
    }

    /// <inheritdoc />
    public override string Title { get; }

    /// <summary>列。</summary>
    public ObservableCollection<SqlStructureRow> Columns { get; } = [];

    /// <summary>索引。</summary>
    public ObservableCollection<SqlStructureRow> Indexes { get; } = [];

    /// <summary>外键。</summary>
    public ObservableCollection<SqlStructureRow> ForeignKeys { get; } = [];

    /// <summary>建表 DDL 原文;方言不提供时是一句说明而不是空白(§7.8)。</summary>
    public string Ddl
    {
        get => _ddl;
        private set => SetProperty(ref _ddl, value);
    }

    /// <summary>状态。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// 一个空小节要显示的字样。
    /// <para>
    /// <b>空白与"读不到"长得一模一样</b>(§7.8)——"外键"底下什么都没有,
    /// 到底是这张表真的没有外键,还是查外键那一步悄悄失败了?一行"无"就把这两件事分开了。
    /// </para>
    /// </summary>
    public string EmptyLabel => _loc["Sql_SectionEmpty"];

    /// <summary>这张表一个索引都没有。</summary>
    public bool HasNoIndexes => Indexes.Count == 0;

    /// <summary>这张表一条外键都没有。</summary>
    public bool HasNoForeignKeys => ForeignKeys.Count == 0;

    /// <summary>「列」小标题。</summary>
    public string ColumnsLabel => _loc["Sql_ColumnsHeader"];

    /// <summary>「索引」小标题。</summary>
    public string IndexesLabel => _loc["Sql_IndexesHeader"];

    /// <summary>「外键」小标题。</summary>
    public string ForeignKeysLabel => _loc["Sql_ForeignKeysHeader"];

    /// <summary>「建表语句」小标题。</summary>
    public string DdlLabel => _loc["Sql_DdlHeader"];

    /// <summary>刷新。</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>选中的列(删列用)。</summary>
    public SqlStructureRow? SelectedColumn
    {
        get;
        set
        {
            field = value;
            DropColumnCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>选中的索引(删索引用)。</summary>
    public SqlStructureRow? SelectedIndex
    {
        get;
        set
        {
            field = value;
            DropIndexCommand.RaiseCanExecuteChanged();
            // 选中一个删不掉的索引时,**当场说清为什么**,而不是让按钮灰着不解释 ——
            // 一个没有理由的灰按钮与"这功能坏了"分不开。
            if (value is { } row && IsConstraintIndex(row.Name))
            {
                Status = _loc.Format("Sql_IndexBackedByConstraint", row.Name);
            }
        }
    }

    /// <summary>
    /// 这个索引是不是某条约束的实现。
    /// <para>
    /// 主键与唯一约束背后的索引<b>不能用 <c>DROP INDEX</c> 删</b>,引擎要求改走 <c>DROP CONSTRAINT</c>
    /// (SQL Server 报 Msg 3723、PG 报 2BP01)。而"删约束"是另一件事 ——
    /// 用户点的是"删索引",这里**不偷偷改写**,只是拦下并说明。
    /// </para>
    /// </summary>
    /// <param name="indexName">索引名。</param>
    /// <returns>是约束背后的索引则为 <see langword="true" />。</returns>
    public bool IsConstraintIndex(string indexName) => _constraintIndexes.Contains(indexName);

    private static bool IsConstraintBacked(SqlIndex index) =>
        // 方言包把"这是唯一约束而不是唯一索引"记在 Kind 里(SQL Server 的 unique-constraint、
        // SQLite 的 UNIQUE CONSTRAINT)。两种写法都认,大小写不敏感。
        index.Kind.Contains("constraint", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 类型候选。**由方言包给,不是一份放之四海皆准的清单** ——
    /// 把 NVARCHAR 摆给 PostgreSQL、把 SERIAL 摆给 SQL Server,都只会让人写出跑不了的 DDL。
    /// </summary>
    public IReadOnlyList<string> TypeChoices => _session.Pack.CommonTypes;

    /// <summary>新列名。</summary>
    public string NewColumnName { get; set; } = "";

    /// <summary>新列类型。</summary>
    public string NewColumnType { get; set; }

    /// <summary>新列是否可空。</summary>
    public bool NewColumnNullable { get; set; } = true;

    /// <summary>新列默认值(原样进 DDL;为空则不写 DEFAULT)。</summary>
    public string NewColumnDefault { get; set; } = "";

    /// <summary>新索引名。</summary>
    public string NewIndexName { get; set; } = "";

    /// <summary>新索引的列(逗号分隔)。</summary>
    public string NewIndexColumns { get; set; } = "";

    /// <summary>新索引是否唯一。</summary>
    public bool NewIndexUnique { get; set; }

    /// <summary>加列。</summary>
    public AsyncRelayCommand AddColumnCommand { get; }

    /// <summary>删列。</summary>
    public AsyncRelayCommand DropColumnCommand { get; }

    /// <summary>建索引。</summary>
    public AsyncRelayCommand CreateIndexCommand { get; }

    /// <summary>删索引。</summary>
    public AsyncRelayCommand DropIndexCommand { get; }

    /// <summary>确认。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>否认。</summary>
    public RelayCommand RejectCommand { get; }

    /// <summary>
    /// 这一页能不能改结构。
    /// <para>
    /// 三条独立的理由都会让它是 <see langword="false" />:**连接标了只读**、
    /// **这不是一张表**(视图/系统表没有列可加),或者**方言包没给 DDL 生成器**。
    /// 三种都不该把按钮摆在那儿等人点了才报错。
    /// </para>
    /// </summary>
    public bool CanDesign =>
        !_session.Settings.ReadOnly
        && _target.Kind == SqlObjectKind.Table
        && _session.Pack.AddColumnDdl(_target, new("c", 0, "int", IsNullable: true)) is not null;

    /// <summary>待确认的 DDL;没有时为 <see langword="null" />。</summary>
    public SqlConfirmationRequest? Confirmation
    {
        get => _confirmation;
        private set
        {
            SetProperty(ref _confirmation, value);
            RaisePropertyChanged(nameof(HasConfirmation));
        }
    }

    /// <summary>有没有待确认的 DDL。</summary>
    public bool HasConfirmation => _confirmation is not null;

    /// <summary>生产环境下要手打的表名。</summary>
    public string TypedConfirmation { get; set; } = "";

    /// <summary>「加列」按钮文案。</summary>
    public string AddColumnLabel => _loc["Sql_AddColumn"];

    /// <summary>「删列」按钮文案。</summary>
    public string DropColumnLabel => _loc["Sql_DropColumn"];

    /// <summary>「建索引」按钮文案。</summary>
    public string CreateIndexLabel => _loc["Sql_CreateIndex"];

    /// <summary>「删索引」按钮文案。</summary>
    public string DropIndexLabel => _loc["Sql_DropIndex"];

    /// <summary>「改结构」小标题。</summary>
    public string DesignerLabel => _loc["Sql_DesignerHeader"];

    /// <summary>确认框「确定」。</summary>
    public string ConfirmLabel => _loc["Sql_ConfirmYes"];

    /// <summary>确认框「取消」。</summary>
    public string CancelLabel => _loc["Sql_ConfirmNo"];

    private async Task AddColumnAsync()
    {
        if (string.IsNullOrWhiteSpace(NewColumnName))
        {
            Status = _loc["Sql_NameRequired"];
            return;
        }
        var column = new SqlColumn(
            NewColumnName.Trim(),
            Ordinal: Columns.Count,
            NewColumnType,
            NewColumnNullable,
            DefaultValue: NewColumnDefault.Trim() is { Length: > 0 } d ? d : null);
        await ApplyDdlAsync(_session.Pack.AddColumnDdl(_target, column)).ConfigureAwait(true);
    }

    private async Task DropColumnAsync()
    {
        if (SelectedColumn is not { } row)
        {
            return;
        }
        await ApplyDdlAsync(_session.Pack.DropColumnDdl(_target, row.Name)).ConfigureAwait(true);
    }

    private async Task CreateIndexAsync()
    {
        string[] columns =
        [
            .. NewIndexColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
        if (string.IsNullOrWhiteSpace(NewIndexName) || columns.Length == 0)
        {
            Status = _loc["Sql_NameRequired"];
            return;
        }
        await ApplyDdlAsync(
            _session.Pack.CreateIndexDdl(_target, NewIndexName.Trim(), columns, NewIndexUnique)).ConfigureAwait(true);
    }

    private async Task DropIndexAsync()
    {
        if (SelectedIndex is not { } row)
        {
            return;
        }
        await ApplyDdlAsync(_session.Pack.DropIndexDdl(_target, row.Name)).ConfigureAwait(true);
    }

    /// <summary>
    /// 发一条 DDL:先给原文、再要确认、然后才发。
    /// <para>
    /// <b>DDL 比 UPDATE 更需要这一步。</b> 改数据错了还有乐观并发拦一道;
    /// <c>DROP COLUMN</c> 发出去的那一刻数据就没了,而且**多数引擎的 DDL 不参与事务回滚**
    /// (MySQL 上它还会把你正开着的事务隐式提交掉)。所以这里没有"直接执行"这条路径。
    /// </para>
    /// </summary>
    private async Task ApplyDdlAsync(string? ddl)
    {
        if (_session.Settings.ReadOnly)
        {
            Status = _loc["Sql_GridReadOnlyConnection"];
            return;
        }
        if (ddl is null)
        {
            // 方言包给不出这条 DDL —— 明说,而不是拼一条大概齐的、让服务端去报语法错。
            Status = _loc.Format("Sql_NoDesignerForDialect", SqlDialects.Of(_session.Dialect).DisplayName);
            return;
        }

        TypedConfirmation = "";
        _confirmationAnswer = new();
        Confirmation = new(
            _loc["Sql_DdlTitle"],
            _loc.Format("Sql_DdlMessage", ddl),
            // 生产环境下改结构要手打表名 —— 与改数据同一条护栏。
            _session.Settings.Environment == SqlEnvironment.Production ? _target.Name : "");
        bool ok = await _confirmationAnswer.Task.ConfigureAwait(true);
        Confirmation = null;
        if (!ok)
        {
            Status = _loc["Sql_Cancelled"];
            return;
        }

        try
        {
            SqlConnection connection = await ConnectionAsync(CancellationToken.None).ConfigureAwait(true);
            await connection.UseAsync(async (raw, token) =>
            {
                await using DbCommand command = raw.CreateCommand();
                command.CommandText = ddl;
                command.CommandTimeout = _session.Settings.CommandTimeoutSeconds;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(true);
            Status = _loc["Sql_DdlApplied"];
            // 结构变了,这一页上显示的东西**全都过期了**,立刻重读。
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void AnswerConfirmation(bool ok)
    {
        if (_confirmationAnswer is not { } answer)
        {
            return;
        }
        // 要手打表名时,打错就不放行 —— 与改数据同一条护栏。
        if (ok
            && Confirmation is { TypedName.Length: > 0 } request
            && !string.Equals(TypedConfirmation.Trim(), request.TypedName, StringComparison.Ordinal))
        {
            return;
        }
        _confirmationAnswer = null;
        answer.TrySetResult(ok);
    }

    /// <summary>装载结构。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            SqlConnection connection = await ConnectionAsync(cancellationToken).ConfigureAwait(true);
            SqlTableSchema schema = await connection
                .UseAsync((c, t) => _session.Pack.DescribeAsync(c, _target, t), cancellationToken).ConfigureAwait(true);

            Columns.Clear();
            foreach (SqlColumn column in schema.Columns)
            {
                // 把"这一列有什么特别之处"摆在一眼能看见的地方:
                // 主键、自增、生成列、可空性 —— 这四样正是 DbMaintenance 给错或给不了的(§2.3)。
                List<string> marks = [];
                if (column.IsPrimaryKey) { marks.Add("PK"); }
                if (column.IsAutoIncrement) { marks.Add(_loc["Sql_AutoIncrement"]); }
                if (column.IsGenerated) { marks.Add(_loc["Sql_GeneratedColumn"]); }
                if (!column.IsNullable) { marks.Add("NOT NULL"); }
                if (column.DefaultValue is { Length: > 0 })
                {
                    marks.Add($"DEFAULT {column.DefaultValue}");
                }
                Columns.Add(new(column.Name, column.DataType, string.Join(" · ", marks.Concat([column.Comment]).Where(x => x.Length > 0))));
            }

            Indexes.Clear();
            _constraintIndexes.Clear();
            foreach (SqlIndex index in schema.Indexes)
            {
                // 主键与唯一**约束**背后的索引删不掉:引擎要求改用 DROP CONSTRAINT
                // (SQL Server 是 Msg 3723,PG 是 2BP01)。记下来,好在按钮那一层就拦住。
                if (index.IsPrimaryKey || IsConstraintBacked(index))
                {
                    _constraintIndexes.Add(index.Name);
                }
                string kind = string.Join(" ", new[]
                {
                    index.IsPrimaryKey ? "PRIMARY" : "",
                    index.IsUnique ? "UNIQUE" : "",
                    index.Kind
                }.Where(x => x.Length > 0));
                Indexes.Add(new(index.Name, string.Join(", ", index.Columns), $"{kind} {index.Definition}".Trim()));
            }

            ForeignKeys.Clear();
            foreach (SqlForeignKey fk in schema.ForeignKeys)
            {
                string target = string.IsNullOrEmpty(fk.ReferencedSchema)
                    ? fk.ReferencedTable
                    : $"{fk.ReferencedSchema}.{fk.ReferencedTable}";
                ForeignKeys.Add(new(
                    fk.Name,
                    $"({string.Join(", ", fk.Columns)}) → {target}({string.Join(", ", fk.ReferencedColumns)})",
                    string.Join(" ", new[] { fk.OnDelete, fk.OnUpdate }.Where(x => x.Length > 0))));
            }

            await LoadDdlAsync(cancellationToken).ConfigureAwait(true);
            RaisePropertyChanged(nameof(HasNoIndexes));
            RaisePropertyChanged(nameof(HasNoForeignKeys));
            Status = _loc.Format("Sql_StructureLoaded", Columns.Count, Indexes.Count, ForeignKeys.Count);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private async Task LoadDdlAsync(CancellationToken cancellationToken)
    {
        if (_session.Pack.ShowCreateSql(_target) is not { } sql)
        {
            // 方言不提供就**如实说**,而不是留一片空白 —— 空白与"这张表没有 DDL"长得一样(§7.8)。
            Ddl = _loc.Format("Sql_NoDdlForDialect", SqlDialects.Of(_session.Dialect).DisplayName);
            return;
        }
        try
        {
            SqlConnection connection = await ConnectionAsync(cancellationToken).ConfigureAwait(true);
            Ddl = await connection.UseAsync(async (raw, token) =>
            {
                await using DbCommand command = raw.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = _session.Settings.CommandTimeoutSeconds;
                await using DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                // MySQL 的 SHOW CREATE TABLE 返回两列,**DDL 在第二列**;
                // 其余方言给一列。取最后一列对两种形态都对。
                return await reader.ReadAsync(token).ConfigureAwait(false)
                    ? reader.GetValue(reader.FieldCount - 1)?.ToString() ?? ""
                    : Ddl;
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Ddl = ex.Message;
        }
    }

    /// <summary>把 DDL 复制走时用的文本。</summary>
    /// <returns>DDL。</returns>
    public string DdlForClipboard() => Ddl;

    /// <summary>行数估算(底栏用)。<b>只是估算</b>,精确值要用户点了才查(§7.3)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>估算行数;拿不到时为 <see langword="null" />。</returns>
    public async Task<long?> EstimateRowsAsync(CancellationToken cancellationToken)
    {
        if (_session.Pack.EstimateRowCountSql(_target) is not { } sql)
        {
            return null;
        }
        try
        {
            SqlConnection connection = await ConnectionAsync(cancellationToken).ConfigureAwait(true);
            return await connection.UseAsync(async (raw, token) =>
            {
                await using DbCommand command = raw.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return value is null or DBNull ? null : (long?)Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 估算拿不到只是少一行信息,不该让结构页失败。
            return null;
        }
    }
}
