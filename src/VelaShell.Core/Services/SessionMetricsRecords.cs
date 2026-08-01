namespace VelaShell.Core.Services;

/// <summary>
/// 指标探针的采集范围。状态栏只要 <see cref="Basic" />(与改造前完全一致的轻量命令);
/// 资源监视窗口按需追加细分段,避免把每秒一次的探针拖成重活。
/// </summary>
[Flags]
public enum MetricsScope
{
    /// <summary>状态栏口径:CPU/内存/磁盘/网络的聚合值与逐核心、逐网卡累计计数。</summary>
    Basic = 0,

    /// <summary>CPU 用户/内核细分、内存明细、磁盘 IO、运行时长与上下文切换。</summary>
    Detail = 1,

    /// <summary>GPU 利用率、显存、温度功耗与 GPU 进程(仅在探测到 nvidia-smi 时有输出)。</summary>
    Gpu = 2,

    /// <summary>按 CPU / 常驻内存排序的进程 Top 列表。</summary>
    Processes = 4,

    /// <summary>资源监视窗口口径:全部细分段。</summary>
    Full = Detail | Gpu | Processes
}

/// <summary>CPU 时间在各状态上的占比(0-100),由采集器对 /proc/stat 聚合行两次采样差分得出。</summary>
/// <param name="User">用户态(含 nice)占比。</param>
/// <param name="System">内核态(含 irq/softirq)占比。</param>
/// <param name="IoWait">等待 IO 占比。</param>
/// <param name="Steal">被宿主机窃取占比(虚拟机上有意义)。</param>
public sealed record CpuBreakdown(double User, double System, double IoWait, double Steal);

/// <summary>物理内存的细分构成(字节),取自 /proc/meminfo。</summary>
/// <param name="Available">内核估算的可用内存。</param>
/// <param name="Buffers">块设备缓冲。</param>
/// <param name="Cached">页缓存(不含可回收 slab)。</param>
/// <param name="SReclaimable">可回收的 slab。</param>
/// <param name="Shmem">共享内存 / tmpfs 占用。</param>
/// <param name="Dirty">尚未回写的脏页。</param>
public sealed record MemoryDetail(long Available, long Buffers, long Cached, long SReclaimable, long Shmem, long Dirty)
{
    /// <summary>缓存合计(页缓存 + 可回收 slab),资源面板“内存组合”条的中段。</summary>
    public long CacheTotal => Cached + SReclaimable;
}

/// <summary>单块物理磁盘的累计 IO 计数(/proc/diskstats),由采集器做两次采样差分。</summary>
/// <param name="Name">块设备名(如 nvme0n1)。</param>
/// <param name="ReadSectors">累计读取扇区数(每扇区 512 字节)。</param>
/// <param name="WriteSectors">累计写入扇区数(每扇区 512 字节)。</param>
/// <param name="IoTicks">设备处于 IO 状态的累计毫秒数,用于计算“活动时间”。</param>
public sealed record DiskIoCounter(string Name, long ReadSectors, long WriteSectors, long IoTicks);

/// <summary>单块物理磁盘的瞬时 IO 速率,由采集器从上一采样计算。</summary>
/// <param name="Name">块设备名。</param>
/// <param name="ReadBytesPerSec">读取速率(字节/秒)。</param>
/// <param name="WriteBytesPerSec">写入速率(字节/秒)。</param>
/// <param name="BusyPercent">活动时间占比(0-100)。</param>
public sealed record DiskIoRate(string Name, double ReadBytesPerSec, double WriteBytesPerSec, double BusyPercent);

/// <summary>一条已建立的 TCP 连接的累计收发字节(ss -ti),由采集器做两次采样差分。</summary>
/// <param name="Local">本地地址:端口。</param>
/// <param name="Peer">对端地址:端口。</param>
/// <param name="Process">占用该连接的进程名;非 root 时只有本用户的连接拿得到,否则为空。</param>
/// <param name="BytesSent">累计发送字节(bytes_sent,老版 iproute2 用 bytes_acked)。</param>
/// <param name="BytesReceived">累计接收字节。</param>
public sealed record ConnectionCounter(string Local, string Peer, string Process, long BytesSent, long BytesReceived);

/// <summary>一条连接的瞬时收发速率,由采集器从上一采样计算。</summary>
/// <param name="Peer">对端地址:端口。</param>
/// <param name="Process">进程名;取不到时为空。</param>
/// <param name="RxBytesPerSec">接收速率(字节/秒)。</param>
/// <param name="TxBytesPerSec">发送速率(字节/秒)。</param>
public sealed record ConnectionRate(string Peer, string Process, double RxBytesPerSec, double TxBytesPerSec);

