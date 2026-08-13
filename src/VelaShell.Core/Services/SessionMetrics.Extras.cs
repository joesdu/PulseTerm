using System.Globalization;

namespace VelaShell.Core.Services;

/// <summary>
/// 资源监视窗口所需的细分探针(CPU 细分 / 内存明细 / 磁盘 IO / GPU / 进程 Top)。
/// 与基础探针分开:状态栏每秒一次只跑 <see cref="MetricsScope.Basic" />,细分段仅在窗口打开时追加,
/// 否则每秒多跑两次 ps 和一次 nvidia-smi 会明显加重远端负担。
/// </summary>
public sealed partial class SessionMetrics
{
    /// <summary>细分段:内存构成、磁盘 IO、上下文切换、运行时长、当前频率与进程数。</summary>
    private const string DetailCommand =
        """echo __MI__; awk '/^MemAvailable:|^Buffers:|^Cached:|^SReclaimable:|^Shmem:|^Dirty:/{print $1" "$2}' /proc/meminfo 2>/dev/null; """ +
        // 逐盘 IO 按 /sys/block 枚举整盘(分区是它的子目录,天然被排除),不靠设备名白名单 ——
        // 白名单在 WSL / 云主机 / nbd / mmc 上会漏掉整块盘,表现为读写速率恒为 0。
        """echo __IO__; for d in /sys/block/*; do n=${d##*/}; case "$n" in loop*|ram*|zram*|dm-*|sr*|fd*|md*) continue;; esac; if [ -r "$d/stat" ]; then awk -v n="$n" '{print n"|"$3"|"$7"|"$10}' "$d/stat"; fi; done 2>/dev/null; """ +
        """echo __CX__; awk '/^ctxt /{print $2}' /proc/stat 2>/dev/null; """ +
        "echo __UT__; cut -d' ' -f1 /proc/uptime 2>/dev/null; " +
        """echo __FQ__; awk '/^cpu MHz/{s+=$4;n++} END{if(n>0)printf "%d", s/n}' /proc/cpuinfo 2>/dev/null; """ +
        "echo __PC__; ls -d /proc/[0-9]* 2>/dev/null | wc -l; " +
        // 网卡属性放在每次采样里而不是一次性静态探针:插拔网线、WiFi 重连、IP 变更都要跟着变。
        // 字段用 | 分隔:WiFi 读 speed 会 EINVAL 输出空串,空格分隔会让 operstate 左移一位
        // 被当成速率吃掉 —— 表现为主力无线网卡永远显示"已断开"。
        """echo __NF__; for i in /sys/class/net/*; do n=${i##*/}; if [ -e "$i/device" ]; then echo "$n|$(cat "$i/address" 2>/dev/null)|$(cat "$i/mtu" 2>/dev/null)|$(cat "$i/speed" 2>/dev/null)|$(cat "$i/operstate" 2>/dev/null)|$(cat "$i/carrier" 2>/dev/null)|$(cat "$i/duplex" 2>/dev/null)|$(cat "$i/statistics/rx_dropped" 2>/dev/null)|$(cat "$i/statistics/tx_dropped" 2>/dev/null)|$(cat "$i/statistics/rx_errors" 2>/dev/null)|$(cat "$i/statistics/tx_errors" 2>/dev/null)"; fi; done 2>/dev/null; """ +
        """echo __IP__; ip -4 -o addr show 2>/dev/null | awk '{print $2" "$4}'; """ +
        // 已建立的 TCP 连接及其累计收发字节(ss -i 的第二行);进程名只有本用户的连接拿得到,
        // 非 root 时其余显示 “—”。ss 不存在(busybox)时整段为空,界面给出"无法获取"提示。
        """echo __SS__; ss -tinpH state established 2>/dev/null | awk 'NR%2==1{local=$3; peer=$4; proc=""; if (match($0, /users:\(\("[^"]+"/)) { proc=substr($0, RSTART+9, RLENGTH-10) }} NR%2==0{s=""; r=""; for(i=1;i<=NF;i++){ if($i ~ /^bytes_sent:/){split($i,a,":"); s=a[2]} else if($i ~ /^bytes_acked:/ && s==""){split($i,a,":"); s=a[2]} else if($i ~ /^bytes_received:/){split($i,a,":"); r=a[2]} } print local"|"peer"|"proc"|"s"|"r}' | head -n 40""";

