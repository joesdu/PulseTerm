using VelaShell.Core.Services;

namespace VelaShell.Core.Tests.Services;

/// <summary>
/// 资源监视窗口用到的细分探针解析(内存明细 / 磁盘 IO / GPU / 进程 Top / 静态信息)。
/// 这些分段的期望值取自真实主机上 nvidia-smi、/proc/diskstats、ps 的实际输出格式。
/// </summary>
[TestClass]
[TestCategory("Metrics")]
public class SessionMetricsExtrasTests
{
    /// <summary>一份包含全部分段的探针输出(MetricsScope.Full 口径)。</summary>
    internal const string FullOutput =
        "__P__\n8\n" +
        "__L__\n0.96 0.80 0.70 3/1234 5678\n" +
        "__M__\n17179869184 4509715660 8589934592 429496729\n" +
        "__D__\n549755813888 128849018880\n" +
        "__O__\nUbuntu 22.04.4 LTS\n" +
        "__K__\n6.8.0-40-generic\n" +
        "__S__\ncpu  1000 20 300 5000 100 10 5 2\n" +
        "__N__\n1000000 200000\n" +
        "__DL__\n/dev/nvme0n1p2 ext4 549755813888 128849018880 /\n/dev/sda1 xfs 1099511627776 549755813888 /data\n" +
        "__C__\ncpu0 500 10 150 2500 50 5 2 1\ncpu1 500 10 150 2500 50 5 3 1\n" +
        "__NI__\neth0 900000 180000\neth1 100000 20000\n" +
        "__MI__\nMemAvailable: 8000000\nBuffers: 500000\nCached: 3000000\nSReclaimable: 200000\nShmem: 100000\nDirty: 12000\n" +
        "__IO__\nnvme0n1|200000|100000|5000\nsda|50000|25000|1200\n" +
        "__CX__\n987654321\n" +
        "__UT__\n3115977.42\n" +
        "__FQ__\n3740\n" +
        "__PC__\n412\n" +
        "__SS__\n10.0.2.31:22|10.0.2.5:51234|sshd|880000|120000\n10.0.2.31:5432|10.0.4.19:44112||4200000|90000\n" +
        "__GP__\n0, NVIDIA A100-SXM4-80GB, GPU-aaa, 94, 57, 68400, 81920, 71, 382.13, 400.00, [N/A], 1410, 1593\n" +
        "1, NVIDIA A100-SXM4-80GB, GPU-bbb, 12, 5, 6200, 81920, 43, 96.00, 400.00, 35, 1215, 1593\n" +
        "__GA__\nGPU-aaa, 7104, python, 62100\n" +
        "__TM__\n 7104  94.0 65011712 python train.py --ddp\n 2481  11.8 15518924 postgres: checkpointer\n" +
        "  881   6.7  1258291 nginx: worker process\n" +
        // 第三个进程只有 statm、没有 VmSwap(内核未编译 swap 支持时就是这样),换出列应为 null。
        "__TP__\n7104|2147483648|1073741824\n2481|536870912|0\n881|10485760|\n";

