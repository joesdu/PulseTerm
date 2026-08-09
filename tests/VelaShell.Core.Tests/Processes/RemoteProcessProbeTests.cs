using VelaShell.Core.Processes;

namespace VelaShell.Core.Tests.Processes;

/// <summary>
/// 进程探针解析的回归测试。样本直接照抄真实 Linux 主机上 <see cref="RemoteProcessProbe.ProbeCommand" />
/// 的输出形态,包括 ps 命令行里带空格、带括号的进程。
/// </summary>
[TestClass]
[TestCategory("Processes")]
public class RemoteProcessProbeTests
{
    private const string Sample = """
        __N__
        4
        __K__
        100
        __B__
        128456.31
        __M__
        16777216000 6442450944
        __S__
        cpu  100 0 50 800 20 0 0 0 0 0
        __J__
        1 4210
        910 88
        1337 250000
        __P__
            1     0 root            Ss      1  11284  0.1  128456 /sbin/init splash
          910     1 systemd-resolve Ssl     2  18240  0.2  128400 /lib/systemd/systemd-resolved
         1337   910 www-data        Rl     33 984320 12.5    9600 /usr/bin/java -Xmx2g -jar /opt/app (server).jar --spring.profiles.active=prod
         2048     2 root            I<      1      0  0.0  128456 [kworker/0:1H]
        """;

    [TestMethod]
    public void Parse_ReadsHostWideCounters()
    {
        RemoteProcessSnapshot snapshot = RemoteProcessProbe.Parse(Sample)!;
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(4, snapshot.CpuCores);
        Assert.AreEqual(100, snapshot.ClockTicksPerSecond);
        Assert.AreEqual(128456.31, snapshot.UptimeSeconds, 0.01);
        Assert.AreEqual(16777216000, snapshot.MemTotalBytes);
        Assert.AreEqual(6442450944, snapshot.MemUsedBytes);
        Assert.AreEqual(970, snapshot.CpuTotalJiffies);
        Assert.AreEqual(820, snapshot.CpuIdleJiffies); // idle 800 + iowait 20
    }

    [TestMethod]
    public void Parse_ReadsEveryProcessRow()
    {
        RemoteProcessSnapshot snapshot = RemoteProcessProbe.Parse(Sample)!;
        Assert.HasCount(4, snapshot.Processes);
    }

    [TestMethod]
    public void Parse_MapsColumnsToFields()
    {
        RemoteProcessInfo init = RemoteProcessProbe.Parse(Sample)!.Processes.Single(p => p.Pid == 1);
        Assert.AreEqual(0, init.ParentPid);
        Assert.AreEqual("root", init.User);
        Assert.AreEqual("Ss", init.State);
        Assert.AreEqual(1, init.Threads);
        Assert.AreEqual(11284L * 1024, init.MemoryBytes);
        Assert.AreEqual(0.1, init.MemoryPercent, 0.001);
        Assert.AreEqual(128456, init.ElapsedSeconds);
        Assert.AreEqual("init", init.Name);
        Assert.AreEqual("/sbin/init splash", init.CommandLine);
    }

    [TestMethod]
    public void Parse_KeepsCommandLineIntact_WhenItContainsSpacesAndParentheses()
    {
        RemoteProcessInfo java = RemoteProcessProbe.Parse(Sample)!.Processes.Single(p => p.Pid == 1337);
        Assert.AreEqual("java", java.Name);
        Assert.AreEqual(
            "/usr/bin/java -Xmx2g -jar /opt/app (server).jar --spring.profiles.active=prod",
            java.CommandLine
        );
        Assert.AreEqual(33, java.Threads);
    }

    [TestMethod]
    public void Parse_CarriesCumulativeCpuTicks_ForDeltaCalculation()
    {
        IReadOnlyList<RemoteProcessInfo> processes = RemoteProcessProbe.Parse(Sample)!.Processes;
        Assert.AreEqual(4210, processes.Single(p => p.Pid == 1).CpuTicks);
        Assert.AreEqual(250000, processes.Single(p => p.Pid == 1337).CpuTicks);
        // __J__ 里没有这个 pid(采样间隙进程退出):滴答为 0,不应让整行丢掉。
        Assert.AreEqual(0, processes.Single(p => p.Pid == 2048).CpuTicks);
    }

    [TestMethod]
    public void Parse_FlagsKernelThreads()
    {
        IReadOnlyList<RemoteProcessInfo> processes = RemoteProcessProbe.Parse(Sample)!.Processes;
        Assert.IsTrue(processes.Single(p => p.Pid == 2048).IsKernelThread);
        Assert.IsFalse(processes.Single(p => p.Pid == 1).IsKernelThread);
    }

    [TestMethod]
    public void Parse_SurvivesMissingSections()
    {
        // 非 Linux 主机上 nproc/getconf/​/proc 全缺,只有 ps 有输出。
        const string psOnly = """
            __P__
                1     0 root     Ss      1  11284  0.1  128456 /sbin/init
            """;
        RemoteProcessSnapshot? snapshot = RemoteProcessProbe.Parse(psOnly);
        Assert.IsNotNull(snapshot);
        Assert.HasCount(1, snapshot.Processes);
        Assert.AreEqual(1, snapshot.CpuCores);
        Assert.AreEqual(100, snapshot.ClockTicksPerSecond);
    }

    [TestMethod]
    public void Parse_ReturnsNull_WhenThereAreNoProcesses()
    {
        Assert.IsNull(RemoteProcessProbe.Parse(null));
        Assert.IsNull(RemoteProcessProbe.Parse("   "));
        Assert.IsNull(RemoteProcessProbe.Parse("__N__\n4\n__P__\n"));
    }

    [TestMethod]
    public void BuildSignalCommand_UsesSignalNumbersAndReportsExitCode()
    {
        string command = RemoteProcessProbe.BuildSignalCommand([12, 34], ProcessSignal.Terminate);
        Assert.AreEqual("kill -15 12 34 2>&1; echo __RC__$?", command);
        Assert.StartsWith("kill -9 ", RemoteProcessProbe.BuildSignalCommand([7], ProcessSignal.Kill));
    }

    [TestMethod]
    public void BuildReniceCommand_ClampsToTheValidNiceRange()
    {
        Assert.Contains("-n -20 ", RemoteProcessProbe.BuildReniceCommand(5, -99));
        Assert.Contains("-n 19 ", RemoteProcessProbe.BuildReniceCommand(5, 99));
    }

    [TestMethod]
    public void ParseOutcome_SplitsMessageFromExitCode()
    {
        RemoteCommandOutcome ok = RemoteProcessProbe.ParseOutcome("__RC__0\n");
        Assert.IsTrue(ok.Success);
        Assert.IsEmpty(ok.Output);

        RemoteCommandOutcome denied = RemoteProcessProbe.ParseOutcome(
            "kill: (1): Operation not permitted\n__RC__1\n"
        );
        Assert.IsFalse(denied.Success);
        Assert.AreEqual("kill: (1): Operation not permitted", denied.Output);
    }

    [TestMethod]
    public void ParseOutcome_TreatsAMissingMarkerAsFailure()
    {
        // 通道半路断掉:没有退出码可信,不能报成功。
        RemoteCommandOutcome outcome = RemoteProcessProbe.ParseOutcome("partial output");
        Assert.IsFalse(outcome.Success);
        Assert.AreEqual("partial output", outcome.Output);
    }
}
