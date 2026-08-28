using System.Collections.Concurrent;
using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;

namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 嵌入式 SonnetDB 引擎的单例封装:负责打开数据库、初始化文档集合与时序 measurement,
/// 并对上层仓储提供带锁的文档/时序访问原语。
/// 数据模型:
/// - 文档集合(业务数据,JSON):session_groups、session_profiles($.groupId 索引)、
/// app_config(settings/state 单文档)、known_hosts、ui_config、quick_commands。
/// - 时序 measurement(时间相关数据):conn_history(最近连接)、audit_log(安全审计)、
/// session_recording_chunks(会话录制),以及插件私有的 pts_* (见 SonnetDbPluginTimeSeries)。
/// </summary>
public sealed class SonnetDbEngine : IDisposable
{
    /// <summary>服务器分组文档集合名。</summary>
    public const string GroupsCollection = "session_groups";
    /// <summary>会话连接配置文档集合名(按 $.groupId 建索引)。</summary>
    public const string ProfilesCollection = "session_profiles";
    /// <summary>应用配置文档集合名(settings/state 等单文档)。</summary>
    public const string ConfigCollection = "app_config";
    /// <summary>已知主机指纹文档集合名。</summary>
    public const string KnownHostsCollection = "known_hosts";
    /// <summary>界面状态文档集合名。</summary>
    public const string UiConfigCollection = "ui_config";
    /// <summary>快捷命令片段文档集合名。</summary>
    public const string QuickCommandsCollection = "quick_commands";
    /// <summary>会话录制元数据文档集合名。</summary>
    public const string RecordingsCollection = "recordings";

    /// <summary>
    /// 插件数据文档集合名(KV 与机密)。单集合 + 复合主键 <c>&lt;pluginId&gt;|&lt;kind&gt;|&lt;key&gt;</c>:
    /// 插件 id 字符集为 [a-z0-9.-],不含分隔符 '|',命名空间不可逃逸;
    /// 按前缀扫描即可整体清除某插件的数据(卸载清理)。
    /// </summary>
    public const string PluginDataCollection = "plugin_data";

    /// <summary>最近连接历史时序 measurement 名。</summary>
    public const string ConnHistoryMeasurement = "conn_history";
    /// <summary>安全审计日志时序 measurement 名。</summary>
    public const string AuditLogMeasurement = "audit_log";
    /// <summary>会话录制分块数据时序 measurement 名。</summary>
    public const string RecordingChunksMeasurement = "session_recording_chunks";

    /// <summary>
    /// 段文件改走内存映射的体积阈值。SonnetDB 默认只对大于 64MB 的段用 mmap,比这小的段在
    /// <c>Tsdb.Open</c> 里整个读进托管堆 —— 而会话录制每次刷盘都落一个新段,几 GB 录制数据
    /// 就是几十上百个不足 64MB 的小段,于是"开库即全量入堆",内存直接顶到数据体积。
    /// 实测:60 个 10MB 段(共 600MB)在默认阈值下开库后托管堆 601.7MB,阈值改成 1MB 后只剩 0.4MB。
    /// 代价:Windows 上被映射的段文件删不掉,DropMeasurement 腾出的字节要等下次开库才真正落盘
    /// (清理入口据此提示"重启后彻底释放")。
    /// </summary>
    private const long SegmentMemoryMapThresholdBytes = 1024 * 1024;