    /// <summary>
    /// GPU 段。NVIDIA 走 nvidia-smi(指标最全);AMD / Intel 走 DRM sysfs —— 比解析各版本
    /// rocm-smi 的输出稳,也不像 intel_gpu_top 那样需要 root 且只能流式输出。
    /// 三段都为空即视为无 GPU,界面隐藏 GPU 页。
    /// </summary>
    private const string GpuCommand =
        "echo __GP__; nvidia-smi --query-gpu=index,name,uuid,utilization.gpu,utilization.memory,memory.used,memory.total,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm,clocks.mem --format=csv,noheader,nounits 2>/dev/null; " +
        "echo __GA__; nvidia-smi --query-compute-apps=gpu_uuid,pid,process_name,used_memory --format=csv,noheader,nounits 2>/dev/null; " +
        // 连接器目录(card0-DP-1)名字里带短横,跳过,否则同一张卡会被重复计入。
        """echo __GS__; for c in /sys/class/drm/card*; do n=${c##*/}; case "$n" in *-*) continue;; esac; [ -d "$c/device" ] || continue; v=$(cat "$c/device/vendor" 2>/dev/null); [ -n "$v" ] || continue; echo "$n|$v|$(cat "$c/device/gpu_busy_percent" 2>/dev/null)|$(cat "$c/device/mem_info_vram_used" 2>/dev/null)|$(cat "$c/device/mem_info_vram_total" 2>/dev/null)|$(cat "$c"/device/hwmon/hwmon*/temp1_input 2>/dev/null | head -1)|$(cat "$c"/device/hwmon/hwmon*/power1_average 2>/dev/null | head -1)|$(cat "$c"/device/hwmon/hwmon*/freq1_input 2>/dev/null | head -1)|$(cat "$c/gt_cur_freq_mhz" 2>/dev/null)|$(cat "$c/device/product_name" 2>/dev/null)"; done 2>/dev/null""";

    /// <summary>
    /// 进程段每行保留的列数(整行,含 <c>pid/%cpu/rss</c> 前缀,前缀本身要吃掉近 20 列)。
    /// </summary>
    /// <remarks>
    /// 原值 90 会让命令行在远端就被切掉,而切口没有任何标记 —— 界面上表现为"列宽明明还有富余,
    /// 命令却断在半个词上"(用户反馈:<c>… -auth /run/us</c>,真身是 <c>/run/user/…</c>;
    /// dockerd 那行的 <c>--containerd=/run/containerd/containerd.sock</c> 同样丢了后缀)。
    /// <para>
    /// 取 300:窗口在 2K 屏最大化时,进程列约 1800px,按 10 号等宽字算差不多 300 字,
    /// 再宽也超出了这张表的用途。20 行 × 最多 210 字的增量只在监视窗口打开时才发生,
    /// 而超长命令行(带巨型 classpath 的 java、一串 flag 的浏览器)仍被这一刀挡在远端。
    /// </para>
    /// </remarks>
    private const int ProcessCommandColumns = 300;

    /// <summary>
    /// 进程段:按常驻内存取前 20 行(整行截断到 <see cref="ProcessCommandColumns" /> 列,避免刷屏)。
    /// 20 行是一屏能放下的量 —— 8 行时列表下半截空着,再多则纯粹是每轮多传的字节。
    /// 只留这一份 —— 界面上的进程表只有内存页一处,再跑一次按 CPU 排序的 ps 是白花的开销。
    /// </summary>
    /// <remarks>
    /// 截断走 awk 而不是 <c>cut -c1-N</c>:cut 的切口不留任何痕迹,界面无从分辨"命令本来就这么短"
    /// 与"被截断了";awk 只在真的超长时补一个省略号,读的人一眼能看出还有下文。
    /// 客户端反推不了这件事 —— 解析侧的行已被 TrimEntries 去掉前导空格(ps 的 PID 是右对齐的),
    /// 而且 SectionAt 还会把首行的空格一并 Trim,按行长比对必然误判。
    /// </remarks>
    private static readonly string ProcessCommand =
        "echo __TM__; ps -eo pid,pcpu,rss,args --sort=-rss 2>/dev/null | tail -n +2 | head -n 20 | " +
        "awk -v n=" + ProcessCommandColumns + " '{ if (length($0) > n) print substr($0, 1, n) \"…\"; else print }'; " +
        // 共享内存与换出量 ps 给不了:共享驻留页在 /proc/<pid>/statm 第 3 列(单位是页),
        // 换出量在 /proc/<pid>/status 的 VmSwap(单位 kB)。所有文件一次性喂给同一个 awk,
        // 避免每个进程 fork 两次 —— 1 秒一轮的轮询下那是 40 个进程/秒的额外开销。
        """echo __TP__; __pg=$(getconf PAGESIZE 2>/dev/null || echo 4096); __f=""; for __p in $(ps -eo pid= --sort=-rss 2>/dev/null | head -n 20); do __f="$__f /proc/$__p/statm /proc/$__p/status"; done; [ -n "$__f" ] && awk -v pg="$__pg" 'FILENAME ~ /statm$/ && FNR == 1 { split(FILENAME, a, "/"); s[a[3]] = $3 * pg; o[++n] = a[3] } FILENAME ~ /status$/ && /^VmSwap:/ { split(FILENAME, a, "/"); w[a[3]] = $2 * 1024 } END { for (i = 1; i <= n; i++) print o[i]"|"s[o[i]]"|"(o[i] in w ? w[o[i]] : "") }' $__f 2>/dev/null""";

