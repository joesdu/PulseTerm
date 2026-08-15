using Avalonia.Media;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 处理中的输入框边框:两枚彗尾沿着边框跑圈(从左上角与右下角对称出发)。
/// 请求一结束就熄灭 —— 有没有在跑,余光扫一眼边框就知道,不必盯着按钮。
/// </summary>
public partial class ChatPanelView
{
    /// <summary>
    /// 开/关流光。光是<b>盖在</b>输入框上画的(见 <see cref="BorderGlowOverlay" />),
    /// 底下那圈边框保持它本来的主题色 —— 于是焦点态/悬停态照常生效,
    /// 熄灭时也不需要恢复什么颜色。
    /// </summary>
    private void SetBusyGlow(bool on)
    {
        if (on)
        {
            // 主题可能在两次请求之间被切过,每次点亮都重新取色
            InputGlow.Core = ResolveColor("VelaAccent", Color.FromRgb(0xBD, 0x93, 0xF9));
            InputGlow.Halo = ResolveColor("VelaShellCyan", Color.FromRgb(0x8B, 0xE9, 0xFD));
            InputGlow.Rail = ResolveColor("VelaBorderSecondary", Color.FromRgb(0x44, 0x47, 0x5A));
        }
        InputGlow.IsRunning = on;
    }
}
