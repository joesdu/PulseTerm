using System.Diagnostics;
using Microsoft.Win32;

namespace VelaShell.Services;

/// <summary>
/// 开机自启动(设置 → 常规 → 启动):Windows 上写 HKCU\...\Run 键,当前用户生效、无需管理员;
/// 其他平台暂不支持(静默忽略)。应用启动与设置保存时各同步一次,保证键值与设置一致。
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VelaShell";

    /// <summary>按开关同步开机自启动注册表项:开启则写入当前可执行文件路径,关闭则移除;非 Windows 或写入失败时静默忽略。</summary>
    public static void Apply(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key is null)
                {
                    return;
                }
                if (enabled)
                {
                    string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(ValueName, $"\"{exePath}\"");
                    }
                }
                else if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
                // 注册表不可写(策略限制等)时静默失败,不影响其余设置生效。
            }
        }
    }
}
