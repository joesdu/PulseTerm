using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="ITimeSeriesApi" /> 的内存实现:语义对齐真实时序库 ——
/// 同序列(标签组合)同毫秒的点<b>覆盖</b>而非追加,查询按标签过滤、按时间排序并钳制条数,
/// 名称与配额走 <see cref="TimeSeriesValidation" /> 同一套校验。线程安全。
/// </summary>
public sealed class InMemoryTimeSeries : ITimeSeriesApi
{
    private readonly Dictionary<string, InMemorySeries> _series = [with(StringComparer.Ordinal)];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<ITimeSeries> OpenAsync(TimeSeriesDefinition definition, CancellationToken cancellationToken = default)
    {
        TimeSeriesValidation.RequireDefinition(definition);
        lock (_gate)
        {
            if (!_series.TryGetValue(definition.Name, out InMemorySeries? series))
            {
                if (_series.Count >= TimeSeriesLimits.MaxSeriesPerPlugin)
                {
                    throw new InvalidOperationException(
                        $"A plugin may create at most {TimeSeriesLimits.MaxSeriesPerPlugin} time series.");
                }
                series = new(definition.Name);
                _series[definition.Name] = series;
            }
            return Task.FromResult<ITimeSeries>(series);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _series.Keys.Order(StringComparer.Ordinal)]);
        }
    }

    /// <inheritdoc />
    public Task<bool> DropAsync(string name, CancellationToken cancellationToken = default)
    {
        TimeSeriesValidation.RequireName(name, "Measurement name");
        lock (_gate)
        {
            return Task.FromResult(_series.Remove(name));
        }
    }

    /// <summary>一个内存 measurement:点按「标签组合 + 时间戳」唯一。</summary>
    private sealed class InMemorySeries(string name) : ITimeSeries
    {
        private readonly List<TimeSeriesPoint> _points = [];
        private readonly Lock _gate = new();

        public string Name { get; } = name;

        public Task WriteAsync(TimeSeriesPoint point, CancellationToken cancellationToken = default)
            => WriteManyAsync([point], cancellationToken);

        public Task WriteManyAsync(IEnumerable<TimeSeriesPoint> points, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(points);
            List<TimeSeriesPoint> batch = [.. points];
            if (batch.Count > TimeSeriesLimits.MaxWriteBatch)
            {
                throw new ArgumentException($"At most {TimeSeriesLimits.MaxWriteBatch} points per batch.", nameof(points));
            }
            foreach (TimeSeriesPoint point in batch)
            {
                TimeSeriesValidation.RequirePoint(point);
            }
            lock (_gate)
            {
                foreach (TimeSeriesPoint point in batch)
                {
                    int existing = _points.FindIndex(p => p.Timestamp == point.Timestamp && SameTags(p.Tags, point.Tags));
                    if (existing >= 0)
                    {
                        _points[existing] = point; // 同序列同毫秒:覆盖(与真实引擎一致)
                    }
                    else
                    {
                        _points.Add(point);
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TimeSeriesPoint>> QueryAsync(TimeSeriesQuery query, CancellationToken cancellationToken = default)
        {
            int limit = TimeSeriesValidation.NormalizeQuery(query);
            lock (_gate)
            {
                IEnumerable<TimeSeriesPoint> matches = _points.Where(p => Matches(p, query));
                matches = query.Descending
                    ? matches.OrderByDescending(p => p.Timestamp)
                    : matches.OrderBy(p => p.Timestamp);
                return Task.FromResult<IReadOnlyList<TimeSeriesPoint>>([.. matches.Take(limit)]);
            }
        }

        public Task<long> CountAsync(string field, TimeSeriesQuery query, CancellationToken cancellationToken = default)
        {
            TimeSeriesValidation.RequireName(field, "Field name");
            TimeSeriesValidation.NormalizeQuery(query);
            lock (_gate)
            {
                return Task.FromResult(_points.LongCount(p => Matches(p, query) && p.Fields.ContainsKey(field)));
            }
        }

        public Task<IReadOnlyList<string>> DistinctTagValuesAsync(string tag, CancellationToken cancellationToken = default)
        {
            TimeSeriesValidation.RequireName(tag, "Tag name");
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<string>>(
                [
                    .. _points.Select(p => p.Tag(tag)).Where(v => v.Length > 0)
                              .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                ]);
            }
        }

        public Task<int> DeleteAsync(IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default)
        {
            foreach (string key in (tags ?? new Dictionary<string, string>()).Keys)
            {
                TimeSeriesValidation.RequireName(key, "Tag name");
            }
            lock (_gate)
            {
                List<TimeSeriesPoint> doomed = [.. _points.Where(p => MatchesTags(p, tags))];
                int affected = doomed.Select(SeriesKey).Distinct(StringComparer.Ordinal).Count();
                foreach (TimeSeriesPoint point in doomed)
                {
                    _points.Remove(point);
                }
                return Task.FromResult(affected);
            }
        }

        /// <summary>序列标识 = 标签组合(排序后拼接),用于统计受影响的序列数。</summary>
        private static string SeriesKey(TimeSeriesPoint point)
            => string.Join(';', point.Tags.OrderBy(t => t.Key, StringComparer.Ordinal)
                                          .Select(t => $"{t.Key}={t.Value}"));

        private static bool Matches(TimeSeriesPoint point, TimeSeriesQuery query)
            => MatchesTags(point, query.Tags)
               && (query.Since is not { } since || point.Timestamp >= since)
               && (query.Until is not { } until || point.Timestamp <= until);

        private static bool MatchesTags(TimeSeriesPoint point, IReadOnlyDictionary<string, string>? tags)
            => tags is null || tags.Count == 0
               || tags.All(t => point.Tags.TryGetValue(t.Key, out string? value) && string.Equals(value, t.Value, StringComparison.Ordinal));

        private static bool SameTags(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
            => left.Count == right.Count
               && left.All(t => right.TryGetValue(t.Key, out string? value) && string.Equals(value, t.Value, StringComparison.Ordinal));
    }
}
