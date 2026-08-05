using Avalonia.Media;

namespace VelaShell.Services;

/// <summary>
/// 每个连接配置的稳定标识色:按 Profile.Id 哈希映射到固定色板,同一配置在任何会话、
/// 任何启动中始终得到同一颜色。用于标签页与 SFTP 面板的颜色联动,让"下方文件面板属于
/// 哪台服务器"可以靠余光辨认,防止多标签时误操作别的服务器。
/// 色板取 Dracula 强调色系——与终端底色(#282A36 家族)同源,深浅主题下均可辨。
/// </summary>
public static class ConnectionAccent
{
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#8BE9FD")), // cyan
        new SolidColorBrush(Color.Parse("#50FA7B")), // green
        new SolidColorBrush(Color.Parse("#FFB86C")), // orange
        new SolidColorBrush(Color.Parse("#FF79C6")), // pink
        new SolidColorBrush(Color.Parse("#BD93F9")), // purple
        new SolidColorBrush(Color.Parse("#F1FA8C")), // yellow
        new SolidColorBrush(Color.Parse("#FF5555")), // red
        new SolidColorBrush(Color.Parse("#6FA8FF"))  // blue
    ];

    /// <summary>返回该配置的标识色画刷(FNV-1a 哈希 Guid 字节,跨启动稳定)。</summary>
    public static IBrush BrushFor(Guid profileId)
    {
        uint hash = 2166136261;
        foreach (byte b in profileId.ToByteArray())
        {
            hash = (hash ^ b) * 16777619;
        }
        return Palette[hash % (uint)Palette.Length];
    }
}
