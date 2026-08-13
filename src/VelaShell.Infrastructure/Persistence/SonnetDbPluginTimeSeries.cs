using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// <see cref="ITimeSeriesApi" /> 的 SonnetDB 实现:插件的每个 measurement 落在
/// 宿主同一个时序库里,物理名 = <c>pts_&lt;插件命名空间&gt;_&lt;短名&gt;</c>。
/// <para>
/// 隔离保证:命名空间由插件 id 派生(不可由插件指定),短名字符集限 <c>[a-z0-9_]</c> ——
/// 插件既写不到别家的 measurement,也碰不到宿主自己的 conn_history/audit_log 等;
/// 卸载时按前缀整体 drop(见 <see cref="SonnetDbPluginDataStore.PurgeAsync" />)。
/// </para>
/// <para>
/// 取值一律走 SQL 参数,不拼进语句文本;查询条数由 <see cref="TimeSeriesLimits" /> 钳制。
/// </para>
/// </summary>
public sealed class SonnetDbPluginTimeSeries(SonnetDbEngine engine, string pluginId) : ITimeSeriesApi
{
    private readonly string _prefix = PrefixFor(pluginId);

    /// <summary>物理 measurement 名的固定前缀(全体插件共用,用于与宿主自有 measurement 区分)。</summary>
    public const string PluginMeasurementPrefix = "pts_";

