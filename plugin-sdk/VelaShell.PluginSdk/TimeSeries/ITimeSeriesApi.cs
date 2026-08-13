namespace VelaShell.PluginSdk.TimeSeries;

/// <summary>
/// 时序能力:插件私有的嵌入式时序库(宿主同一个 SonnetDB 实例,按插件 id 命名空间隔离)。
/// 适合「按时间追加、按标签检索」的数据 —— 会话记录、指标采样、事件流;
/// 小配置仍用 <see cref="Storage.IPluginStorage" />。
/// <para>
/// 隔离保证:measurement 名由宿主加插件前缀,插件读不到别家数据;卸载插件时整体删除。
/// 无数据库的宿主(headless 测试)上调用会抛 <see cref="InvalidOperationException" />。
/// </para>
/// </summary>
public interface ITimeSeriesApi
{
    /// <summary>
    /// 打开(必要时创建)一个 measurement。已存在时沿用既有 schema,
    /// <paramref name="definition" /> 的列变化不会自动迁移。
    /// </summary>
    Task<ITimeSeries> OpenAsync(TimeSeriesDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>列出本插件已创建的 measurement 名(插件视角的短名)。</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>删除一个 measurement 及其全部数据;返回此前是否存在。</summary>
    Task<bool> DropAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// 一个已打开的 measurement 句柄。实现线程安全;调用一律异步且不阻塞 UI 线程。
/// </summary>
public interface ITimeSeries
{
    /// <summary>插件视角的 measurement 名(即 <see cref="TimeSeriesDefinition.Name" />)。</summary>
    string Name { get; }

    /// <summary>写入一个点(同序列同毫秒会覆盖,见 <see cref="TimeSeriesPoint" /> 的说明)。</summary>
    Task WriteAsync(TimeSeriesPoint point, CancellationToken cancellationToken = default);

    /// <summary>批量写入(单批 ≤ <see cref="TimeSeriesLimits.MaxWriteBatch" /> 个点)。</summary>
    Task WriteManyAsync(IEnumerable<TimeSeriesPoint> points, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件查询数据点:先按标签/时间过滤,再按时间排序取 <see cref="TimeSeriesQuery.Limit" /> 条。
    /// 匹配点极多(数万级)时扫描会被宿主截断 —— 请用 <see cref="TimeSeriesQuery.Since" /> /
    /// <see cref="TimeSeriesQuery.Until" /> 或更细的标签收窄范围。
    /// </summary>
    Task<IReadOnlyList<TimeSeriesPoint>> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计匹配条件、且 <paramref name="field" /> 有值的点数
    /// (时序引擎按列计数 —— 缺该字段的点不计入)。
    /// </summary>
    Task<long> CountAsync(string field, TimeSeriesQuery query, CancellationToken cancellationToken = default);

    /// <summary>列出某个标签列出现过的全部取值(去重)。</summary>
    Task<IReadOnlyList<string>> DistinctTagValuesAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除匹配标签的序列数据;返回受影响的序列数。
    /// <paramref name="tags" /> 为空表示清空整个 measurement 的数据(保留 schema)。
    /// </summary>
    Task<int> DeleteAsync(IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default);
}