    [TestMethod]
    public void Parse_MemoryDetail_SubtractsShmemFromCached()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m?.Memory);
        Assert.AreEqual(8000000L * 1024, m.Memory.Available);
        Assert.AreEqual(500000L * 1024, m.Memory.Buffers);
        // Cached 含 Shmem,面板要展示的是可回收部分,因此扣掉共享内存。
        Assert.AreEqual((3000000L - 100000L) * 1024, m.Memory.Cached);
        Assert.AreEqual(200000L * 1024, m.Memory.SReclaimable);
        Assert.AreEqual(12000L * 1024, m.Memory.Dirty);
        Assert.AreEqual((3000000L - 100000L + 200000L) * 1024, m.Memory.CacheTotal);
    }

    [TestMethod]
    public void Parse_DiskIoAndCounters_KeepRawValues()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m);
        Assert.HasCount(2, m.DiskIoCounters);
        Assert.AreEqual("nvme0n1", m.DiskIoCounters[0].Name);
        Assert.AreEqual(200000L, m.DiskIoCounters[0].ReadSectors);
        Assert.AreEqual(100000L, m.DiskIoCounters[0].WriteSectors);
        Assert.AreEqual(5000L, m.DiskIoCounters[0].IoTicks);
        Assert.AreEqual(987654321L, m.ContextSwitches);
        Assert.AreEqual(412, m.ProcessCount);
        Assert.AreEqual(3740, m.CurrentMhz);
        Assert.AreEqual(3115977.42, m.UptimeSeconds, 0.01);
    }

    [TestMethod]
    public void Parse_Connections_KeepsPeerProcessAndByteCounters()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m);
        Assert.IsTrue(m.HasConnectionProbe);
        Assert.HasCount(2, m.Connections);
        Assert.AreEqual("10.0.2.5:51234", m.Connections[0].Peer);
        Assert.AreEqual("sshd", m.Connections[0].Process);
        Assert.AreEqual(880000L, m.Connections[0].BytesSent);
        Assert.AreEqual(120000L, m.Connections[0].BytesReceived);
        // 非 root 时拿不到别人的进程名,留空由界面显示占位符。
        Assert.AreEqual("", m.Connections[1].Process);
        // 速率要靠两次采样差分,单次解析不给。
        Assert.IsNull(m.ConnectionRates);
    }

    [TestMethod]
    public void Parse_WithoutSsOutput_MarksTheProbeAsRunButEmpty()
    {
        // ss 不存在(busybox)时该段有标记但没有内容 —— 界面据此区分"没连接"与"没采集"。
        var m = SessionMetrics.Parse("__P__\n2\n__L__\n0.1 0.1 0.1\n__SS__\n");

        Assert.IsNotNull(m);
        Assert.IsEmpty(m.Connections);
        Assert.IsTrue(m.HasConnectionProbe);
    }

    [TestMethod]
    public void Parse_LoadAverage_ReadsAllThreeWindowsAndTaskCounts()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m);
        Assert.AreEqual(0.96, m.Load1, 0.001);
        Assert.AreEqual(0.80, m.Load5, 0.001);
        Assert.AreEqual(0.70, m.Load15, 0.001);
        Assert.AreEqual(3, m.RunningTasks);
        Assert.AreEqual(1234, m.ThreadCount);
        // 细分占比要靠两次采样差分,单次解析只保留原始列。
        Assert.HasCount(8, m.CpuStatColumns);
        Assert.IsNull(m.Cpu);
    }

    [TestMethod]
    public void Parse_Gpus_MapsCsvAndTolerAtesNotAvailableFields()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m);
        Assert.HasCount(2, m.Gpus);
        GpuDevice first = m.Gpus[0];
        Assert.AreEqual(0, first.Index);
        Assert.AreEqual(GpuVendor.Nvidia, first.Vendor);
        Assert.AreEqual("NVIDIA A100-SXM4-80GB", first.Name);
        Assert.AreEqual(94.0, first.UtilPercent!.Value);
        Assert.AreEqual(57.0, first.MemUtilPercent!.Value);
        Assert.AreEqual(68400L * 1024 * 1024, first.MemUsedBytes!.Value);
        Assert.AreEqual(81920L * 1024 * 1024, first.MemTotalBytes!.Value);
        Assert.AreEqual(71.0, first.TemperatureC!.Value);
        Assert.AreEqual(382.13, first.PowerWatts!.Value, 0.01);
        Assert.AreEqual(1410, first.ClockMhz!.Value);
        // 数据中心卡没有风扇,nvidia-smi 输出 [N/A] —— 必须是 null,不能是 0。
        Assert.IsNull(first.FanPercent);
        Assert.AreEqual(35, m.Gpus[1].FanPercent);
        Assert.AreEqual(83.5, first.MemPercent!.Value, 0.1);
    }

    [TestMethod]
    public void Parse_SysfsGpus_CoversAmdAndIntelAndSkipsNvidiaWhenSmiAlreadyAnswered()
    {
        // amdgpu 暴露利用率/显存/温度/功耗;Intel 核显只有频率,利用率要 root 跑 PMU 才有。
        const string sysfs =
            "__GS__\n" +
            "card0|0x1002|63|8589934592|17179869184|64000|185000000||2100|Radeon RX 7900 XTX\n" +
            "card1|0x8086||||45000|12000000|1550000000||\n" +
            "card2|0x10de|||||||1800|\n";

        var m = SessionMetrics.Parse(FullOutput + sysfs);

        Assert.IsNotNull(m);
        // NVIDIA 那张已由 nvidia-smi 采到,sysfs 里的同一张不重复计入。
        Assert.HasCount(4, m.Gpus);

        GpuDevice amd = m.Gpus.Single(g => g.Vendor == GpuVendor.Amd);
        Assert.AreEqual("card0", amd.Card);
        Assert.AreEqual("Radeon RX 7900 XTX", amd.Name);
        Assert.AreEqual(63.0, amd.UtilPercent!.Value);
        Assert.AreEqual(8589934592L, amd.MemUsedBytes!.Value);
        Assert.AreEqual(17179869184L, amd.MemTotalBytes!.Value);
        Assert.AreEqual(64.0, amd.TemperatureC!.Value, 0.01);   // 毫摄氏度 → 摄氏度
        Assert.AreEqual(185.0, amd.PowerWatts!.Value, 0.01);    // 微瓦 → 瓦
        Assert.AreEqual(2100, amd.ClockMhz!.Value);             // gt_cur_freq_mhz 直接是 MHz
        Assert.AreEqual(50.0, amd.MemPercent!.Value, 0.01);

        GpuDevice intel = m.Gpus.Single(g => g.Vendor == GpuVendor.Intel);
        Assert.IsNull(intel.UtilPercent, "Intel 核显没有 gpu_busy_percent,必须是 null 而不是 0%。");
        Assert.IsNull(intel.MemTotalBytes);
        Assert.IsNull(intel.MemPercent);
        Assert.AreEqual(45.0, intel.TemperatureC!.Value, 0.01);
        Assert.AreEqual(1550, intel.ClockMhz!.Value);           // hwmon 的 freq1_input 是赫兹
        // 序号接在 nvidia-smi 的后面排,不与其冲突。
        Assert.AreEqual(2, m.Gpus[2].Index);
        Assert.AreEqual(3, m.Gpus[3].Index);
    }

    [TestMethod]
    public void Parse_SysfsOnly_KeepsNvidiaCardsWhenSmiMissing()
    {
        // 装了开源 nouveau / 没装 nvidia-smi 的机器:sysfs 里的 NVIDIA 卡仍要列出来。
        var m = SessionMetrics.Parse("__P__\n4\n__L__\n1.0 0.5 0.2\n__GS__\ncard0|0x10de|||||||1800|\n");

        Assert.IsNotNull(m);
        Assert.HasCount(1, m.Gpus);
        Assert.AreEqual(GpuVendor.Nvidia, m.Gpus[0].Vendor);
        Assert.AreEqual(0, m.Gpus[0].Index);
    }

    [TestMethod]
    public void Parse_GpuProcesses_ResolveOwningCardByUuid()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m);
        Assert.HasCount(1, m.GpuProcesses);
        Assert.AreEqual(0, m.GpuProcesses[0].GpuIndex);
        Assert.AreEqual(7104, m.GpuProcesses[0].Pid);
        Assert.AreEqual("python", m.GpuProcesses[0].Name);
        Assert.AreEqual(62100L * 1024 * 1024, m.GpuProcesses[0].MemBytes);
    }

    [TestMethod]
    public void Parse_ProcessTops_KeepFullCommandLineWithSpaces()
    {
        var m = SessionMetrics.Parse(FullOutput);

        Assert.IsNotNull(m);
        Assert.HasCount(3, m.TopByMemory);
        Assert.AreEqual(7104, m.TopByMemory[0].Pid);
        Assert.AreEqual(94.0, m.TopByMemory[0].CpuPercent, 0.01);
        Assert.AreEqual(65011712L * 1024, m.TopByMemory[0].RssBytes);
        Assert.AreEqual("python train.py --ddp", m.TopByMemory[0].Command);
        // 命令行里的空格与冒号不能被吃掉。
        Assert.AreEqual("postgres: checkpointer", m.TopByMemory[1].Command);
        Assert.AreEqual("nginx: worker process", m.TopByMemory[2].Command);

        // 共享 / 换出按 PID 与 ps 那段对齐;读不到的字段是 null,不能当成 0
        //(0 会被读成"这个进程一点没换出",而事实是根本没探到)。
        Assert.AreEqual(2147483648L, m.TopByMemory[0].SharedBytes);
        Assert.AreEqual(1073741824L, m.TopByMemory[0].SwapBytes);
        Assert.AreEqual(0L, m.TopByMemory[1].SwapBytes);
        Assert.AreEqual(10485760L, m.TopByMemory[2].SharedBytes);
        Assert.IsNull(m.TopByMemory[2].SwapBytes);
    }

    [TestMethod]
    public void Parse_ProcessTops_WithoutExtrasSection_LeavesSharedAndSwapUnknown()
    {
        // /proc 读不到(容器里挂了 hidepid、或进程刚退出)时整段缺席,不能退化成 0。
        var m = SessionMetrics.Parse(
            "__P__\n4\n__L__\n1.0 0.5 0.2\n__M__\n1024 512\n__O__\nDebian\n" +
            "__TM__\n 881   6.7  1258291 nginx: worker process\n");

        Assert.IsNotNull(m);
        Assert.HasCount(1, m.TopByMemory);
        Assert.IsNull(m.TopByMemory[0].SharedBytes);
        Assert.IsNull(m.TopByMemory[0].SwapBytes);
    }

    [TestMethod]
    public void ProcessProbe_TruncatesWideEnoughAndMarksTheCut()
    {
        // 起因:探针在远端把整行切到 90 列,而前缀(pid/%cpu/rss)先吃掉近 20 列,
        // 命令行只剩七十来字 —— 界面上列宽明明还有富余,命令却断在半个词上
        //(用户反馈 "-auth /run/us",真身是 /run/user/…;dockerd 那行丢了 .sock 后缀)。
        string command = SessionMetrics.BuildCommand(MetricsScope.Processes);

        Assert.DoesNotContain("cut -c1-90", command, "90 列在常见窗口宽度下就会切掉命令行。");

        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(command, @"awk -v n=(\d+) '\{ if \(length\(\$0\) > n\)");
        Assert.IsTrue(match.Success, "进程段必须保留列数上限,否则超长命令行每秒回传一次会把带宽吃光。");

        int columns = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsGreaterThan(200, columns, "留给命令行的列数要够常见窗口宽度铺满进程列。");
        Assert.IsLessThan(1000, columns, "上限也不能取消 —— 巨型 classpath 的 java 命令行能有上万字。");

        // 切口必须留痕:没有这个省略号,界面分不清"命令本来就这么短"和"被截断了"。
        Assert.Contains("\"…\"", command, "超长行要在远端补省略号。");
    }

    [TestMethod]
    public void Parse_ProcessLine_KeepsTheProbesTruncationMarker()
    {
        // 省略号由远端 awk 补,解析侧原样带过 —— 中途被 Trim 或当成分隔符吃掉就白标了。
        var m = SessionMetrics.Parse(
            "__P__\n4\n__L__\n1.0 0.5 0.2\n__M__\n1024 512\n__O__\nDebian\n" +
            "__TM__\n 4242   1.0 12345 /usr/bin/Xwayland :1024 -rootless -auth /run/user/1000/x…\n");

        Assert.IsNotNull(m);
        Assert.AreEqual("/usr/bin/Xwayland :1024 -rootless -auth /run/user/1000/x…", m.TopByMemory[0].Command);
    }

    [TestMethod]
    public void Parse_WithoutExtraSections_LeavesDetailFieldsEmpty()
    {
        // 状态栏口径(MetricsScope.Basic)不采集细分段,解析必须优雅降级。
        var m = SessionMetrics.Parse(
            "__P__\n4\n__L__\n1.0 0.5 0.2\n__M__\n1024 512\n__O__\nDebian\n");

        Assert.IsNotNull(m);
        Assert.IsNull(m.Memory);
        Assert.IsEmpty(m.Gpus);
        Assert.IsEmpty(m.DiskIoCounters);
        Assert.IsEmpty(m.TopByMemory);
        Assert.AreEqual(0, m.ProcessCount);
    }

    [TestMethod]
    public void BuildCommand_BasicStaysIdenticalAndScopesAppendSections()
    {
        Assert.AreEqual(SessionMetrics.MetricsCommand, SessionMetrics.BuildCommand(MetricsScope.Basic));

        string full = SessionMetrics.BuildCommand(MetricsScope.Full);
        Assert.Contains("__MI__", full);
        Assert.Contains("__IO__", full);
        Assert.Contains("nvidia-smi", full);
        Assert.Contains("--sort=-rss", full);
        // 进程表与它的共享/换出补充段必须取同样多的行,否则后半截进程永远配不上那两列。
        Assert.AreEqual(2, full.Split("head -n 20").Length - 1, "进程段与 /proc 补充段的行数不一致。");

        string detailOnly = SessionMetrics.BuildCommand(MetricsScope.Detail);
        Assert.Contains("__IO__", detailOnly);
        Assert.DoesNotContain("nvidia-smi", detailOnly);
    }

    [TestMethod]
    public void ParseStatic_ReadsTopologyDisksNicsAndGpuDriver()
    {
        const string output =
            "__CM__\nAMD EPYC 9754 96-Core Processor\n" +
            "__LS__\nSocket(s)|1\nCore(s) per socket|96\nThread(s) per core|2\nCPU max MHz|3710.0000\n" +
            // lsblk -P 的 KEY="值" 格式(行尾那个空格是原始字符串字面量的收尾要求,解析忽略它)。
            "__BD__\n" +
            """NAME="nvme0n1" SIZE="3840755982336" ROTA="0" TRAN="nvme" MODEL="SAMSUNG MZQL23T8HCLS-00A07" """ + "\n" +
            """NAME="sda" SIZE="16000900661248" ROTA="1" TRAN="sata" MODEL="ST16000NM000J-2TW103" """ + "\n" +
            """NAME="loop3" SIZE="12345678" ROTA="0" TRAN="" MODEL="" """ + "\n" +
            """NAME="sr0" SIZE="0" ROTA="1" TRAN="sata" MODEL="QEMU DVD-ROM" """ + "\n" +
            "__GD__\n0, 550.90.07\n1, 550.90.07\n";

        SessionStaticInfo info = SessionMetrics.ParseStatic(output);

        Assert.AreEqual("AMD EPYC 9754 96-Core Processor", info.CpuModel);
        Assert.AreEqual(1, info.Sockets);
        Assert.AreEqual(96, info.CoresPerSocket);
        Assert.AreEqual(2, info.ThreadsPerCore);
        Assert.AreEqual(96, info.PhysicalCores);
        Assert.AreEqual(3710.0, info.MaxMhz, 0.1);

        // 只留整块物理磁盘:loop(每个 snap 一个)与容量为 0 的光驱都不该进"物理磁盘"列表。
        Assert.HasCount(2, info.Disks);
        Assert.AreEqual("nvme0n1", info.Disks[0].Name);
        Assert.IsFalse(info.Disks[0].Rotational);
        Assert.AreEqual("SAMSUNG MZQL23T8HCLS-00A07", info.Disks[0].Model);
        // 接口类型来自 TRAN 列;型号里带空格,按引号切才不会被截断。
        Assert.AreEqual("nvme", info.Disks[0].Transport);
        Assert.AreEqual("sata", info.Disks[1].Transport);
        Assert.IsTrue(info.Disks[1].Rotational);
        Assert.IsEmpty(info.Disks.Where(d => d.Name.StartsWith("loop") || d.Name.StartsWith("sr")));

        Assert.AreEqual(2, info.GpuCount);
        Assert.AreEqual("550.90.07", info.GpuDriver);
    }

    [TestMethod]
    public void ParseStatic_PassthroughGpuWithoutDriver_StillShowsUpFromPci()
    {
        // ESXi / PVE 把卡直通进来但客户机没装驱动:没有 DRM 节点,nvidia-smi 也跑不了,
        // 只有 /sys/bus/pci 看得见。这时候必须还能报出"卡在,但读不到指标"。
        const string output =
            "__GV__\n" +
            "__GL__\n03:00.0 \"VGA compatible controller\" \"NVIDIA Corporation\" \"GA102 [GeForce RTX 3090]\" -r a1 \"NVIDIA Corporation\" \"Device 1467\"\n" +
            "__GC__\n0000:03:00.0|0x10de|0x2204|\n";

        SessionStaticInfo info = SessionMetrics.ParseStatic(output);

        Assert.HasCount(1, info.GpuCards);
        GpuCardInfo card = info.GpuCards[0];
        Assert.AreEqual(GpuVendor.Nvidia, card.Vendor);
        Assert.AreEqual("GA102 [GeForce RTX 3090]", card.Name);
        Assert.AreEqual("0000:03:00.0", card.Slot);
        Assert.IsFalse(card.HasDrm, "没有 DRM 节点的卡不能被标成有实时指标。");
        Assert.IsEmpty(card.Driver);
        Assert.AreEqual(1, info.GpuCount);
    }

    [TestMethod]
    public void ParseStatic_VirtualGpus_AreLabelledInsteadOfUnknown()
    {
        // KVM/PVE 的 virtio-gpu 与 ESXi 的 SVGA:厂商号不在三大厂里,归到 Unknown
        // 界面会显示成 "UNKNOWN",看着像探测失败。
        const string output =
            "__GV__\ncard0 0000:00:01.0 0x1af4\n" +
            "__GC__\n0000:00:01.0|0x1af4|0x1050|virtio-pci\n0000:00:0f.0|0x15ad|0x0405|vmwgfx\n";

        SessionStaticInfo info = SessionMetrics.ParseStatic(output);

        Assert.HasCount(2, info.GpuCards);
        // DRM 已经采到的那张不能因为 PCI 段再出现一次。
        Assert.AreEqual("card0", info.GpuCards[0].Card);
        Assert.IsTrue(info.GpuCards[0].HasDrm);
        Assert.AreEqual(GpuVendor.Virtual, info.GpuCards[0].Vendor);
        Assert.AreEqual("0000:00:0f.0", info.GpuCards[1].Card);
        Assert.AreEqual(GpuVendor.Virtual, info.GpuCards[1].Vendor);
        Assert.AreEqual("vmwgfx", info.GpuCards[1].Driver);
    }

    [TestMethod]
    public void ParseStatic_Wsl_ReportsTheD3D12Device()
    {
        // WSL2 既没有 PCI 显示设备也没有 DRM,GPU 只体现为 /dev/dxg。
        SessionStaticInfo info = SessionMetrics.ParseStatic("__GV__\n__GC__\n__GW__\ndxg\n");

        Assert.HasCount(1, info.GpuCards);
        Assert.AreEqual(GpuVendor.Virtual, info.GpuCards[0].Vendor);
        Assert.Contains("dxg", info.GpuCards[0].Name);
    }

    [TestMethod]
    public void ParseStatic_FallsBackToLscpuModelNameAndCollapsesPadding()
    {
        // aarch64 与不少虚拟机的 /proc/cpuinfo 根本没有 model name 字段,
        // 型号名只能从 lscpu 拿;Intel 的型号名还常带对齐用的连续空格。
        const string output =
            "__CM__\n" +
            "__LS__\nSocket(s)|1\nCore(s) per socket|8\n" +
            "Model name|Intel(R) Core(TM) i9-14900HX     CPU @ 2.20GHz\n";

        SessionStaticInfo info = SessionMetrics.ParseStatic(output);

        Assert.AreEqual("Intel(R) Core(TM) i9-14900HX CPU @ 2.20GHz", info.CpuModel);
    }

    [TestMethod]
    public void ParseStatic_PrefersCpuinfoModelNameOverLscpu()
    {
        const string output =
            "__CM__\nAMD EPYC 9754 96-Core Processor\n" +
            "__LS__\nModel name|AMD EPYC 9754\n";

        Assert.AreEqual("AMD EPYC 9754 96-Core Processor", SessionMetrics.ParseStatic(output).CpuModel);
    }

    [TestMethod]
    public void Parse_Nics_KeepFieldsAlignedWhenSpeedIsUnreadable()
    {
        // WiFi 网卡读 speed 会 EINVAL 输出空串。字段若按空格切,operstate 会左移一位被当成速率,
        // 于是主力无线网卡永远显示"已断开" —— 这里用管道分隔并逐字段核对。
        var m = SessionMetrics.Parse(
            "__P__\n8\n__L__\n0.5 0.4 0.3\n" +
            "__NF__\neth0|b4:2e:99:0c:1a:77|9000|10000|up|1|full|0|0|3|1\n" +
            "wlp4s0|28:0c:5c:9f:8b:de|1500||dormant|1||||\n" +
            "enp3s0|28:0c:5c:9f:8b:df|1500||down|0\n" +
            "__IP__\nlo 127.0.0.1/8\neth0 10.0.2.31/24\nwlp4s0 192.168.124.192/24\n");

        Assert.IsNotNull(m);
        Assert.HasCount(3, m.NicInfos);

        Assert.AreEqual(10000, m.NicInfos[0].SpeedMbps);
        Assert.AreEqual("10.0.2.31/24", m.NicInfos[0].IpAddress);
        Assert.IsTrue(m.NicInfos[0].LinkUp);

        // WiFi:速率读不到归零,但载波已建立 —— 必须判为已连接。
        NicInfo wifi = m.NicInfos[1];
        Assert.AreEqual("28:0c:5c:9f:8b:de", wifi.Mac);
        Assert.AreEqual(1500, wifi.Mtu);
        Assert.AreEqual(0, wifi.SpeedMbps);
        Assert.AreEqual("dormant", wifi.OperState);
        Assert.AreEqual("192.168.124.192/24", wifi.IpAddress);
        Assert.IsTrue(wifi.LinkUp, "载波已建立的无线网卡不应显示为已断开。");

        // 双工与丢包/错误计数取自 sysfs;老内核 / 虚拟网卡没有这些项时必须是 null,
        // 变成 0 会被读成"一个包都没丢"。
        Assert.AreEqual("full", m.NicInfos[0].Duplex);
        Assert.AreEqual(0L, m.NicInfos[0].RxDropped);
        Assert.AreEqual(3L, m.NicInfos[0].RxErrors);
        Assert.AreEqual(1L, m.NicInfos[0].TxErrors);
        Assert.IsEmpty(wifi.Duplex);
        Assert.IsNull(wifi.RxDropped);
        Assert.IsNull(m.NicInfos[2].TxErrors);

        Assert.IsFalse(m.NicInfos[2].LinkUp);
    }

    [TestMethod]
    public void Parse_Nics_TakesTheFirstGlobalIpv6PerInterface()
    {
        // IPv6 是新采的一段:双栈主机上一张网卡常有多个全局地址(SLAAC + 固定),只记第一个;
        // 链路本地(fe80::)在探针侧就按 scope global 滤掉了,这里不该出现。
        var m = SessionMetrics.Parse(
            "__P__\n8\n__L__\n0.5 0.4 0.3\n" +
            "__NF__\neth0|b4:2e:99:0c:1a:77|1500|1000|up|1|full|0|0|0|0\n" +
            "eth1|b4:2e:99:0c:1a:78|1500|1000|up|1|full|0|0|0|0\n" +
            "__IP__\neth0 10.0.2.31/24\n" +
            "__I6__\neth0 2001:db8:1::31/64\neth0 fd00::7a/128\n");

        Assert.IsNotNull(m);
        Assert.AreEqual("2001:db8:1::31/64", m.NicInfos[0].Ipv6Address);
        // 没有 IPv6 的网卡是空串,界面据此整行不出 —— 不能塞占位符。
        Assert.IsEmpty(m.NicInfos[1].Ipv6Address);
    }

    [TestMethod]
    public void ParseStatic_Nics_ReadsDriverMediumAndEthtoolSpeed()
    {
        // __NS__:驱动 / 介质类型 / 无线 / 有无物理设备 / ethtool 兜底速率。
        // 这一段是"网卡详情里那个刺眼的链路速率 —"的解药:virtio 这类半虚拟化网卡的
        // sysfs speed 是 -1,界面据 HasDevice + 驱动名把它说成"不适用"而不是"未知"。
        SessionStaticInfo info = SessionMetrics.ParseStatic(
            "__CM__\nAMD EPYC 9754\n" +
            "__NS__\neth0|1|ixgbe|0|1|10000\nwlp4s0|1|iwlwifi|1|1|\nlo|772||0|0|\nvirt0|1|virtio_net|0|1|\n");

        Assert.HasCount(4, info.Nics);

        NicStaticInfo eth0 = info.Nics[0];
        Assert.AreEqual("ixgbe", eth0.Driver);
        Assert.AreEqual(10000, eth0.SpeedMbps, "sysfs 读不到时才有这个值,来自 ethtool。");
        Assert.IsTrue(eth0.HasDevice);
        Assert.IsFalse(eth0.IsWireless);
        Assert.IsFalse(eth0.IsLoopback);

        Assert.IsTrue(info.Nics[1].IsWireless);
        Assert.AreEqual(0, info.Nics[1].SpeedMbps, "ethtool 也没给出速率时不能瞎填。");

        Assert.IsTrue(info.Nics[2].IsLoopback, "type=772 是回环。");
        Assert.IsFalse(info.Nics[2].HasDevice);

        Assert.AreEqual("virtio_net", info.Nics[3].Driver);
    }

    [TestMethod]
    public void StaticCommand_ProbesNicAttributesWithoutForkingPerSample()
    {
        // ethtool 要 fork,只能待在会话级静态探针里:放进每秒一轮的细分探针,
        // 几十张 veth 的 docker 主机会被 fork 淹掉。
        Assert.Contains("__NS__", SessionMetrics.StaticCommand);
        Assert.Contains("ethtool", SessionMetrics.StaticCommand);
        Assert.DoesNotContain("ethtool", SessionMetrics.BuildCommand(MetricsScope.Full));
    }

    [TestMethod]
    public void Parse_DiskIo_SkipsLoopAndOtherPseudoDevices()
    {
        var m = SessionMetrics.Parse(
            "__P__\n4\n__L__\n0.5 0.4 0.3\n" +
            "__IO__\nnvme0n1|200000|100000|5000\nloop7|10|0|1\ndm-0|50|20|3\nzram0|8|8|1\nvda|900|300|40\n");

        Assert.IsNotNull(m);
        Assert.HasCount(2, m.DiskIoCounters);
        Assert.AreEqual("nvme0n1", m.DiskIoCounters[0].Name);
        Assert.AreEqual("vda", m.DiskIoCounters[1].Name);
    }

    [TestMethod]
    public void ParseStatic_GpuCards_NameThemFromLspciBySlot()
    {
        const string output =
            "__GV__\ncard0 0000:03:00.0 0x10de\ncard1 0000:00:02.0 0x8086\n" +
            "__GL__\n03:00.0 \"VGA compatible controller\" \"NVIDIA Corporation\" \"GA102 [GeForce RTX 3090]\" -ra1 \"NVIDIA\" \"Device 147d\"\n" +
            "00:02.0 \"VGA compatible controller\" \"Intel Corporation\" \"Raptor Lake-S GT1 [UHD Graphics 770]\" -r04 \"Intel\" \"Device 3000\"\n";

        SessionStaticInfo info = SessionMetrics.ParseStatic(output);

        Assert.HasCount(2, info.GpuCards);
        Assert.AreEqual(GpuVendor.Nvidia, info.GpuCards[0].Vendor);
        Assert.AreEqual("GA102 [GeForce RTX 3090]", info.GpuCards[0].Name);
        Assert.AreEqual(GpuVendor.Intel, info.GpuCards[1].Vendor);
        Assert.AreEqual("Raptor Lake-S GT1 [UHD Graphics 770]", info.GpuCards[1].Name);
        // 没有 nvidia-smi 时,卡数由 DRM 卡兜底,GPU 页才不会被误隐藏。
        Assert.AreEqual(2, info.GpuCount);
    }

    [TestMethod]
    public void ParseStatic_EmptyOutput_ReturnsBlankInfo()
    {
        SessionStaticInfo info = SessionMetrics.ParseStatic("");

        Assert.AreEqual("", info.CpuModel);
        Assert.AreEqual(0, info.GpuCount);
        Assert.IsEmpty(info.Disks);
        Assert.IsEmpty(info.GpuCards);
    }
}
