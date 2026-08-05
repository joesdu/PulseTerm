using VelaShell.Core.Resources;

namespace VelaShell.Services;

/// <summary>本机可用的一种本地 shell(§12 P1-1)。CommandLine 含参数,直接交给 ConPTY 启动。</summary>
public sealed record LocalShellInfo(string Id, string Name, string CommandLine);

/// <summary>
/// 探测本机安装的 shell:PowerShell 7 / Windows PowerShell / CMD / WSL / Git Bash。
/// 只做文件存在性检查,构造命令注册表时同步调用足够便宜;非 Windows 返回空。
/// </summary>
public static class LocalShellCatalog
{
    /// <summary>探测本机已安装的 shell 并返回其列表;非 Windows 平台返回空列表。</summary>
    public static IReadOnlyList<LocalShellInfo> DetectShells()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        var shells = new List<LocalShellInfo>();
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // PowerShell 7(pwsh)优先于 Windows PowerShell;都装了就都给。
        if (FindOnPath("pwsh.exe") is { } pwsh)
        {
            shells.Add(new("pwsh", "PowerShell", Quote(pwsh) + " -NoLogo"));
        }
        string windowsPowerShell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            shells.Add(new("powershell", "Windows PowerShell", Quote(windowsPowerShell) + " -NoLogo"));
        }
        string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(system32, "cmd.exe");
        if (File.Exists(cmd))
        {
            shells.Add(new("cmd", Strings.Get("Shell_Cmd"), Quote(cmd)));
        }
        string wsl = Path.Combine(system32, "wsl.exe");
        if (File.Exists(wsl))
        {
            shells.Add(new("wsl", "WSL", Quote(wsl)));
        }
        if (FindGitBash() is { } gitBash)
        {
            shells.Add(new("gitbash", "Git Bash", Quote(gitBash) + " --login -i"));
        }
        return shells;
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里的非法片段,跳过。
            }
        }
        return null;
    }

    private static string? FindGitBash()
    {
        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        ];
        foreach (string? root in roots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }
            string candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Git 在 PATH 时,git.exe 兄弟目录里找 bash。
        // ReSharper disable once InvertIf
        if (FindOnPath("git.exe") is { } git)
        {
            string? gitRoot = Path.GetDirectoryName(Path.GetDirectoryName(git));
            if (gitRoot is null)
            {
                return null;
            }
            string candidate = Path.Combine(gitRoot, "bin", "bash.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