    /// <summary>
    /// 主机静态信息探针:CPU 型号与拓扑、块设备型号、网卡 MAC/MTU/速率/IP、GPU 驱动。
    /// 每个会话只跑一次并缓存 —— 这些值在会话生命周期内不会变。lscpu 强制 C 语言环境,
    /// 否则中文语言环境的服务器会输出本地化标签,字段匹配全部落空。
    /// </summary>
    public const string StaticCommand =
        """echo __CM__; awk -F: '/^model name/{gsub(/^ +/,"",$2); print $2; exit}' /proc/cpuinfo 2>/dev/null; """ +
        """echo __LS__; LC_ALL=C lscpu 2>/dev/null | awk -F: '/^Socket\(s\)|^Core\(s\) per socket|^Thread\(s\) per core|^CPU max MHz|^Model name/{gsub(/^ +/,"",$2); print $1"|"$2}'; """ +
        // -e 7,1,11:排除 loop / ram / sr(光驱)。Ubuntu 上每个 snap 都是一个 loop 设备,
        // 不滤掉磁盘列表会被几十个 loopN 淹掉。
        // -P 输出 KEY="值" 对:TRAN(接口类型)在虚拟盘上是空的,按列切会把空列吞掉、
        // 后面的字段整体左移(型号被读成接口类型)。
        "echo __BD__; lsblk -dnb -e 7,1,11 -P -o NAME,SIZE,ROTA,TRAN,MODEL 2>/dev/null; " +
        "echo __GD__; nvidia-smi --query-gpu=index,driver_version --format=csv,noheader 2>/dev/null; " +
        // DRM 卡 → PCI 槽位 → lspci 型号名:sysfs 只有 AMD 才有 product_name,
        // Intel 核显靠 lspci 才能显示成人话。
        """echo __GV__; for c in /sys/class/drm/card*; do n=${c##*/}; case "$n" in *-*) continue;; esac; [ -d "$c/device" ] || continue; echo "$n $(basename "$(readlink -f "$c/device")") $(cat "$c/device/vendor" 2>/dev/null)"; done 2>/dev/null; """ +
        """echo __GL__; lspci -mm 2>/dev/null | grep -iE '"(VGA|3D|Display)'; """ +
        // __GC__:直接从 sysfs 枚举 PCI 显示类设备(class 0x03xxxx)。这是虚拟化场景的关键一段 ——
        // 显卡直通进来但宿主没装驱动时没有 DRM 节点,lspci 也未必装,只有 /sys/bus/pci 一定在。
        // 先用一个 awk 把所有 class 文件筛一遍(大机器上 PCI 设备好几百,逐个 cat 就是几百次 fork),
        // 只对筛出来的显示设备再读厂商/设备/驱动。
        """echo __GC__; for f in $(awk 'FNR==1 && /^0x03/{print FILENAME}' /sys/bus/pci/devices/*/class 2>/dev/null); do d=${f%/class}; echo "${d##*/}|$(cat "$d/vendor" 2>/dev/null)|$(cat "$d/device" 2>/dev/null)|$(basename "$(readlink -f "$d/driver" 2>/dev/null)" 2>/dev/null)"; done 2>/dev/null; """ +
        // __GW__:WSL2 没有 PCI 也没有 DRM,GPU 走 /dev/dxg 的 D3D12 直通。
        """echo __GW__; [ -e /dev/dxg ] && echo dxg""";