    /// <summary>
    /// 某插件的物理 measurement 前缀:<c>pts_&lt;净化 id&gt;_&lt;id 哈希 8 位&gt;_</c>。
    /// 净化只为可读(id 里的 '.'/'-' 不能进标识符),唯一性由哈希兜底 ——
    /// 「a.b」与「a-b」不会撞进同一命名空间。
    /// </summary>
    public static string PrefixFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        var sanitized = new StringBuilder(pluginId.Length);
        foreach (char c in pluginId)
        {
            sanitized.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }
        string readable = sanitized.ToString();
        if (readable.Length > 24)
        {
            readable = readable[..24];
        }
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(pluginId)).AsSpan(0, 4));
        return $"{PluginMeasurementPrefix}{readable}_{hash}_";
    }

    /// <inheritdoc />
    public async Task<ITimeSeries> OpenAsync(TimeSeriesDefinition definition, CancellationToken cancellationToken = default)
    {
        TimeSeriesValidation.RequireDefinition(definition);
        string physical = _prefix + definition.Name;
        if (await engine.GetMeasurementAsync(physical, cancellationToken).ConfigureAwait(false) is null)
        {
            IReadOnlyList<string> existing = await ListAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Count >= TimeSeriesLimits.MaxSeriesPerPlugin)
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' already has {existing.Count} time series (limit {TimeSeriesLimits.MaxSeriesPerPlugin}).");
            }
        }
        MeasurementSchema schema = await engine.EnsureMeasurementAsync(ToSchema(physical, definition), cancellationToken)
                                               .ConfigureAwait(false);
        await MarkAsync(definition.Name, physical, cancellationToken).ConfigureAwait(false);
        return new DbSeries(engine, definition.Name, schema);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MeasurementSchema> all = await engine.ListMeasurementsAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. all.Select(s => s.Name)
                  .Where(n => n.StartsWith(_prefix, StringComparison.Ordinal))
                  .Select(n => n[_prefix.Length..])
                  .Order(StringComparer.Ordinal)
        ];
    }

    /// <inheritdoc />
    public async Task<bool> DropAsync(string name, CancellationToken cancellationToken = default)
    {
        TimeSeriesValidation.RequireName(name, "Measurement name");
        bool existed = await engine.DropMeasurementAsync(_prefix + name, cancellationToken).ConfigureAwait(false);
        await engine.WithCollectionAsync<object?>(SonnetDbEngine.PluginDataCollection, store =>
        {
            store.Delete(MarkerId(name));
            return null;
        }, cancellationToken).ConfigureAwait(false);
        return existed;
    }

    /// <summary>
    /// 在插件数据集合里留一条 <c>&lt;pluginId&gt;|ts|&lt;短名&gt;</c> 标记文档。
    /// 目的只有一个:让「只用过时序、没写过 KV」的插件同样出现在
    /// <see cref="SonnetDbPluginDataStore.ListPluginIdsAsync" /> 的扫描里 ——
    /// 否则卸载后它的 measurement 无人认领,永远清不掉。
    /// </summary>
    private Task<object?> MarkAsync(string name, string physical, CancellationToken cancellationToken)
        => engine.WithCollectionAsync<object?>(SonnetDbEngine.PluginDataCollection, store =>
        {
            string id = MarkerId(name);
            if (store.Get(id) is null)
            {
                store.Upsert(id, $"{{\"V\":\"{physical}\"}}");
            }
            return null;
        }, cancellationToken);

    private string MarkerId(string name) => $"{pluginId}|ts|{name}";

    private static MeasurementSchema ToSchema(string physicalName, TimeSeriesDefinition definition)
        => MeasurementSchema.Create(physicalName,
        [
            .. definition.Columns.Select(c => new MeasurementColumn(c.Name,
                c.Role == TimeSeriesColumnRole.Tag ? MeasurementColumnRole.Tag : MeasurementColumnRole.Field,
                c.Role == TimeSeriesColumnRole.Tag ? FieldType.String : ToFieldType(c.Kind), null, null))
        ]);

    private static FieldType ToFieldType(TimeSeriesValueKind kind) => kind switch
    {
        TimeSeriesValueKind.Integer => FieldType.Int64,
        TimeSeriesValueKind.Number => FieldType.Float64,
        TimeSeriesValueKind.Flag => FieldType.Boolean,
        _ => FieldType.String
    };

    /// <summary>绑定到单个物理 measurement 的句柄:全部取值经参数化 SQL 进出。</summary>
    private sealed class DbSeries(SonnetDbEngine engine, string shortName, MeasurementSchema schema) : ITimeSeries
    {
        /// <summary>单次查询的扫描上限:超过这么多匹配点时按扫描顺序截断(收窄 Since/Until 才是正解)。</summary>
        private const int MaxScanRows = 4 * TimeSeriesLimits.MaxQueryLimit;

        private readonly string _physical = schema.Name;

        public string Name { get; } = shortName;

        public Task WriteAsync(TimeSeriesPoint point, CancellationToken cancellationToken = default)
            => WriteManyAsync([point], cancellationToken);

        public Task WriteManyAsync(IEnumerable<TimeSeriesPoint> points, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(points);
            TimeSeriesPoint[] batch = [.. points];
            if (batch.Length > TimeSeriesLimits.MaxWriteBatch)
            {
                throw new ArgumentException($"At most {TimeSeriesLimits.MaxWriteBatch} points per batch.", nameof(points));
            }
            foreach (TimeSeriesPoint point in batch)
            {
                TimeSeriesValidation.RequirePoint(point);
            }
            return engine.WriteManyAsync(batch.Select(ToPoint), cancellationToken);
        }

        public async Task<IReadOnlyList<TimeSeriesPoint>> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken = default)
        {
            int limit = TimeSeriesValidation.NormalizeQuery(query);
            var parameters = new SqlParameters();
            string where = BuildWhere(query, parameters);
            string columns = string.Join(", ", new[] { "time" }.Concat(schema.Columns.Select(c => c.Name)));

            // 排序与取数在这里做,不交给 SQL:实测 SonnetDB 的 SELECT 会按「序列分组、组内时间升序」
            // 返回,ORDER BY 方向不生效,LIMIT 也是截扫描结果而非截排序结果 —— 直接下推会拿到错的那几行。
            // SQL 只负责过滤(WHERE 生效)与一个防跑飞的扫描上限。
            SelectExecutionResult result = await engine
                .QueryAsync($"SELECT {columns} FROM {_physical}{where} LIMIT {MaxScanRows}", parameters, cancellationToken)
                .ConfigureAwait(false);
            IEnumerable<TimeSeriesPoint> points = result.Rows.Select(row => ToTimeSeriesPoint(result.Columns, row));
            points = query.Descending
                ? points.OrderByDescending(p => p.Timestamp)
                : points.OrderBy(p => p.Timestamp);
            return [.. points.Take(limit)];
        }

        public async Task<long> CountAsync(string field, TimeSeriesQuery query, CancellationToken cancellationToken = default)
        {
            TimeSeriesValidation.RequireName(field, "Field name");
            TimeSeriesValidation.NormalizeQuery(query);
            var parameters = new SqlParameters();
            string where = BuildWhere(query, parameters);
            SelectExecutionResult result = await engine
                .QueryAsync($"SELECT count({field}) FROM {_physical}{where}", parameters, cancellationToken).ConfigureAwait(false);
            return result.Rows.Count > 0 && result.Rows[0].Count > 0 && result.Rows[0][0] is { } value
                ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
                : 0;
        }

        public async Task<IReadOnlyList<string>> DistinctTagValuesAsync(string tag, CancellationToken cancellationToken = default)
        {
            TimeSeriesValidation.RequireName(tag, "Tag name");
            SelectExecutionResult result = await engine
                .QueryAsync($"SELECT DISTINCT {tag} FROM {_physical}", new(), cancellationToken).ConfigureAwait(false);
            return
            [
                .. result.Rows.Select(row => row.Count > 0 ? row[0]?.ToString() : null)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Select(v => v!)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
            ];
        }

        public async Task<int> DeleteAsync(IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default)
        {
            if (tags is null || tags.Count == 0)
            {
                // 清空整表:drop + 按原 schema 重建,比逐序列删除干净(磁盘也真的回收)。
                await engine.DropMeasurementAsync(_physical, cancellationToken).ConfigureAwait(false);
                await engine.EnsureMeasurementAsync(schema, cancellationToken).ConfigureAwait(false);
                return 1;
            }
            var parameters = new SqlParameters();
            string where = BuildWhere(new() { Tags = tags }, parameters);
            int affected = await engine.TryDeleteAsync($"DELETE FROM {_physical}{where}", parameters, cancellationToken)
                                       .ConfigureAwait(false);
            return Math.Max(affected, 0);
        }

        /// <summary>拼 WHERE 子句:列名来自校验过的标识符,取值一律进参数(参数集就地累加)。</summary>
        private static string BuildWhere(TimeSeriesQuery query, SqlParameters parameters)
        {
            var clauses = new List<string>();
            int index = 0;
            foreach ((string key, string value) in query.Tags ?? new Dictionary<string, string>())
            {
                string name = $"t{index++}";
                clauses.Add($"{TimeSeriesValidation.RequireName(key, "Tag name")} = @{name}");
                parameters.AddNamed(name, value ?? "");
            }
            if (query.Since is { } since)
            {
                clauses.Add("time >= @since");
                parameters.AddNamed("since", since.ToUnixTimeMilliseconds());
            }
            if (query.Until is { } until)
            {
                clauses.Add("time <= @until");
                parameters.AddNamed("until", until.ToUnixTimeMilliseconds());
            }
            return clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
        }

        private Point ToPoint(TimeSeriesPoint point)
            => Point.Create(_physical, point.Timestamp.ToUnixTimeMilliseconds(),
                point.Tags ?? new Dictionary<string, string>(),
                point.Fields.ToDictionary(f => f.Key, f => ToFieldValue(f.Value), StringComparer.Ordinal));

        private static FieldValue ToFieldValue(TimeSeriesValue value) => value.Kind switch
        {
            TimeSeriesValueKind.Integer => FieldValue.FromLong(value.Integer),
            TimeSeriesValueKind.Number => FieldValue.FromDouble(value.Number),
            TimeSeriesValueKind.Flag => FieldValue.FromBool(value.Flag),
            _ => FieldValue.FromString(value.Text ?? "")
        };

        /// <summary>行 → 数据点:按 schema 的列角色/类型还原;缺值的列直接不出现。</summary>
        private TimeSeriesPoint ToTimeSeriesPoint(IReadOnlyList<string> columns, IReadOnlyList<object?> row)
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var fields = new Dictionary<string, TimeSeriesValue>(StringComparer.Ordinal);
            long timestamp = 0;
            for (int i = 0; i < columns.Count && i < row.Count; i++)
            {
                string column = columns[i];
                object? value = row[i];
                if (string.Equals(column, "time", StringComparison.OrdinalIgnoreCase))
                {
                    timestamp = value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (value is null || schema.TryGetColumn(column) is not { } declared)
                {
                    continue;
                }
                if (declared.Role == MeasurementColumnRole.Tag)
                {
                    tags[column] = value.ToString() ?? "";
                    continue;
                }
                fields[column] = declared.DataType switch
                {
                    FieldType.Int64 => TimeSeriesValue.FromInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                    FieldType.Float64 => TimeSeriesValue.FromNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                    FieldType.Boolean => TimeSeriesValue.FromFlag(Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
                    _ => TimeSeriesValue.FromText(value.ToString())
                };
            }
            return new(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), tags, fields);
        }
    }
}
