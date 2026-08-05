using Avalonia.Interactivity;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Docking.Controls;

/// <summary>终端专用标签行为,与 SFTP 标签分开维护。</summary>
public partial class DockTabItem : DockTabItemBase
{
    /// <summary>初始化终端专用标签控件。</summary>
    public DockTabItem() => InitializeComponent();

    private TerminalTabViewModel? Terminal => (DataContext as TerminalDocument)?.Terminal;

    private void JoinChannelA_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.A);
    private void JoinChannelB_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.B);
    private void JoinChannelC_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.C);
    private void JoinChannelD_Click(object? sender, RoutedEventArgs e) => Terminal?.JoinSyncChannel(SyncInputChannel.D);
    private void LeaveSyncChannel_Click(object? sender, RoutedEventArgs e) => Terminal?.LeaveSyncChannel();
}
