using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VelaShell.Services;

/// <summary>
/// 判断本进程是否以 MSIX 包身份运行(即从 Microsoft Store 安装的版本)。
/// <para>
/// 便携版与商店版共用同一份源码、同一套二进制,两者的行为差异全部由这里的<b>运行时</b>判断驱动,
/// 而不是编译期常量 —— 这样就不存在"商店构建误发到 GitHub Releases"或反过来的事故,
/// CI 也不必维护两套编译配置:同样的发布产物,打进 MSIX 就是商店版,压成 zip 就是便携版。
/// </para>
/// <para>
/// 目前唯一的差异是自更新:MSIX 装在 <c>C:\Program Files\WindowsApps</c>,该目录只读且 ACL 锁死,
/// 换版根本无从谈起;商店政策也要求包应用只能经商店更新。见 <see cref="IUpdateService.IsStoreManaged" />。
/// </para>
/// </summary>
internal static partial class AppPackaging
{
    /// <summary>缓冲区不足 —— 说明确实取到了包全名(本进程有包身份)。</summary>
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>本进程没有包身份(便携版直接运行的常态)。</summary>
    private const uint AppModelErrorNoPackage = 15700;

    /// <summary>本进程是否以 MSIX 包身份运行。进程内不会变化,构造一次即可。</summary>
    public static bool IsPackaged { get; } = DetectPackageIdentity();

    /// <summary>
    /// 以长度 0 的缓冲区试探 <c>GetCurrentPackageFullName</c>:有包身份时因装不下返回
    /// <see cref="ErrorInsufficientBuffer" />,没有则返回 <see cref="AppModelErrorNoPackage" />。
    /// 这是官方推荐的判定法,比查进程路径是否落在 WindowsApps 之类的启发式可靠。
    /// </summary>
    private static bool DetectPackageIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            int length = 0;
            uint result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            if (result is ErrorInsufficientBuffer or AppModelErrorNoPackage)
            {
                return result == ErrorInsufficientBuffer;
            }
            Trace.WriteLine($"[VelaShell] Unexpected GetCurrentPackageFullName result {result}; assuming unpackaged.");
            return false;
        }
        catch (Exception ex)
        {
            // Windows 8 以前没有这个导出;取不到就按便携版处理,自更新自会因目录不可写而止步。
            Trace.WriteLine($"[VelaShell] Package identity probe failed, assuming unpackaged: {ex.Message}");
            return false;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial uint GetCurrentPackageFullName(ref int packageFullNameLength, IntPtr packageFullName);
}
