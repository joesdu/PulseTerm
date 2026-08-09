using VelaShell.Core.Services;

namespace VelaShell.Core.Tests.Services;

[TestClass]
[TestCategory("Metrics")]
public class SessionMetricsTests
{
    private const string SampleOutput =
        "__P__\n8\n" +
        "__L__\n0.96 0.80 0.70 1/234 5678\n" +
        "__M__\n17179869184 4509715660\n" +
        "__D__\n549755813888 128849018880\n" +
        "__O__\nUbuntu 22.04.4 LTS\n" +
        "__K__\n6.8.0-40-generic\n";

    [TestMethod]
    public void Parse_FullOutput_MapsAllFields()
    {
        var m = SessionMetrics.Parse(SampleOutput);

        Assert.IsNotNull(m);
        Assert.AreEqual(8, m.CpuCores);
        Assert.AreEqual(12.0, m.CpuPercent, 0.1); // 0.96 / 8 cores = 12%
        Assert.AreEqual(17179869184L, m.MemTotalBytes);
        Assert.AreEqual(4509715660L, m.MemUsedBytes);
        Assert.AreEqual(549755813888L, m.DiskTotalBytes);
        Assert.AreEqual("Ubuntu 22.04.4 LTS", m.OsVersion);
        Assert.AreEqual("6.8.0-40-generic", m.Kernel);
        Assert.AreEqual(26.25, m.MemPercent, 0.1);
        Assert.AreEqual(23.4, m.DiskPercent, 0.1);
    }

    [TestMethod]
    public void Parse_PartialOutput_MissingSectionsDegradeGracefully()
    {
        var m = SessionMetrics.Parse("__P__\n4\n__L__\n2.0 1.0 0.5\n__M__\n__D__\n__O__\n__K__\n");

        Assert.IsNotNull(m);
        Assert.AreEqual(4, m.CpuCores);
        Assert.AreEqual(50.0, m.CpuPercent, 0.1);
        Assert.AreEqual(0, m.MemPercent);
    }

    [TestMethod]
    public void Parse_CpuPercent_ClampsAt100()
    {
        var m = SessionMetrics.Parse("__P__\n1\n__L__\n9.5 1.0 0.5\n__O__\nx\n");
        Assert.IsNotNull(m);
        Assert.AreEqual(100.0, m.CpuPercent);
    }

    [TestMethod]
    public void Parse_EmptyOrGarbage_ReturnsNull()
    {
        Assert.IsNull(SessionMetrics.Parse(""));
        Assert.IsNull(SessionMetrics.Parse("command not found"));
    }

    [TestMethod]
    public void Parse_CpuStatLine_ExposesJiffyCounters()
    {
        // cpu user nice system idle iowait irq softirq
        var m = SessionMetrics.Parse(SampleOutput + "__S__\ncpu  100 0 50 800 40 5 5\n");

        Assert.IsNotNull(m);
        Assert.IsTrue(m.HasCpuCounters);
        Assert.AreEqual(1000, m.CpuTotalJiffies);
        Assert.AreEqual(840, m.CpuIdleJiffies); // idle + iowait
    }

    [TestMethod]
    public void Parse_NetCounters_ExposeCumulativeBytes()
    {
        var m = SessionMetrics.Parse(SampleOutput + "__N__\n123456789 987654321\n");

        Assert.IsNotNull(m);
        Assert.IsTrue(m.HasNetCounters);
        Assert.AreEqual(123456789L, m.NetRxTotalBytes);
        Assert.AreEqual(987654321L, m.NetTxTotalBytes);
        Assert.IsFalse(m.HasNetRates); // rates only exist after a second sample
    }

    [TestMethod]
    public void Parse_SwapValues_ExposeTotalsAndPercent()
    {
        // __M__ carries "memTotal memUsed swapTotal swapUsed".
        var m = SessionMetrics.Parse(
            "__P__\n4\n__L__\n0.5 0.4 0.3\n__M__\n1000 500 2048 512\n__D__\n__O__\nx\n__K__\n");

        Assert.IsNotNull(m);
        Assert.AreEqual(2048L, m.SwapTotalBytes);
        Assert.AreEqual(512L, m.SwapUsedBytes);
        Assert.AreEqual(25.0, m.SwapPercent, 0.1);
    }

    [TestMethod]
    public void Parse_TwoValueMemSection_LeavesSwapAtZero()
    {
        var m = SessionMetrics.Parse(SampleOutput); // legacy "total used" pair only

        Assert.IsNotNull(m);
        Assert.AreEqual(0, m.SwapTotalBytes);
        Assert.AreEqual(0, m.SwapPercent);
    }

