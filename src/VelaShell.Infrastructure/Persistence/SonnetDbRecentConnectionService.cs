using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using VelaShell.Core.Data;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 基于 SonnetDB 时序 measurement <c>conn_history</c> 的连接历史:
/// 每次连接写入一个数据点(tags: profile_id/host/username;fields: name/group_name/port/connection_type/success/duration_ms),
/// “最近连接”按时间倒序查询并对同一目标去重。
/// </summary>
public sealed class SonnetDbRecentConnectionService(SonnetDbEngine engine) : IRecentConnectionService
{
    private readonly SonnetDbEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>
    /// 将一次连接记录为 <c>conn_history</c> measurement 的一个数据点。
    /// </summary>
    public Task RecordAsync(RecentConnectionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // SonnetDB 不允许空 tag 值:临时连接(无配置)不写 profile_id。
        var tags = new Dictionary<string, string>();
        if (entry.ProfileId is { } profileId)
        {
            tags["profile_id"] = profileId.ToString("D");
        }
        if (!string.IsNullOrEmpty(entry.Host))
        {
            tags["host"] = entry.Host;
        }
        if (!string.IsNullOrEmpty(entry.Username))
        {
            tags["username"] = entry.Username;
        }
        var fields = new Dictionary<string, FieldValue>
        {
            ["name"] = FieldValue.FromString(entry.Name),
            ["group_name"] = FieldValue.FromString(entry.GroupName),
            ["port"] = FieldValue.FromLong(entry.Port),
            ["connection_type"] = FieldValue.FromLong((int)entry.ConnectionType),
            ["success"] = FieldValue.FromBool(entry.Success),
            ["duration_ms"] = FieldValue.FromLong(entry.DurationMs)
        };
        return _engine.WritePointAsync(SonnetDbEngine.ConnHistoryMeasurement, entry.ConnectedAt, tags, fields, cancellationToken);
    }

    /// <summary>
    /// 按时间倒序返回最近成功的连接,并对同一目标去重,最多 <paramref name="limit" /> 条。
    /// </summary>
    public async Task<List<RecentConnectionEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // 多取一些再去重,保证同一目标反复连接时仍能凑满 limit 个不同目标。
        int fetch = Math.Clamp(limit * 10, 50, 500);
        SelectExecutionResult result = await _engine.QueryAsync($"SELECT time, profile_id, host, username, name, group_name, port, connection_type, success FROM {SonnetDbEngine.ConnHistoryMeasurement} ORDER BY time DESC LIMIT {fetch}",
                                           cancellationToken).ConfigureAwait(false);
        var entries = new List<RecentConnectionEntry>(limit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyList<object?> row in result.Rows)
        {
            RecentConnectionEntry? entry = MapRow(result.Columns, row);
            if (entry is null || !entry.Success)
            {
                continue;
            }
            string key = entry.ProfileId is { } id
                             ? "p:" + id.ToString("D")
                             : $"h:{entry.Username}@{entry.Host}:{entry.Port}";
            if (!seen.Add(key))
            {
                continue;
            }
            entries.Add(entry);
            if (entries.Count >= limit)
            {
                break;
            }
        }
        return entries;
    }

    /// <summary>
    /// 清空全部连接历史(重置 <c>conn_history</c> measurement)。
    /// </summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) => _engine.ResetMeasurementAsync(SonnetDbEngine.ConnHistoryMeasurement, cancellationToken);

    private static RecentConnectionEntry? MapRow(IReadOnlyList<string> columns, IReadOnlyList<object?> row)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count && i < row.Count; i++)
        {
            values[columns[i]] = row[i];
        }
        if (!values.TryGetValue("time", out object? time) || time is null)
        {
            return null;
        }
        string profileIdRaw = AsString(values.GetValueOrDefault("profile_id"));
        return new()
        {
            ConnectedAt = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(time)),
            ProfileId = Guid.TryParse(profileIdRaw, out Guid id) ? id : null,
            Host = AsString(values.GetValueOrDefault("host")),
            Username = AsString(values.GetValueOrDefault("username")),
            ConnectionType = ParseConnectionType(values.GetValueOrDefault("connection_type")),
            Name = AsString(values.GetValueOrDefault("name")),
            GroupName = AsString(values.GetValueOrDefault("group_name")),
            Port = values.GetValueOrDefault("port") is { } port ? (int)Convert.ToInt64(port) : 22,
            Success = values.GetValueOrDefault("success") switch
            {
                bool b => b,
                null => true,
                var other => Convert.ToBoolean(other)
            }
        };
    }

    private static string AsString(object? value) => value?.ToString() ?? string.Empty;

    /// <summary>
    /// 时序库里协议存的是 Int64。白名单化(<see cref="Enum.IsDefined{TEnum}" />)而非逐值比对:
    /// 未知值仍降级为 SSH,但新增协议无需再改这里。
    /// </summary>
    private static ConnectionType ParseConnectionType(object? value) =>
        value is long raw && Enum.IsDefined((ConnectionType)raw)
            ? (ConnectionType)raw
            : ConnectionType.SSH;
}
