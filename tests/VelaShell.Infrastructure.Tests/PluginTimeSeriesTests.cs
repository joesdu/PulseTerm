using VelaShell.Infrastructure.Persistence;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 插件时序能力(SonnetDB 后端)的行为守护:命名空间隔离、查询过滤、
/// 删除、配额与卸载清理。
/// </summary>
[TestClass]
public sealed class PluginTimeSeriesTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly string _testDirectory;

    private static readonly TimeSeriesDefinition ChatDefinition = new("chat_messages",
    [
        TimeSeriesColumn.Tag("conv"),
        TimeSeriesColumn.Field("role", TimeSeriesValueKind.Text),
        TimeSeriesColumn.Field("seq", TimeSeriesValueKind.Integer),
        TimeSeriesColumn.Field("text", TimeSeriesValueKind.Text),
        TimeSeriesColumn.Field("done", TimeSeriesValueKind.Flag),
        TimeSeriesColumn.Field("cost", TimeSeriesValueKind.Number)
    ]);

    public PluginTimeSeriesTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_tstest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    private static TimeSeriesPoint Message(DateTimeOffset at, string conv, long seq, string role, string text)
        => new(at, new Dictionary<string, string> { ["conv"] = conv }, new Dictionary<string, TimeSeriesValue>
        {
            ["role"] = TimeSeriesValue.FromText(role),
            ["seq"] = TimeSeriesValue.FromInteger(seq),
            ["text"] = TimeSeriesValue.FromText(text),
            ["done"] = TimeSeriesValue.FromFlag(true),
            ["cost"] = TimeSeriesValue.FromNumber(seq * 0.5)
        });

    [TestMethod]
    public async Task WriteAndQuery_RoundTripsAllValueKinds_AndFiltersByTag()
    {
        ITimeSeriesApi api = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        ITimeSeries series = await api.OpenAsync(ChatDefinition);
        var clock = new TimeSeriesClock();
        await series.WriteManyAsync(
        [
            Message(clock.Next(), "c1", 0, "user", "你好 'quoted' \"double\"\n换行"),
            Message(clock.Next(), "c1", 1, "assistant", "hi"),
            Message(clock.Next(), "c2", 0, "user", "other conversation")
        ]);

        IReadOnlyList<TimeSeriesPoint> all = await series.QueryAsync(new() { Descending = false });
        Assert.HasCount(3, all);

        IReadOnlyList<TimeSeriesPoint> conversation = await series.QueryAsync(new()
        {
            Tags = new Dictionary<string, string> { ["conv"] = "c1" },
            Descending = false
        });
        Assert.HasCount(2, conversation);
        Assert.AreEqual("user", conversation[0].Text("role"));
        Assert.AreEqual("你好 'quoted' \"double\"\n换行", conversation[0].Text("text"), "引号与换行不应破坏存取");
        Assert.AreEqual(1, conversation[1].Integer("seq"));
        Assert.IsTrue(conversation[1].Field("done")!.Value.AsFlag());
        Assert.AreEqual(0.5, conversation[1].Field("cost")!.Value.Number, 0.0001);
        Assert.AreEqual("c1", conversation[0].Tag("conv"));
    }

    [TestMethod]
    public async Task Query_HonoursDescendingLimitAndTimeRange()
    {
        ITimeSeriesApi api = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        ITimeSeries series = await api.OpenAsync(ChatDefinition);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            await series.WriteAsync(Message(start.AddMilliseconds(i), "c1", i, "user", $"m{i}"));
        }

        IReadOnlyList<TimeSeriesPoint> newest = await series.QueryAsync(new() { Limit = 2 });
        Assert.HasCount(2, newest);
        Assert.AreEqual(4, newest[0].Integer("seq"), "倒序应先给最新的点");
        Assert.AreEqual(3, newest[1].Integer("seq"));

        IReadOnlyList<TimeSeriesPoint> window = await series.QueryAsync(new()
        {
            Since = start.AddMilliseconds(1),
            Until = start.AddMilliseconds(3),
            Descending = false
        });
        Assert.HasCount(3, window);
        Assert.AreEqual(1, window[0].Integer("seq"));
    }

    [TestMethod]
    public async Task SameTimestampInSameSeries_Overwrites_ClockAvoidsIt()
    {
        ITimeSeriesApi api = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        ITimeSeries series = await api.OpenAsync(ChatDefinition);
        DateTimeOffset at = DateTimeOffset.UtcNow;
        await series.WriteAsync(Message(at, "c1", 0, "user", "first"));
        await series.WriteAsync(Message(at, "c1", 1, "user", "second"));
        IReadOnlyList<TimeSeriesPoint> collided = await series.QueryAsync(new());
        Assert.HasCount(1, collided, "同序列同毫秒是覆盖语义 —— 这正是需要 TimeSeriesClock 的原因");

        var clock = new TimeSeriesClock();
        clock.Observe(at);
        await series.WriteManyAsync([
            Message(clock.Next(), "c2", 0, "user", "first"),
            Message(clock.Next(), "c2", 1, "user", "second")
        ]);
        Assert.HasCount(2, await series.QueryAsync(new() { Tags = new Dictionary<string, string> { ["conv"] = "c2" } }));
    }

    [TestMethod]
    public async Task CountDistinctAndDelete_WorkPerConversation()
    {
        ITimeSeriesApi api = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        ITimeSeries series = await api.OpenAsync(ChatDefinition);
        var clock = new TimeSeriesClock();
        await series.WriteManyAsync(
        [
            Message(clock.Next(), "c1", 0, "user", "a"),
            Message(clock.Next(), "c1", 1, "assistant", "b"),
            Message(clock.Next(), "c2", 0, "user", "c")
        ]);

        Assert.AreEqual(2, await series.CountAsync("seq", new() { Tags = new Dictionary<string, string> { ["conv"] = "c1" } }));
        Assert.AreSequenceEqual(new[] { "c1", "c2" }, [.. (await series.DistinctTagValuesAsync("conv"))], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);

        await series.DeleteAsync(new Dictionary<string, string> { ["conv"] = "c1" });
        Assert.IsEmpty(await series.QueryAsync(new() { Tags = new Dictionary<string, string> { ["conv"] = "c1" } }));
        Assert.HasCount(1, await series.QueryAsync(new() { Tags = new Dictionary<string, string> { ["conv"] = "c2" } }));

        await series.DeleteAsync();
        Assert.IsEmpty(await series.QueryAsync(new()));
        Assert.Contains("chat_messages", [.. (await api.ListAsync())], "清空数据不应删掉 measurement 本身");
    }

    [TestMethod]
    public async Task Plugins_CannotSeeEachOthersSeries()
    {
        ITimeSeriesApi ai = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        ITimeSeriesApi other = new SonnetDbPluginTimeSeries(_engine, "velashell.other");
        ITimeSeries aiSeries = await ai.OpenAsync(ChatDefinition);
        ITimeSeries otherSeries = await other.OpenAsync(ChatDefinition);
        await aiSeries.WriteAsync(Message(DateTimeOffset.UtcNow, "c1", 0, "user", "secret"));

        Assert.IsEmpty(await otherSeries.QueryAsync(new()), "同名 measurement 在不同插件下必须是两张表");
        Assert.HasCount(1, await aiSeries.QueryAsync(new()));
        Assert.AreNotEqual(SonnetDbPluginTimeSeries.PrefixFor("velashell.ai"),
            SonnetDbPluginTimeSeries.PrefixFor("velashell-ai"), "'.' 与 '-' 净化后不能撞进同一命名空间");
    }

    [TestMethod]
    public async Task Purge_DropsPluginMeasurements()
    {
        var store = new SonnetDbPluginDataStore(_engine, null);
        ITimeSeries series = await store.CreateTimeSeries("velashell.ai").OpenAsync(ChatDefinition);
        await series.WriteAsync(Message(DateTimeOffset.UtcNow, "c1", 0, "user", "x"));
        Assert.Contains("velashell.ai", [.. (await store.ListPluginIdsAsync())],
            "只用过时序的插件也要出现在扫描里,否则卸载后清不掉");

        await store.PurgeAsync("velashell.ai");
        Assert.IsEmpty(await store.CreateTimeSeries("velashell.ai").ListAsync());
    }

    [TestMethod]
    public async Task Open_RejectsBadNamesAndEnforcesQuota()
    {
        ITimeSeriesApi api = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await api.OpenAsync(new("Bad Name", [TimeSeriesColumn.Field("v", TimeSeriesValueKind.Integer)])));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await api.OpenAsync(new("only_tags", [TimeSeriesColumn.Tag("t")])));

        for (int i = 0; i < TimeSeriesLimits.MaxSeriesPerPlugin; i++)
        {
            await api.OpenAsync(new($"series_{i}", [TimeSeriesColumn.Field("v", TimeSeriesValueKind.Integer)]));
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await api.OpenAsync(new("one_too_many", [TimeSeriesColumn.Field("v", TimeSeriesValueKind.Integer)])));
    }

    [TestMethod]
    public async Task Drop_RemovesSeriesAndItsMarker()
    {
        ITimeSeriesApi api = new SonnetDbPluginTimeSeries(_engine, "velashell.ai");
        await api.OpenAsync(ChatDefinition);
        Assert.IsTrue(await api.DropAsync("chat_messages"));
        Assert.IsEmpty(await api.ListAsync());
        Assert.IsFalse(await api.DropAsync("chat_messages"));
    }
}