    [TestMethod]
    public void Parse_MissingCounterSections_LeavesCountersUnavailable()
    {
        var m = SessionMetrics.Parse(SampleOutput);

        Assert.IsNotNull(m);
        Assert.IsFalse(m.HasCpuCounters);
        Assert.IsFalse(m.HasNetCounters);
    }

    [TestMethod]
    public void Parse_DiskList_MapsEachFilesystem_AndDedupesBindMounts()
    {
        var m = SessionMetrics.Parse(SampleOutput +
            "__DL__\n" +
            "/dev/vda1 ext4 53687091200 50465865728 /\n" +
            "/dev/vdb1 xfs 528280977408 84825604096 /data\n" +
            "/dev/vdb1 xfs 528280977408 84825604096 /srv/data\n" +   // bind mount of the same device
            "/dev/vdc1 ext4 107374182400 1073741824 /mnt/my backup\n"); // mount point with a space

        Assert.IsNotNull(m);
        Assert.HasCount(3, m.Disks);
        Assert.AreEqual("/", m.Disks[0].MountPoint);
        Assert.AreEqual(53687091200L, m.Disks[0].TotalBytes);
        Assert.AreEqual("ext4", m.Disks[0].FsType);
        Assert.AreEqual("/data", m.Disks[1].MountPoint);
        Assert.AreEqual("xfs", m.Disks[1].FsType);
        Assert.AreEqual(94.0, m.Disks[0].Percent, 0.1);
        Assert.AreEqual("/mnt/my backup", m.Disks[2].MountPoint);
    }

    [TestMethod]
    public void Parse_DiskList_WithoutFsTypeColumn_StillMapsSizes()
    {
        // 老 df 不支持 --output 里的 fstype 就会少一列;少一列不能把容量读成挂载点。
        var m = SessionMetrics.Parse(SampleOutput +
            "__DL__\n/dev/vda1 53687091200 50465865728 /\n/dev/vdc1 107374182400 1073741824 /mnt/my backup\n");

        Assert.IsNotNull(m);
        Assert.HasCount(2, m.Disks);
        Assert.AreEqual("/", m.Disks[0].MountPoint);
        Assert.AreEqual(53687091200L, m.Disks[0].TotalBytes);
        Assert.IsEmpty(m.Disks[0].FsType);
        Assert.AreEqual("/mnt/my backup", m.Disks[1].MountPoint);
    }

    [TestMethod]
    public void Parse_NoDiskList_KeepsAggregateFallback()
    {
        var m = SessionMetrics.Parse(SampleOutput);

        Assert.IsNotNull(m);
        Assert.IsEmpty(m.Disks);
        Assert.IsGreaterThan(0, m.DiskTotalBytes); // 根分区聚合值仍可用
    }

    [TestMethod]
    public void Parse_PerCoreCounters_ExposeJiffiesPerCore()
    {
        var m = SessionMetrics.Parse(SampleOutput +
            "__C__\ncpu0 100 0 50 800 40 5 5\ncpu1 200 0 100 600 90 5 5\n");

        Assert.IsNotNull(m);
        Assert.HasCount(2, m.CoreCounters);
        Assert.AreEqual("cpu0", m.CoreCounters[0].Name);
        Assert.AreEqual(1000, m.CoreCounters[0].TotalJiffies);
        Assert.AreEqual(840, m.CoreCounters[0].IdleJiffies); // idle + iowait
        Assert.AreEqual("cpu1", m.CoreCounters[1].Name);
        Assert.IsNull(m.CorePercents); // 百分比要等采集器第二次采样
    }

    [TestMethod]
    public void Parse_PerNicCounters_ExposeCumulativeBytesPerInterface()
    {
        var m = SessionMetrics.Parse(SampleOutput +
            "__NI__\neth0 1000 2000\neth1 300 400\n");

        Assert.IsNotNull(m);
        Assert.HasCount(2, m.NicCounters);
        Assert.AreEqual("eth0", m.NicCounters[0].Name);
        Assert.AreEqual(1000L, m.NicCounters[0].RxBytes);
        Assert.AreEqual(2000L, m.NicCounters[0].TxBytes);
        Assert.AreEqual("eth1", m.NicCounters[1].Name);
        Assert.IsNull(m.NicRates); // 速率要等采集器第二次采样
    }
}
