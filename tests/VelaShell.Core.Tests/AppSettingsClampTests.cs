using VelaShell.Core.Models;

namespace VelaShell.Core.Tests;

/// <summary>
/// <see cref="AppSettings.Normalize" /> 对数值项的钳制。
/// </summary>
/// <remarks>
/// 此前 <c>Normalize</c> 只做字段迁移、不钳制任何数值。设置页的 <c>NumericUpDown</c>
/// 拦得住手输,拦不住磁盘上的内容 —— 一份损坏的、手改过的、或更早版本写的配置能把
/// <c>ScrollbackLines</c> 写成天文数字(200 列 × 20 万行 × 16 字节 ≈ 640 MB / 标签)、
/// 把字号写成 0、把端口写成 -1。
/// </remarks>
[TestClass]
[TestCategory("Settings")]
public sealed class AppSettingsClampTests
{
    [TestMethod]
    public void AbsurdScrollback_IsClampedToTheSupportedMaximum()
    {
        var settings = new AppSettings { ScrollbackLines = 50_000_000 };

        settings.Normalize();

        Assert.AreEqual(200_000, settings.ScrollbackLines);
    }

    [TestMethod]
    public void TinyScrollback_IsRaisedToTheMinimum()
    {
        var settings = new AppSettings { ScrollbackLines = 0 };

        settings.Normalize();

        Assert.AreEqual(100, settings.ScrollbackLines);
    }

    [TestMethod]
    public void ZeroFontSize_IsRaised()
    {
        // 字号 0 会让单元格度量退化成 0 宽 0 高 —— 终端画不出任何东西,而且除不尽会炸。
        var settings = new AppSettings { TerminalFontSize = 0 };

        settings.Normalize();

        Assert.AreEqual(6, settings.TerminalFontSize);
    }

    [TestMethod]
    public void OutOfRangePort_IsClamped()
    {
        Assert.AreEqual(1, Normalized(s => s.DefaultPort = -1).DefaultPort);
        Assert.AreEqual(65535, Normalized(s => s.DefaultPort = 999_999).DefaultPort);
    }

    [TestMethod]
    public void ConnectionTimeoutsAreClamped()
    {
        Assert.AreEqual(1, Normalized(s => s.General.ConnectTimeoutSeconds = 0).General.ConnectTimeoutSeconds);
        Assert.AreEqual(600, Normalized(s => s.General.ConnectTimeoutSeconds = 99_999).General.ConnectTimeoutSeconds);
    }

    [TestMethod]
    public void KeepAliveAllowsZero_MeaningDisabled()
    {
        // 0 是"关掉心跳"的合法取值,不能被抬成 1。
        Assert.AreEqual(0, Normalized(s => s.General.KeepAliveSeconds = 0).General.KeepAliveSeconds);
        Assert.AreEqual(0, Normalized(s => s.General.KeepAliveSeconds = -5).General.KeepAliveSeconds);
        Assert.AreEqual(3600, Normalized(s => s.General.KeepAliveSeconds = 100_000).General.KeepAliveSeconds);
    }

    [TestMethod]
    public void ReconnectSettingsAreClamped()
    {
        Assert.AreEqual(0, Normalized(s => s.General.MaxRetries = -1).General.MaxRetries);
        Assert.AreEqual(100, Normalized(s => s.General.MaxRetries = 10_000).General.MaxRetries);
        Assert.AreEqual(1, Normalized(s => s.General.ReconnectIntervalSeconds = 0).General.ReconnectIntervalSeconds);
        Assert.AreEqual(300, Normalized(s => s.General.ReconnectIntervalSeconds = 9_999).General.ReconnectIntervalSeconds);
    }

    [TestMethod]
    public void StatusMetricsIntervalIsClamped()
    {
        // 0 会让计时器变成"能跑多快跑多快"—— 对远端就是不停 fork/exec。
        Assert.AreEqual(1, Normalized(s => s.General.StatusMetricsIntervalSeconds = 0).General.StatusMetricsIntervalSeconds);
        Assert.AreEqual(60, Normalized(s => s.General.StatusMetricsIntervalSeconds = 3_600).General.StatusMetricsIntervalSeconds);
    }

    [TestMethod]
    public void ConcurrentTransfersAreClamped()
    {
        Assert.AreEqual(1, Normalized(s => s.Transfer.MaxConcurrentTransfers = 0).Transfer.MaxConcurrentTransfers);
        Assert.AreEqual(16, Normalized(s => s.Transfer.MaxConcurrentTransfers = 512).Transfer.MaxConcurrentTransfers);
    }

    [TestMethod]
    public void ValuesAlreadyInRangeAreLeftAlone()
    {
        var settings = new AppSettings
        {
            ScrollbackLines = 5_000,
            TerminalFontSize = 14,
            DefaultPort = 2222
        };
        settings.General.ConnectTimeoutSeconds = 15;
        settings.General.ReconnectIntervalSeconds = 30;
        settings.Transfer.MaxConcurrentTransfers = 4;

        settings.Normalize();

        Assert.AreEqual(5_000, settings.ScrollbackLines);
        Assert.AreEqual(14, settings.TerminalFontSize);
        Assert.AreEqual(2222, settings.DefaultPort);
        Assert.AreEqual(15, settings.General.ConnectTimeoutSeconds);
        Assert.AreEqual(30, settings.General.ReconnectIntervalSeconds);
        Assert.AreEqual(4, settings.Transfer.MaxConcurrentTransfers);
    }

    [TestMethod]
    public void NormalizeIsIdempotent()
    {
        var settings = new AppSettings { ScrollbackLines = 999_999_999 };

        settings.Normalize();
        int once = settings.ScrollbackLines;
        settings.Normalize();

        Assert.AreEqual(once, settings.ScrollbackLines);
    }

    private static AppSettings Normalized(Action<AppSettings> mutate)
    {
        var settings = new AppSettings();
        mutate(settings);
        settings.Normalize();
        return settings;
    }
}
