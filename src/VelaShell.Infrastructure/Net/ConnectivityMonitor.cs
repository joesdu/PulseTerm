using System.Diagnostics;
using System.Net.NetworkInformation;

namespace VelaShell.Infrastructure.Net;

/// <summary>
/// 「网络刚恢复」「机器刚从睡眠里醒来」这两件事的统一信号源。
/// </summary>
public interface IConnectivityMonitor : IDisposable
{
    /// <summary>网络恢复可用,或系统从睡眠/休眠中唤醒(已合并去抖)。</summary>
    event Action? Resumed;
}

/// <summary>
/// 基于 <see cref="NetworkChange" /> 的连通性监视器(三平台通用,不引任何额外依赖)。
/// </summary>
/// <remarks>
/// <para>
/// 解决的是这个场景:合上盖子再打开,所有会话要等 keepalive 超时(默认几十秒)才发现断了,
/// 然后按固定间隔慢慢重试 —— 而网络其实在唤醒后两三秒就好了。用户看到的是"合盖回来要等一分钟"。
/// </para>
/// <para>
/// **去抖 2 秒**再发信号:唤醒时网卡往往会连着抖好几下(虚拟网卡、VPN、Wi-Fi 重连各发一次),
/// 不去抖就会连着触发好几轮重连风暴。去抖也顺带给了协议栈一点时间把路由表理顺 ——
/// 网卡"可用"的那一刻 DNS 还未必通。
/// </para>
/// <para>
/// <b>只用网络事件,不订阅 Windows 的 <c>SystemEvents.PowerModeChanged</c>。</b>
/// 那个类型在 <c>Microsoft.Win32.SystemEvents</c> 包里,本仓库没有引用它;
/// 而唤醒时网卡本就会重新初始化并触发一次网络事件,主场景已经覆盖到了。
/// 为"接着网线唤醒、网络全程没断"这种边角情形新拉一个依赖不划算。
/// </para>
/// </remarks>
public sealed class ConnectivityMonitor : IConnectivityMonitor
{
    /// <summary>信号合并窗口。见类型注释:唤醒时网卡会连着抖好几下。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly Timer _debounce;
    private bool _disposed;

    /// <summary>订阅系统事件并开始监视。</summary>
    public ConnectivityMonitor()
    {
        _debounce = new(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            Trace.WriteLine($"[ConnectivityMonitor] 网络事件不可用:{ex.Message}");
        }
    }

    /// <inheritdoc />
    public event Action? Resumed;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // 只关心"变可用";变不可用什么都不用做 —— 会话自己会断,断了自有重连逻辑。
        if (e.IsAvailable)
        {
            Schedule();
        }
    }

    private void Schedule()
    {
        if (!_disposed)
        {
            _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        if (!_disposed)
        {
            Resumed?.Invoke();
        }
    }

    /// <summary>退订系统事件。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            Trace.WriteLine($"[ConnectivityMonitor] 退订网络事件失败(订阅时多半也失败过):{ex.Message}");
        }
        _debounce.Dispose();
    }
}
