using System.Globalization;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 插件私有的持久化:收藏的键(<c>Storage</c>)与控制台命令历史(<c>TimeSeries</c>)。
/// <para>
/// 命令历史落时序库不只是"下次还能按 ↑" —— 它同时兑现了设计文档 §7.3 那条:
/// **"谁在什么时候对生产库敲了什么"是可回溯的**。这对团队场景的价值高于任何单机 GUI,
/// 而它只可能出现在一个本身就有审计文化的宿主里。
/// </para>
/// <para>
/// 全部方法**对不可用的后端静默降级**:headless 宿主(单测)没有数据库,
/// <c>TimeSeries.OpenAsync</c> 会抛 —— 那时收藏与历史只是本次会话内有效,
/// 而不是让面板打不开。
/// </para>
/// </summary>
/// <param name="context">插件上下文。</param>
internal sealed class RedisStore(IPluginContext context)
{
    /// <summary>时序表名(SDK 要求 <c>[a-z][a-z0-9_]*</c>)。</summary>
    private const string HistoryMeasurement = "console_history";

    /// <summary>单条连接保留的历史条数。再多对"按 ↑ 找上次那条"没有帮助,只是占地方。</summary>
    private const int HistoryLimit = 200;

    private ITimeSeries? _history;
    private bool _historyUnavailable;
    private readonly TimeSeriesClock _clock = new();

    /// <summary>收藏的键。按连接分开存 —— 同一个键名在两台服务器上是两件事。</summary>
    /// <param name="connectionKey">连接标识(端点)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>收藏的键名(显示形式)。</returns>
    public async Task<IReadOnlyList<string>> LoadFavoritesAsync(
        string connectionKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string[]? saved = await context.Storage
                .GetAsync<string[]>(FavoritesKey(connectionKey), cancellationToken).ConfigureAwait(false);
            return saved ?? [];
        }
        catch (Exception ex)
        {
            context.Log.Info($"Reading favorites failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>保存收藏。</summary>
    /// <param name="connectionKey">连接标识。</param>
    /// <param name="keys">收藏的键名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task SaveFavoritesAsync(
        string connectionKey,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await context.Storage
                .SetAsync(FavoritesKey(connectionKey), keys.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 收藏存不下不该影响正在做的事:如实记一条日志,界面上那颗星仍然亮着
            // (本次会话内有效),下次打开会发现它没了 —— 比弹一个错误框好。
            context.Log.Info($"Saving favorites failed: {ex.Message}");
        }
    }

    /// <summary>追加一条控制台命令历史。</summary>
    /// <param name="connectionKey">连接标识。</param>
    /// <param name="command">命令行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task AppendHistoryAsync(
        string connectionKey,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (await OpenHistoryAsync(cancellationToken).ConfigureAwait(false) is not { } series)
        {
            return;
        }
        try
        {
            await series.WriteAsync(new(
                // 严格递增的时间戳:同序列同毫秒会被覆盖,而连着敲两条命令是常事。
                _clock.Next(),
                new Dictionary<string, string> { ["conn"] = connectionKey },
                new Dictionary<string, TimeSeriesValue> { ["cmd"] = TimeSeriesValue.FromText(command) }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Info($"Appending console history failed: {ex.Message}");
        }
    }

    /// <summary>读回控制台历史(旧的在前,与 ↑ 的遍历顺序一致)。</summary>
    /// <param name="connectionKey">连接标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命令行。</returns>
    public async Task<IReadOnlyList<string>> LoadHistoryAsync(
        string connectionKey,
        CancellationToken cancellationToken = default)
    {
        if (await OpenHistoryAsync(cancellationToken).ConfigureAwait(false) is not { } series)
        {
            return [];
        }
        try
        {
            IReadOnlyList<TimeSeriesPoint> points = await series.QueryAsync(new()
            {
                Tags = new Dictionary<string, string> { ["conn"] = connectionKey },
                Descending = false,
                Limit = HistoryLimit
            }, cancellationToken).ConfigureAwait(false);
            return
            [
                .. points
                    .Select(point => point.Fields.TryGetValue("cmd", out TimeSeriesValue value) ? value.Text : null)
                    .Where(static text => !string.IsNullOrEmpty(text))
                    .Select(static text => text!)
            ];
        }
        catch (Exception ex)
        {
            context.Log.Info($"Reading console history failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 惰性打开时序表。**只试一次** —— headless 宿主上它每次都会抛,
    /// 每敲一条命令都重试一遍只会往日志里灌噪音。
    /// </summary>
    private async Task<ITimeSeries?> OpenHistoryAsync(CancellationToken cancellationToken)
    {
        if (_history is not null)
        {
            return _history;
        }
        if (_historyUnavailable)
        {
            return null;
        }
        try
        {
            _history = await context.TimeSeries.OpenAsync(new(HistoryMeasurement,
            [
                TimeSeriesColumn.Tag("conn"),
                TimeSeriesColumn.Field("cmd", TimeSeriesValueKind.Text)
            ]), cancellationToken).ConfigureAwait(false);
            return _history;
        }
        catch (Exception ex)
        {
            // 无 DB 的宿主(单测/headless):历史只在本次会话内有效,如实降级。
            _historyUnavailable = true;
            context.Log.Info($"Console history is not persisted: {ex.Message}");
            return null;
        }
    }

    private static string FavoritesKey(string connectionKey) =>
        string.Create(CultureInfo.InvariantCulture, $"favorites:{connectionKey}");
}