    /// <summary>
    /// 按采集范围组装探针命令。<see cref="MetricsScope.Basic" /> 返回与状态栏一致的原命令,
    /// 其余标志各自追加一段;解析侧对缺失分段一律降级为空,因此两端可以独立演进。
    /// </summary>
    /// <param name="scope">需要采集的范围。</param>
    /// <returns>可直接在远端 shell 执行的命令串。</returns>
    public static string BuildCommand(MetricsScope scope)
    {
        if (scope == MetricsScope.Basic)
        {
            return MetricsCommand;
        }
        var sb = new System.Text.StringBuilder(MetricsCommand);
        if (scope.HasFlag(MetricsScope.Detail))
        {
            sb.Append("; ").Append(DetailCommand);
        }
        if (scope.HasFlag(MetricsScope.Gpu))
        {
            sb.Append("; ").Append(GpuCommand);
        }
        if (scope.HasFlag(MetricsScope.Processes))
        {
            sb.Append("; ").Append(ProcessCommand);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 取出以 <c>__X__</c> 标记开头的分段内容。与基础解析不同,这里以“行首的 __”为终止条件:
    /// 进程命令行里可能出现双下划线,按任意 <c>__</c> 截断会把整段吃掉。
    /// </summary>
    private static string SectionAt(string output, string marker)
    {
        int start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }
        start += marker.Length;
        int end = output.IndexOf("\n__", start, StringComparison.Ordinal);
        return (end < 0 ? output[start..] : output[start..end]).Trim();
    }

    private static string[] LinesOf(string section) =>
        section.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double Num(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>数值或 null:空串与 nvidia-smi 的 <c>[N/A]</c> 都表示"这块卡不提供该指标"。</summary>
    private static double? NumOrNull(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>
    /// PCI 厂商号 → 厂商枚举。虚拟化厂商单列一类:KVM/PVE 的 virtio-gpu 与 QXL、ESXi 的 SVGA、
    /// Hyper-V 的合成显卡都只是宿主拼出来的设备,一个实时指标也读不到 —— 归到 Unknown 会
    /// 显示成 "UNKNOWN" 让人以为是探测失败。
    /// </summary>
    private static GpuVendor VendorOf(string vendorId) => vendorId.Trim().ToLowerInvariant() switch
    {
        "0x10de" => GpuVendor.Nvidia,
        "0x1002" or "0x1022" => GpuVendor.Amd,
        "0x8086" => GpuVendor.Intel,
        // 1af4/1b36 Red Hat(virtio-gpu / QXL)、15ad VMware、1414 微软、1234 QEMU stdvga、
        // 1013 Cirrus、100b Chips&Tech(部分老 KVM)。
        "0x1af4" or "0x1b36" or "0x15ad" or "0x1414" or "0x1234" or "0x1013" => GpuVendor.Virtual,
        _ => GpuVendor.Unknown
    };

    /// <summary>解析细分段并写入本实例;分段缺失时对应属性保持默认值。</summary>
    private void ParseExtras(string output)
    {
        // __MI__:meminfo 的若干行 "Key: value"(单位 kB)。
        long available = 0, buffers = 0, cached = 0, reclaimable = 0, shmem = 0, dirty = 0;
        bool hasMemDetail = false;
        foreach (string line in LinesOf(SectionAt(output, "__MI__")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out long kb))
            {
                continue;
            }
            hasMemDetail = true;
            long bytes = kb * 1024;
            switch (parts[0])
            {
                case "MemAvailable:": available = bytes; break;
                case "Buffers:": buffers = bytes; break;
                case "Cached:": cached = bytes; break;
                case "SReclaimable:": reclaimable = bytes; break;
                case "Shmem:": shmem = bytes; break;
                case "Dirty:": dirty = bytes; break;
            }
        }
        if (hasMemDetail)
        {
            // /proc/meminfo 的 Cached 含 Shmem;资源面板要展示的是"可回收的页缓存",扣掉共享内存。
            Memory = new(available, buffers, Math.Max(0, cached - shmem), reclaimable, shmem, dirty);
        }

        // __IO__:"设备|读扇区|写扇区|io_ticks"(取自 /sys/block/<dev>/stat)。
        var io = new List<DiskIoCounter>();
        foreach (string line in LinesOf(SectionAt(output, "__IO__")))
        {
            string[] parts = line.Split('|');
            if (parts.Length >= 4 &&
                IsPhysicalDisk(parts[0].Trim()) &&
                long.TryParse(parts[1].Trim(), out long rd) &&
                long.TryParse(parts[2].Trim(), out long wr) &&
                long.TryParse(parts[3].Trim(), out long ticks))
            {
                io.Add(new(parts[0].Trim(), rd, wr, ticks));
            }
        }
        DiskIoCounters = io;

        // __SS__:"本地|对端|进程|累计发送|累计接收"。ss 不可用时整段为空。
        var connections = new List<ConnectionCounter>();
        foreach (string line in LinesOf(SectionAt(output, "__SS__")))
        {
            string[] parts = line.Split('|');
            if (parts.Length < 5 || parts[1].Trim().Length == 0)
            {
                continue;
            }
            _ = long.TryParse(parts[3].Trim(), out long sent);
            _ = long.TryParse(parts[4].Trim(), out long received);
            connections.Add(new(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), sent, received));
        }
        Connections = connections;
        HasConnectionProbe = SectionAt(output, "__SS__").Length > 0 || output.Contains("__SS__", StringComparison.Ordinal);

        if (long.TryParse(SectionAt(output, "__CX__"), out long ctxt))
        {
            ContextSwitches = ctxt;
        }
        UptimeSeconds = Num(SectionAt(output, "__UT__"));
        CurrentMhz = Num(SectionAt(output, "__FQ__"));
        if (int.TryParse(SectionAt(output, "__PC__"), out int procs))
        {
            ProcessCount = procs;
        }

        // __GP__:nvidia-smi CSV。缺失字段(如被动散热卡的风扇、虚拟化下的功耗)输出 [N/A],
        // 一律解析为 null —— 拿 0 冒充会让界面显示"功耗 0 W"。
        var gpus = new List<GpuDevice>();
        foreach (string line in LinesOf(SectionAt(output, "__GP__")))
        {
            string[] f = line.Split(',');
            if (f.Length < 13 || !int.TryParse(f[0].Trim(), out int idx))
            {
                continue;
            }
            gpus.Add(new(
                idx,
                f[1].Trim(),
                f[2].Trim(),
                GpuVendor.Nvidia,
                "",
                NumOrNull(f[3]),
                NumOrNull(f[4]),
                NumOrNull(f[5]) is { } used ? (long)(used * 1024 * 1024) : null,
                NumOrNull(f[6]) is { } total ? (long)(total * 1024 * 1024) : null,
                NumOrNull(f[7]),
                NumOrNull(f[8]),
                NumOrNull(f[9]),
                int.TryParse(f[10].Trim(), out int fan) ? fan : null,
                (int?)NumOrNull(f[11]),
                (int?)NumOrNull(f[12])));
        }

        // __GS__:DRM sysfs。NVIDIA 卡若已由 nvidia-smi 采到就跳过(闭源驱动的 sysfs 几乎是空的),
        // 序号从 nvidia 之后接着排。
        bool hasNvidiaSmi = gpus.Count > 0;
        int nextIndex = hasNvidiaSmi ? gpus.Max(g => g.Index) + 1 : 0;
        foreach (string line in LinesOf(SectionAt(output, "__GS__")))
        {
            string[] f = line.Split('|');
            if (f.Length < 10)
            {
                continue;
            }
            GpuVendor vendor = VendorOf(f[1].Trim());
            if (vendor == GpuVendor.Nvidia && hasNvidiaSmi)
            {
                continue;
            }
            // hwmon 的温度是毫摄氏度、功耗是微瓦、频率是赫兹;gt_cur_freq_mhz 直接是 MHz。
            double? clock = NumOrNull(f[7]) is { } hz ? hz / 1_000_000 : NumOrNull(f[8]);
            gpus.Add(new(
                nextIndex++,
                f[9].Trim(),
                "",
                vendor,
                f[0].Trim(),
                NumOrNull(f[2]),
                null,
                NumOrNull(f[3]) is { } vramUsed ? (long)vramUsed : null,
                NumOrNull(f[4]) is { } vramTotal ? (long)vramTotal : null,
                NumOrNull(f[5]) is { } milli ? milli / 1000 : null,
                NumOrNull(f[6]) is { } micro ? micro / 1_000_000 : null,
                null,
                null,
                (int?)clock,
                null));
        }
        Gpus = gpus;

        // __GA__:计算进程 "gpu_uuid, pid, name, used_mem(MiB)";按 UUID 归到具体卡。
        var gpuProcs = new List<GpuProcess>();
        foreach (string line in LinesOf(SectionAt(output, "__GA__")))
        {
            string[] f = line.Split(',');
            if (f.Length < 4 || !int.TryParse(f[1].Trim(), out int pid))
            {
                continue;
            }
            string uuid = f[0].Trim();
            int gpuIndex = -1;
            foreach (GpuDevice g in gpus)
            {
                if (string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                {
                    gpuIndex = g.Index;
                    break;
                }
            }
            gpuProcs.Add(new(gpuIndex, pid, f[2].Trim(), (long)(Num(f[3]) * 1024 * 1024)));
        }
        GpuProcesses = gpuProcs;

        TopByMemory = ParseProcessList(SectionAt(output, "__TM__"), ParseProcessExtras(SectionAt(output, "__TP__")));

        // __IP__:"接口 地址/掩码位";一张网卡可能有多个地址,只记第一个。
        var ips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in LinesOf(SectionAt(output, "__IP__")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                _ = ips.TryAdd(parts[0], parts[1]);
            }
        }

        // __NF__:"名称|MAC|MTU|速率|operstate|carrier|duplex|丢包收|丢包发|错误收|错误发"
        //(字段可能为空,不能用 RemoveEmptyEntries)。
        var nics = new List<NicInfo>();
        foreach (string line in LinesOf(SectionAt(output, "__NF__")))
        {
            string[] f = line.Split('|');
            if (f.Length < 5 || f[0].Trim().Length == 0)
            {
                continue;
            }
            string name = f[0].Trim();
            // speed 在 WiFi / 未协商 / 虚拟网卡上是空或 -1,一律归零表示"未知"。
            long speed = long.TryParse(f[3].Trim(), out long sp) && sp > 0 ? sp : 0;
            bool? carrier = f.Length > 5 && f[5].Trim() is { Length: > 0 } c ? c == "1" : null;
            nics.Add(new(
                name,
                f[1].Trim(),
                int.TryParse(f[2].Trim(), out int mtu) ? mtu : 0,
                speed,
                f[4].Trim() is { Length: > 0 } state ? state : "unknown",
                ips.GetValueOrDefault(name, ""),
                carrier,
                At(f, 6),
                CountAt(f, 7), CountAt(f, 8), CountAt(f, 9), CountAt(f, 10)));
        }
        NicInfos = nics;

        // 老内核 / 虚拟网卡缺这些 sysfs 项时字段是空的 —— 空要变 null(界面显示占位符),
        // 不能变 0(那会被读成"一个包都没丢")。
        static string At(string[] fields, int index) =>
            index < fields.Length ? fields[index].Trim() : "";

        static long? CountAt(string[] fields, int index) =>
            long.TryParse(At(fields, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    /// <summary>
    /// 是否是"整块物理磁盘"。loop(每个 snap 一个)、ram、zram、dm(LVM 映射)、光驱、
    /// 软驱与 md 阵列都不该出现在"物理磁盘"列表里。
    /// </summary>
    private static bool IsPhysicalDisk(string name) =>
        !name.StartsWith("loop", StringComparison.Ordinal) &&
        !name.StartsWith("ram", StringComparison.Ordinal) &&
        !name.StartsWith("zram", StringComparison.Ordinal) &&
        !name.StartsWith("dm-", StringComparison.Ordinal) &&
        !name.StartsWith("sr", StringComparison.Ordinal) &&
        !name.StartsWith("fd", StringComparison.Ordinal) &&
        !name.StartsWith("md", StringComparison.Ordinal);

    /// <summary>解析 ps 的 "pid pcpu rss args" 输出;命令行含空格,取前三列后其余全部作为命令。</summary>
    /// <param name="section">ps 分段。</param>
    /// <param name="extras">按 PID 索引的共享/换出量;取不到就整段为空,对应列显示占位符。</param>
    private static IReadOnlyList<ProcessUsage> ParseProcessList(
        string section, IReadOnlyDictionary<int, (long? Shared, long? Swap)> extras)
    {
        var list = new List<ProcessUsage>();
        foreach (string line in LinesOf(section))
        {
            string[] parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4 || !int.TryParse(parts[0], out int pid) || !long.TryParse(parts[2], out long rssKb))
            {
                continue;
            }
            _ = extras.TryGetValue(pid, out (long? Shared, long? Swap) extra);

            // 命令行原样带过:超长行末尾的省略号是远端 awk 补的(见 ProcessCommand),
            // 界面只管照显,列宽不够时再由 TextTrimming 叠一层自己的省略号。
            list.Add(new(pid, parts[3], Num(parts[1]), rssKb * 1024, extra.Shared, extra.Swap));
        }
        return list;
    }

    /// <summary>
    /// 解析 <c>pid|共享字节|换出字节</c> 分段。空字段表示该进程的 /proc 项读不到
    /// (进程刚退出,或权限不足),按 null 传下去让界面显示占位符而不是 0。
    /// </summary>
    private static Dictionary<int, (long? Shared, long? Swap)> ParseProcessExtras(string section)
    {
        var map = new Dictionary<int, (long? Shared, long? Swap)>();
        foreach (string line in LinesOf(section))
        {
            string[] parts = line.Split('|');
            if (parts.Length < 3 || !int.TryParse(parts[0].Trim(), out int pid))
            {
                continue;
            }
            map[pid] = (LongOrNull(parts[1]), LongOrNull(parts[2]));
        }
        return map;

        static long? LongOrNull(string s) =>
            long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
    }

    /// <summary>解析静态信息探针的输出;整段不可用时返回一个各字段为空的实例。</summary>
    /// <param name="output"><see cref="StaticCommand" /> 的原始输出。</param>
    /// <returns>解析后的主机静态信息。</returns>
    public static SessionStaticInfo ParseStatic(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new();
        }
        int sockets = 0, coresPerSocket = 0, threadsPerCore = 0;
        double maxMhz = 0;
        string lscpuModel = "";
        foreach (string line in LinesOf(SectionAt(output, "__LS__")))
        {
            int bar = line.IndexOf('|');
            if (bar <= 0)
            {
                continue;
            }
            string key = line[..bar].Trim();
            string value = line[(bar + 1)..].Trim();
            if (key.StartsWith("Socket", StringComparison.Ordinal))
            {
                _ = int.TryParse(value, out sockets);
            }
            else if (key.StartsWith("Core(s)", StringComparison.Ordinal))
            {
                _ = int.TryParse(value, out coresPerSocket);
            }
            else if (key.StartsWith("Thread(s)", StringComparison.Ordinal))
            {
                _ = int.TryParse(value, out threadsPerCore);
            }
            else if (key.StartsWith("CPU max", StringComparison.Ordinal))
            {
                maxMhz = Num(value);
            }
            else if (key.StartsWith("Model name", StringComparison.Ordinal))
            {
                lscpuModel = value;
            }
        }

        var disks = new List<BlockDevice>();
        foreach (string line in LinesOf(SectionAt(output, "__BD__")))
        {
            Dictionary<string, string> fields = ParsePairs(line);
            // 容量为 0 的条目是未挂载的 loop / 空读卡器,列出来只会让"物理磁盘"里出现不存在的盘。
            if (!fields.TryGetValue("NAME", out string? name) ||
                !long.TryParse(fields.GetValueOrDefault("SIZE"), out long size) ||
                size <= 0 || !IsPhysicalDisk(name))
            {
                continue;
            }
            disks.Add(new(name, fields.GetValueOrDefault("MODEL", "").Trim(), size,
                fields.GetValueOrDefault("ROTA") == "1", fields.GetValueOrDefault("TRAN", "").Trim()));
        }

        string driver = "";
        int gpuCount = 0;
        foreach (string line in LinesOf(SectionAt(output, "__GD__")))
        {
            string[] parts = line.Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out _))
            {
                continue;
            }
            gpuCount++;
            driver = parts[1].Trim();
        }

        // __GL__:lspci -mm 的引号字段,第 4 个是型号名(如 “GA102 [GeForce RTX 3090]”)。
        var lspci = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in LinesOf(SectionAt(output, "__GL__")))
        {
            int space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }
            string[] quoted = QuotedFields(line[(space + 1)..]);
            if (quoted.Length >= 3)
            {
                lspci[line[..space]] = quoted[2];
            }
        }

        // __GV__:"卡名 PCI槽位 厂商号";把 lspci 的型号名按槽位后缀匹配上去
        // (sysfs 给的是 0000:03:00.0,lspci 给的是 03:00.0)。
        var cards = new List<GpuCardInfo>();
        foreach (string line in LinesOf(SectionAt(output, "__GV__")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }
            cards.Add(new(parts[0], VendorOf(parts[2]), LspciName(lspci, parts[1]), parts[1], "", true));
        }

        // __GC__:PCI 上的显示类设备。直通卡在宿主没装驱动时没有 DRM 节点,虚拟机的
        // virtio-gpu / SVGA 则可能连 lspci 都没装 —— 这一段保证"卡确实在,只是没指标"也能显示出来。
        foreach (string line in LinesOf(SectionAt(output, "__GC__")))
        {
            string[] f = line.Split('|');
            if (f.Length < 2 || f[0].Trim().Length == 0)
            {
                continue;
            }
            string slot = f[0].Trim();
            if (cards.Any(c => c.Slot.Length > 0 && c.Slot.EndsWith(slot, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            cards.Add(new(slot, VendorOf(f[1]), LspciName(lspci, slot), slot,
                f.Length > 3 ? f[3].Trim() : "", false));
        }

        // WSL2 既没有 PCI 也没有 DRM:有 /dev/dxg 就说明 GPU 是经 D3D12 直通进来的。
        // NVIDIA 卡在 WSL 里 nvidia-smi 能用(走 __GP__),其余厂商只能给出"存在"这一个事实。
        if (cards.Count == 0 && SectionAt(output, "__GW__").Contains("dxg", StringComparison.Ordinal))
        {
            cards.Add(new("dxg", GpuVendor.Virtual, "WSL D3D12 (/dev/dxg)"));
        }

        if (gpuCount < cards.Count)
        {
            gpuCount = cards.Count;
        }

        return new()
        {
            // /proc/cpuinfo 的 model name 在 aarch64 / 部分虚拟机上根本不存在,
            // 退回 lscpu 的 Model name。两者都可能带成串空格,统一压成单空格。
            CpuModel = CollapseSpaces(SectionAt(output, "__CM__") is { Length: > 0 } cpuinfoModel
                ? cpuinfoModel
                : lscpuModel),
            Sockets = sockets,
            CoresPerSocket = coresPerSocket,
            ThreadsPerCore = threadsPerCore,
            MaxMhz = maxMhz,
            Disks = disks,
            GpuDriver = driver,
            GpuCount = gpuCount,
            GpuCards = cards
        };
    }

    /// <summary>把连续空白压成单个空格并去掉首尾空白(CPU 型号名常带对齐用的多余空格)。</summary>
    private static string CollapseSpaces(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }
        var sb = new System.Text.StringBuilder(value.Length);
        bool space = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
            {
                _ = sb.Append(' ');
                space = false;
            }
            _ = sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 按槽位后缀在 lspci 结果里找型号名:sysfs 给的是 0000:03:00.0,lspci 给的是 03:00.0。
    /// </summary>
    private static string LspciName(Dictionary<string, string> lspci, string slot)
    {
        foreach ((string key, string label) in lspci)
        {
            if (slot.EndsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }
        return "";
    }

    /// <summary>
    /// 解析 <c>KEY="值" KEY="值"</c> 形式的一行(lsblk -P)。值里可能有空格(型号名),
    /// 因此只能按引号切,不能按空格切。
    /// </summary>
    private static Dictionary<string, string> ParsePairs(string line)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int index = 0;
        while (index < line.Length)
        {
            int eq = line.IndexOf('=', index);
            if (eq < 0 || eq + 1 >= line.Length || line[eq + 1] != '"')
            {
                break;
            }
            int end = line.IndexOf('"', eq + 2);
            if (end < 0)
            {
                break;
            }
            map[line[index..eq].Trim()] = line[(eq + 2)..end];
            index = end + 1;
        }
        return map;
    }

    /// <summary>取出一行里的全部双引号字段(lspci -mm 的机器可读格式)。</summary>
    private static string[] QuotedFields(string line)
    {
        var fields = new List<string>();
        int index = 0;
        while (index < line.Length)
        {
            int start = line.IndexOf('"', index);
            if (start < 0)
            {
                break;
            }
            int end = line.IndexOf('"', start + 1);
            if (end < 0)
            {
                break;
            }
            fields.Add(line[(start + 1)..end]);
            index = end + 1;
        }
        return [.. fields];
    }
}
