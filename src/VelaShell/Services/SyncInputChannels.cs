using Avalonia.Media;

namespace VelaShell.Services;

/// <summary>同步输入频道:同一频道内任意标签的用户输入会实时复制到频道内其他标签。</summary>
public enum SyncInputChannel
{
    /// <summary>频道 A(粉)。</summary>
    A,

    /// <summary>频道 B(蓝)。</summary>
    B,

    /// <summary>频道 C(橙)。</summary>
    C,

    /// <summary>频道 D(绿)。</summary>
    D
}

/// <summary>
/// 同步输入频道的固定标识色:标签右键菜单的色块、标签头的频道字母与终端上方横条
/// 三处同色联动,让"哪些标签在同一频道"可以靠余光辨认。
/// </summary>
/// <remarks>
/// 取当前主题的 <c>VelaSyncChannelA..D</c> 令牌(由种子色派生)。原先写死四个
/// Dracula 色值,亮色主题下徽章整体失配且绕过对比度把关。每次现取不缓存 ——
/// 令牌值会随主题切换整体替换。
/// </remarks>
public static class SyncInputChannels
{
    /// <summary>频道 A 的标识色(粉)。</summary>
    public static IBrush BrushA => ThemeBrushes.Resolve("VelaSyncChannelA", Color.Parse("#FF79C6"));

    /// <summary>频道 B 的标识色(蓝)。</summary>
    public static IBrush BrushB => ThemeBrushes.Resolve("VelaSyncChannelB", Color.Parse("#6FA8FF"));

    /// <summary>频道 C 的标识色(橙)。</summary>
    public static IBrush BrushC => ThemeBrushes.Resolve("VelaSyncChannelC", Color.Parse("#FFB86C"));

    /// <summary>频道 D 的标识色(绿)。</summary>
    public static IBrush BrushD => ThemeBrushes.Resolve("VelaSyncChannelD", Color.Parse("#50FA7B"));

    /// <summary>返回频道的标识色画刷。</summary>
    /// <param name="channel">频道。</param>
    /// <returns>该频道的标识色画刷。</returns>
    public static IBrush BrushFor(SyncInputChannel channel) =>
        channel switch
        {
            SyncInputChannel.A => BrushA,
            SyncInputChannel.B => BrushB,
            SyncInputChannel.C => BrushC,
            _ => BrushD
        };
}
