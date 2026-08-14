namespace VelaShell.PluginSdk.TimeSeries;

/// <summary>
/// 时序契约的校验(名称、配额、取值长度)。宿主与测试替身共用同一套规则 ——
/// 插件在两种宿主上得到完全一致的报错。
/// </summary>
public static class TimeSeriesValidation
{
    /// <summary>
    /// 校验名称:<c>[a-z][a-z0-9_]*</c>,长度 ≤ <see cref="TimeSeriesLimits.MaxNameLength" />。
    /// 限成这个字符集是因为名称会进入时序引擎的标识符位置。
    /// </summary>
    public static string RequireName(string? name, string what)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > TimeSeriesLimits.MaxNameLength)
        {
            throw new ArgumentException($"{what} '{name}' exceeds {TimeSeriesLimits.MaxNameLength} characters.", nameof(name));
        }
        if (!char.IsAsciiLetterLower(name[0]))
        {
            throw new ArgumentException($"{what} '{name}' must start with a lowercase ASCII letter.", nameof(name));
        }
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_')
            {
                throw new ArgumentException($"{what} '{name}' may only contain [a-z0-9_].", nameof(name));
            }
        }
        return name;
    }

    /// <summary>校验 measurement 定义(名称、列名、列数、至少一个字段列、无重名)。</summary>
    public static void RequireDefinition(TimeSeriesDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        RequireName(definition.Name, "Measurement name");
        if (definition.Columns is null || definition.Columns.Count == 0)
        {
            throw new ArgumentException("A time series definition needs at least one column.", nameof(definition));
        }
        if (definition.Columns.Count > TimeSeriesLimits.MaxColumns)
        {
            throw new ArgumentException($"A time series may declare at most {TimeSeriesLimits.MaxColumns} columns.", nameof(definition));
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool hasField = false;
        foreach (TimeSeriesColumn column in definition.Columns)
        {
            RequireName(column.Name, "Column name");
            if (!seen.Add(column.Name))
            {
                throw new ArgumentException($"Duplicate column '{column.Name}'.", nameof(definition));
            }
            if (column.Name == "time")
            {
                throw new ArgumentException("'time' is reserved for the point timestamp.", nameof(definition));
            }
            hasField |= column.Role == TimeSeriesColumnRole.Field;
        }
        if (!hasField)
        {
            throw new ArgumentException("A time series needs at least one field column.", nameof(definition));
        }
    }

    /// <summary>校验一个数据点(标签/字段名合法、标签值与文本字段不超长)。</summary>
    public static void RequirePoint(TimeSeriesPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.Fields is null || point.Fields.Count == 0)
        {
            throw new ArgumentException("A point needs at least one field value.", nameof(point));
        }
        foreach ((string key, string value) in point.Tags ?? new Dictionary<string, string>())
        {
            RequireName(key, "Tag name");
            if ((value?.Length ?? 0) > TimeSeriesLimits.MaxTagValueLength)
            {
                throw new ArgumentException($"Tag '{key}' exceeds {TimeSeriesLimits.MaxTagValueLength} characters — put long text in a field.", nameof(point));
            }
        }
        foreach ((string key, TimeSeriesValue value) in point.Fields)
        {
            RequireName(key, "Field name");
            if (value is { Kind: TimeSeriesValueKind.Text, Text.Length: > TimeSeriesLimits.MaxTextFieldLength })
            {
                throw new ArgumentException($"Field '{key}' exceeds {TimeSeriesLimits.MaxTextFieldLength} characters.", nameof(point));
            }
        }
    }

    /// <summary>校验查询条件里的标签名,并把 <see cref="TimeSeriesQuery.Limit" /> 钳到合法区间。</summary>
    public static int NormalizeQuery(TimeSeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (string key in (query.Tags ?? new Dictionary<string, string>()).Keys)
        {
            RequireName(key, "Tag name");
        }
        return Math.Clamp(query.Limit, 1, TimeSeriesLimits.MaxQueryLimit);
    }
}
