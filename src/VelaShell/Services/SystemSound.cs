using System.Runtime.InteropServices;

namespace VelaShell.Services;

/// <summary>
/// 系统提示音(设置 → 常规 → 声音提示):Windows 上用 user32 MessageBeep,
/// 无外部依赖;其他平台静默。
/// </summary>
public static partial class SystemSound
{
    /// <summary>播放一次系统警告提示音;非 Windows 平台或调用失败时静默返回。</summary>
    public static void Alert()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            MessageBeep(0x30 /* MB_ICONWARNING */);
        }
        catch
        {
            // 提示音失败无关紧要。
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
