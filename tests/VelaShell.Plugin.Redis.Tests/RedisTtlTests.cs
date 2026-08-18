using VelaShell.Plugin.Redis;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// TTL 输入的解析。运维脑子里想的是"再放半小时"或"活到明天中午",
/// 逼他先换算成秒是把机器的口径强加给人 —— 所以三种写法都要接。
/// </summary>
[TestClass]
public sealed class RedisTtlTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void PlainNumber_IsSeconds()
    {
        Assert.IsTrue(RedisTtl.TryParse("900", Now, out TimeSpan ttl));
        Assert.AreEqual(TimeSpan.FromSeconds(900), ttl);
    }

    [TestMethod]
    public void SingleUnit_IsParsed()
    {
        Assert.IsTrue(RedisTtl.TryParse("15m", Now, out TimeSpan minutes));
        Assert.AreEqual(TimeSpan.FromMinutes(15), minutes);

        Assert.IsTrue(RedisTtl.TryParse("7d", Now, out TimeSpan days));
        Assert.AreEqual(TimeSpan.FromDays(7), days);

        Assert.IsTrue(RedisTtl.TryParse("90S", Now, out TimeSpan seconds));
        Assert.AreEqual(TimeSpan.FromSeconds(90), seconds);
    }

    [TestMethod]
    public void CompoundDuration_SumsTheParts()
    {
        Assert.IsTrue(RedisTtl.TryParse("2h30m", Now, out TimeSpan ttl));
        Assert.AreEqual(TimeSpan.FromMinutes(150), ttl);

        Assert.IsTrue(RedisTtl.TryParse("1d12h30m15s", Now, out TimeSpan long_));
        Assert.AreEqual(new TimeSpan(1, 12, 30, 15), long_);
    }

    [TestMethod]
    public void AbsoluteTime_BecomesTheRemainingSpan()
    {
        Assert.IsTrue(RedisTtl.TryParse("2026-08-17 18:00:00", Now, out TimeSpan ttl));
        Assert.AreEqual(TimeSpan.FromHours(6), ttl);
    }

    [TestMethod]
    public void AbsoluteTimeInThePast_IsRejected()
    {
        // 不当成"立刻过期":那等于用一个看着像笔误的输入删掉一个键。
        Assert.IsFalse(RedisTtl.TryParse("2020-01-01 00:00:00", Now, out _));
    }

    [TestMethod]
    public void ZeroAndNegative_AreRejected()
    {
        Assert.IsFalse(RedisTtl.TryParse("0", Now, out _));
        Assert.IsFalse(RedisTtl.TryParse("-5", Now, out _));
    }

    [TestMethod]
    public void IncompleteDuration_IsRejected()
    {
        // "2h30" 的 30 没有单位 —— 不猜它是分钟还是秒。
        Assert.IsFalse(RedisTtl.TryParse("2h30", Now, out _));
    }

    [TestMethod]
    public void UnknownUnit_IsRejected()
    {
        Assert.IsFalse(RedisTtl.TryParse("5w", Now, out _));
        Assert.IsFalse(RedisTtl.TryParse("abc", Now, out _));
        Assert.IsFalse(RedisTtl.TryParse("", Now, out _));
        Assert.IsFalse(RedisTtl.TryParse(null, Now, out _));
    }

    [TestMethod]
    public void Describe_UsesDaysForLongSpansAndClockForShort()
    {
        Assert.AreEqual("2d 3h", RedisTtl.Describe(new TimeSpan(2, 3, 4, 5)));
        Assert.AreEqual("3d", RedisTtl.Describe(TimeSpan.FromDays(3)));
        Assert.AreEqual("2:03:04", RedisTtl.Describe(new TimeSpan(2, 3, 4)));
        Assert.AreEqual("03:04", RedisTtl.Describe(new TimeSpan(0, 3, 4)));
        Assert.AreEqual("0", RedisTtl.Describe(TimeSpan.Zero));
    }
}