/// <summary>GPU 厂商。决定用哪条探针,也决定哪些指标注定拿不到。</summary>
public enum GpuVendor
{
    /// <summary>未能识别。</summary>
    Unknown,

    /// <summary>NVIDIA:走 nvidia-smi,指标最全。</summary>
    Nvidia,

    /// <summary>AMD:走 amdgpu 的 DRM sysfs(利用率/显存/温度/功耗齐全)。</summary>
    Amd,

    /// <summary>Intel:走 i915/xe 的 DRM sysfs(通常只有频率与功耗,利用率需 root 跑 PMU)。</summary>
    Intel,

    /// <summary>
    /// 虚拟显卡(virtio-gpu / VMware SVGA / QXL / Cirrus / Hyper-V / bochs)。
    /// 宿主合成出来的设备,除了"存在"以外没有任何可读指标。
    /// </summary>
    Virtual
}

/// <summary>
/// 一张 GPU 的实时读数。数值一律可空 —— 不同厂商暴露的指标差别很大
/// (Intel 核显没有 gpu_busy_percent、数据中心卡没有风扇),拿不到就是 null,界面显示 “—”,
/// 而不是拿 0 冒充"占用率 0%"。
/// </summary>
/// <param name="Index">GPU 序号(NVIDIA 用 nvidia-smi 的序号,其余按 DRM 卡序追加)。</param>
/// <param name="Name">型号名。</param>
/// <param name="Uuid">GPU UUID,用于把计算进程归到具体卡上(仅 NVIDIA)。</param>
/// <param name="Vendor">厂商。</param>
/// <param name="Card">DRM 卡名(如 card0);NVIDIA 经 nvidia-smi 取得时为空。</param>
/// <param name="UtilPercent">计算利用率 0-100。</param>
/// <param name="MemUtilPercent">显存带宽利用率 0-100(仅 NVIDIA)。</param>
/// <param name="MemUsedBytes">已用显存(字节)。</param>
/// <param name="MemTotalBytes">显存总量(字节)。</param>
/// <param name="TemperatureC">核心温度(摄氏度)。</param>
/// <param name="PowerWatts">当前功耗(瓦)。</param>
/// <param name="PowerLimitWatts">功耗上限(瓦)。</param>
/// <param name="FanPercent">风扇转速百分比;被动散热卡为 null。</param>
/// <param name="ClockMhz">当前核心时钟(MHz)。</param>
/// <param name="MemClockMhz">当前显存时钟(MHz)。</param>
public sealed record GpuDevice(
    int Index,
    string Name,
    string Uuid,
    GpuVendor Vendor,
    string Card,
    double? UtilPercent,
    double? MemUtilPercent,
    long? MemUsedBytes,
    long? MemTotalBytes,
    double? TemperatureC,
    double? PowerWatts,
    double? PowerLimitWatts,
    int? FanPercent,
    int? ClockMhz,
    int? MemClockMhz)
{
    /// <summary>显存使用率(0-100);拿不到显存总量时为 null。</summary>
    public double? MemPercent =>
        MemTotalBytes is > 0 && MemUsedBytes is { } used ? used * 100.0 / MemTotalBytes.Value : null;
}

/// <summary>占用某张 GPU 的一个进程(nvidia-smi compute-apps)。</summary>
/// <param name="GpuIndex">所属 GPU 序号;无法归属时为 -1。</param>
/// <param name="Pid">进程号。</param>
/// <param name="Name">进程名。</param>
/// <param name="MemBytes">该进程占用的显存(字节)。</param>
public sealed record GpuProcess(int GpuIndex, int Pid, string Name, long MemBytes);

/// <summary>进程 Top 列表中的一行(ps 口径)。</summary>
/// <param name="Pid">进程号。</param>
/// <param name="Command">命令行(已截断)。</param>
/// <param name="CpuPercent">CPU 占用百分比。</param>
/// <param name="RssBytes">常驻内存(字节)。</param>
/// <param name="SharedBytes">共享驻留内存(字节);/proc 读不到时为 null。</param>
/// <param name="SwapBytes">已换出内存(字节);/proc 读不到时为 null。</param>
public sealed record ProcessUsage(
    int Pid, string Command, double CpuPercent, long RssBytes, long? SharedBytes = null, long? SwapBytes = null);

/// <summary>主机的静态信息(每个会话只探测一次并缓存)。</summary>
public sealed class SessionStaticInfo
{
    /// <summary>CPU 型号名(/proc/cpuinfo 的 model name)。</summary>
    public string CpuModel { get; init; } = "";

