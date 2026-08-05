using VelaShell.ViewModels;

namespace VelaShell.Services;

/// <summary>
/// 同步输入频道的转发中枢:对等模型,频道内每个标签既是发送者又是接收者。
/// 挂钩每个标签终端的 <c>TypedInput</c>(仅用户产生的输入,不含协议自动应答),
/// 把输入字节经 <see cref="TerminalTabViewModel.WriteSyncInput" /> 直写同频道其他
/// 标签的 PTY。直写走桥的 SendRaw,不经接收端终端控件的输入事件——既不会二次
/// 触发 TypedInput(防转发回环),也不会驱动接收端的命令补全弹层(智能建议只
/// 属于用户正在键入的焦点标签)。
/// </summary>
public sealed class SyncInputCoordinator
{
    private readonly Dictionary<TerminalTabViewModel, Action<byte[]>> _taps = [];

    /// <summary>把标签纳入转发管辖(幂等);随标签集合变化由宿主调用。</summary>
    public void Attach(TerminalTabViewModel tab)
    {
        if (_taps.ContainsKey(tab))
        {
            return;
        }
        void Tap(byte[] data) => Forward(tab, data);
        _taps[tab] = Tap;
        tab.TerminalEmulator.TypedInput += Tap;
        tab.SyncChannelCloseRequested += CloseChannel;
    }

    /// <summary>解除标签的转发管辖并让其退出频道(标签关闭时由宿主调用)。</summary>
    public void Detach(TerminalTabViewModel tab)
    {
        if (!_taps.Remove(tab, out Action<byte[]>? tap))
        {
            return;
        }
        tab.TerminalEmulator.TypedInput -= tap;
        tab.SyncChannelCloseRequested -= CloseChannel;
        tab.LeaveSyncChannel();
    }

    /// <summary>关闭频道:让频道内所有标签退出(横条的“关闭频道”按钮)。</summary>
    public void CloseChannel(SyncInputChannel channel)
    {
        foreach (
            TerminalTabViewModel tab in _taps.Keys.Where(t => t.SyncChannel == channel).ToArray()
        )
        {
            tab.LeaveSyncChannel();
        }
    }

    private void Forward(TerminalTabViewModel source, byte[] data)
    {
        // 暂停的标签不发送也不接收;快捷命令自带多目标分发,转发会造成频道内重复注入。
        if (
            source.SyncChannel is not { } channel
            || source.IsSyncPaused
            || source.IsSyncForwardSuppressed
            || !source.IsConnected
        )
        {
            return;
        }
        foreach (TerminalTabViewModel peer in _taps.Keys)
        {
            if (
                !ReferenceEquals(peer, source)
                && peer.SyncChannel == channel
                && !peer.IsSyncPaused
            )
            {
                peer.WriteSyncInput(data);
            }
        }
    }
}