    private readonly Tsdb _db;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DocumentCollectionStore> _stores = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>以应用存储路径中的 SonnetDB 目录构造引擎。</summary>
    public SonnetDbEngine(VelaShellStoragePaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).SonnetDbDirectory) { }

    /// <summary>以指定根目录打开(必要时创建)SonnetDB 并初始化 schema。</summary>
    public SonnetDbEngine(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(@"SonnetDB root directory is required.", nameof(rootDirectory));
        }
        Directory.CreateDirectory(rootDirectory);
        RootDirectory = rootDirectory;
        _db = Tsdb.Open(new()
        {
            RootDirectory = rootDirectory,
            SegmentReaderOptions = new()
            {
                UseMemoryMappedFileForLargeSegments = true,
                MemoryMappedFileThresholdBytes = SegmentMemoryMapThresholdBytes
            }
        });
        EnsureSchema();
    }

    /// <summary>数据库根目录(统计磁盘占用用)。</summary>
    public string RootDirectory { get; }

    /// <summary>释放所有文档集合存储与底层数据库句柄;线程安全且幂等。</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (DocumentCollectionStore store in _stores.Values)
            {
                store.Dispose();
            }
            _stores.Clear();
            _db.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>在引擎锁内执行文档集合操作。</summary>
    public async Task<T> WithCollectionAsync<T>(string collection, Func<DocumentCollectionStore, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return action(OpenStore(collection));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>写入一条时序数据点。</summary>
    public async Task WritePointAsync(
        string measurement,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, FieldValue> fields,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _db.Write(Point.Create(measurement, timestamp.ToUnixTimeMilliseconds(), tags, fields));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>批量写入时序数据点(单次入库,少一轮加锁)。</summary>
    public async Task WriteManyAsync(IEnumerable<Point> points, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        Point[] batch = [.. points];
        if (batch.Length == 0)
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _db.WriteMany(batch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 执行带参数的时序 SQL 查询(SELECT)。取值一律走参数,不拼进 SQL 文本 ——
    /// 插件提供的标签值等外来输入由此与语法隔离。
    /// </summary>
    public async Task<SelectExecutionResult> QueryAsync(string sql, SqlParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return SqlExecutor.Execute(_db, null, sql, parameters, null) switch
            {
                SelectExecutionResult select => select,
                var other => throw new InvalidOperationException($"Expected a SELECT result but got {other?.GetType().Name ?? "null"} for: {sql}")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 执行带参数的 DELETE;返回受影响的序列数,方言不支持或语句失败时返回 -1
    /// (调用方自行降级,与 <see cref="TryExecuteAsync(string,CancellationToken)" /> 同纪律)。
    /// </summary>
    public async Task<int> TryDeleteAsync(string sql, SqlParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return SqlExecutor.Execute(_db, null, sql, parameters, null) is DeleteExecutionResult result
                ? result.SeriesAffected
                : -1;
        }
        catch
        {
            return -1;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按给定 schema 创建 measurement(已存在则原样沿用);返回生效的 schema。</summary>
    public async Task<MeasurementSchema> EnsureMeasurementAsync(MeasurementSchema schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_db.Measurements.TryGet(schema.Name) is { } existing)
            {
                return existing;
            }
            _db.CreateMeasurement(schema);
            return schema;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>取 measurement 的 schema;不存在时返回 <see langword="null" />。</summary>
    public async Task<MeasurementSchema?> GetMeasurementAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _db.Measurements.TryGet(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>列出全部 measurement 的 schema 快照。</summary>
    public async Task<IReadOnlyList<MeasurementSchema>> ListMeasurementsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _db.Measurements.Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除 measurement 及其数据;返回此前是否存在。</summary>
    public async Task<bool> DropMeasurementAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return _db.DropMeasurement(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>执行时序 SQL 查询(SELECT)。</summary>
    public async Task<SelectExecutionResult> QueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return SqlExecutor.Execute(_db, sql) switch
            {
                SelectExecutionResult select => select,
                var other => throw new InvalidOperationException($"Expected a SELECT result but got {other?.GetType().Name ?? "null"} for: {sql}")
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 执行非查询时序 SQL(如 DELETE);返回是否成功。SonnetDB 的 SQL 方言若不支持
    /// 该语句则返回 false,调用方自行降级(例如仅删元数据,数据块留作不可见孤儿)。
    /// </summary>
    public async Task<bool> TryExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SqlExecutor.Execute(_db, sql);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 把内存表立刻刷成段文件。批量回灌大量数据时必须周期性调用 —— 否则写进去的点会一直堆在
    /// 内存表里(默认要攒满 100 万点或 5 分钟才自动刷),搬运几 GB 录制就等于把几 GB 顶进内存。
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _db.FlushNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除并重建一个 measurement(用于清空历史)。</summary>
    public async Task ResetMeasurementAsync(string measurement, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _db.DropMeasurement(measurement);
            CreateMeasurementIfMissing(measurement);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DocumentCollectionStore OpenStore(string collection) =>
        _stores.GetOrAdd(collection, name =>
        {
            CreateCollectionIfMissing(name);
            return _db.Documents.Open(name);
        });

    private void EnsureSchema()
    {
        CreateCollectionIfMissing(GroupsCollection);
        CreateCollectionIfMissing(ProfilesCollection, new DocumentPathIndexDefinition("idx_group", "$.groupId"));
        CreateCollectionIfMissing(ConfigCollection);
        CreateCollectionIfMissing(KnownHostsCollection);
        CreateCollectionIfMissing(UiConfigCollection);
        CreateCollectionIfMissing(QuickCommandsCollection);
        CreateCollectionIfMissing(RecordingsCollection);
        CreateCollectionIfMissing(PluginDataCollection);
        CreateMeasurementIfMissing(ConnHistoryMeasurement);
        CreateMeasurementIfMissing(AuditLogMeasurement);
        CreateMeasurementIfMissing(RecordingChunksMeasurement);
    }

    private void CreateCollectionIfMissing(string name, params DocumentPathIndexDefinition[] indexes)
    {
        if (_db.Documents.Catalog.TryGet(name) is null)
        {
            _db.Documents.Create(DocumentCollectionSchema.Create(name, indexes.Length == 0 ? null : indexes));
        }
    }

    private void CreateMeasurementIfMissing(string name)
    {
        if (_db.Measurements.Contains(name))
        {
            return;
        }
        MeasurementSchema schema = name switch
        {
            ConnHistoryMeasurement => MeasurementSchema.Create(name,
            [
                new("profile_id", MeasurementColumnRole.Tag, FieldType.String),
                new("host", MeasurementColumnRole.Tag, FieldType.String),
                new("username", MeasurementColumnRole.Tag, FieldType.String),
                new("name", MeasurementColumnRole.Field, FieldType.String),
                new("group_name", MeasurementColumnRole.Field, FieldType.String),
                new("port", MeasurementColumnRole.Field, FieldType.Int64),
                new("connection_type", MeasurementColumnRole.Field, FieldType.Int64),
                new("success", MeasurementColumnRole.Field, FieldType.Boolean),
                new("duration_ms", MeasurementColumnRole.Field, FieldType.Int64)
            ]),
            AuditLogMeasurement => MeasurementSchema.Create(name,
            [
                new("category", MeasurementColumnRole.Tag, FieldType.String),
                new("action", MeasurementColumnRole.Tag, FieldType.String),
                new("profile_id", MeasurementColumnRole.Tag, FieldType.String),
                new("detail", MeasurementColumnRole.Field, FieldType.String)
            ]),

            // 会话录制(设置 → 安全审计):终端原始输出按时间分块存储,
            // offset_ms = 相对录制开始的毫秒偏移(回放时间轴),data = Base64 输出字节。
            RecordingChunksMeasurement => MeasurementSchema.Create(name,
            [
                new("recording_id", MeasurementColumnRole.Tag, FieldType.String),
                new("offset_ms", MeasurementColumnRole.Field, FieldType.Int64),
                new("data", MeasurementColumnRole.Field, FieldType.String)
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, @"Unknown measurement.")
        };
        _db.CreateMeasurement(schema);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