    /// <summary>物理插槽数;读不到为 0。</summary>
    public int Sockets { get; init; }

    /// <summary>每插槽物理核心数;读不到为 0。</summary>
    public int CoresPerSocket { get; init; }

    /// <summary>每核心线程数;读不到为 0。</summary>
    public int ThreadsPerCore { get; init; }

    /// <summary>标称最高频率(MHz);读不到为 0。</summary>
    public double MaxMhz { get; init; }

    /// <summary>物理核心总数(插槽 × 每插槽核心);读不到为 0。</summary>
    public int PhysicalCores => Sockets * CoresPerSocket;

    /// <summary>物理块设备列表(lsblk)。</summary>
    public IReadOnlyList<BlockDevice> Disks { get; init; } = [];

    /// <summary>NVIDIA 驱动版本;无 NVIDIA 卡时为空(AMD/Intel 的 sysfs 不暴露版本号)。</summary>
    public string GpuDriver { get; init; } = "";

    /// <summary>探测到的 GPU 数量;无 GPU 时为 0(界面据此隐藏 GPU 页)。</summary>
    public int GpuCount { get; init; }

    /// <summary>DRM 卡的静态标识(卡名、厂商、lspci 型号名),用于给 sysfs 采到的卡补名字。</summary>
    public IReadOnlyList<GpuCardInfo> GpuCards { get; init; } = [];
}

/// <summary>一张显卡的静态标识(DRM 卡、或只在 PCI 上看得见的卡)。</summary>
/// <param name="Card">卡名(如 card0);没有 DRM 节点时用 PCI 槽位。</param>
/// <param name="Vendor">厂商。</param>
/// <param name="Name">lspci 给出的型号名;取不到时为空。</param>
/// <param name="Slot">PCI 槽位(如 0000:03:00.0);WSL 的合成设备为空。</param>
/// <param name="Driver">已绑定的内核驱动;直通后宿主未装驱动时为空。</param>
/// <param name="HasDrm">是否有 /sys/class/drm 节点 —— 没有就意味着一个实时指标也读不到。</param>
public sealed record GpuCardInfo(
    string Card, GpuVendor Vendor, string Name, string Slot = "", string Driver = "", bool HasDrm = false);

/// <summary>一块物理块设备的静态属性(lsblk)。</summary>
/// <param name="Name">设备名(如 nvme0n1)。</param>
/// <param name="Model">厂商型号。</param>
/// <param name="SizeBytes">容量(字节)。</param>
/// <param name="Rotational">true = 机械盘,false = 固态。</param>
/// <param name="Transport">接口类型(nvme / sata / sas / usb…);虚拟盘与老 lsblk 上为空。</param>
public sealed record BlockDevice(string Name, string Model, long SizeBytes, bool Rotational, string Transport = "");

/// <summary>一张物理网卡的静态属性。</summary>
/// <param name="Name">接口名。</param>
/// <param name="Mac">MAC 地址。</param>
/// <param name="Mtu">MTU。</param>
/// <param name="SpeedMbps">链路速率(Mbps);未协商或不支持时为 0。</param>
/// <param name="OperState">链路状态(up / down / unknown / dormant)。</param>
/// <param name="IpAddress">IPv4 地址(含掩码位);无地址时为空。</param>
/// <param name="Carrier">载波检测(/sys/class/net/*/carrier);读不到时为 null。</param>
/// <param name="Duplex">双工模式(full / half);虚拟网卡与未协商时为空。</param>
/// <param name="RxDropped">累计丢弃的接收包;读不到时为 null。</param>
/// <param name="TxDropped">累计丢弃的发送包;读不到时为 null。</param>
/// <param name="RxErrors">累计接收错误;读不到时为 null。</param>
/// <param name="TxErrors">累计发送错误;读不到时为 null。</param>
public sealed record NicInfo(
    string Name, string Mac, int Mtu, long SpeedMbps, string OperState, string IpAddress, bool? Carrier = null,
    string Duplex = "", long? RxDropped = null, long? TxDropped = null, long? RxErrors = null, long? TxErrors = null)
{
    /// <summary>
    /// 链路是否连通。operstate 与 carrier 任一为真即算连通 —— 有些无线驱动在关联完成后
    /// 仍把 operstate 报成 dormant,只看 operstate 会把主力网卡显示成"已断开"。
    /// </summary>
    public bool LinkUp =>
        string.Equals(OperState, "up", StringComparison.OrdinalIgnoreCase) || Carrier == true;
}
