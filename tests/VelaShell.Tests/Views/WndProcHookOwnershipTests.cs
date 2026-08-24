using System.Text.RegularExpressions;

namespace VelaShell.Tests.Views;

/// <summary>
/// 一个窗口只能有一个 WndProc 钩子的约定检查(静态扫描 .cs,不实例化窗口)。
/// </summary>
/// <remarks>
/// <c>Win32Properties.AddWndProcHookCallback</c> 名字像"添加",实际是往同一个委托上 <c>+=</c>,
/// 而 <c>WindowImpl.WndProcMessageHandler</c> 只取【最后一个回调】的返回值:
/// <code>
/// if (WndProcHookCallback is { } callback) ret = callback(hWnd, msg, wParam, lParam, ref handled);
/// if (handled) return ret;
/// </code>
/// 于是第二个钩子哪怕什么都不干、老老实实 <c>return IntPtr.Zero</c>,也会把前一个钩子给
/// WM_NCHITTEST 返回的 HTCLIENT/HTMAXBUTTON 覆盖成 0(HTNOWHERE)—— 整窗对鼠标失聪:
/// 标题栏拖不动、最小化/关闭按钮点不了(2026-08-24 修 #264 时真踩过一次)。
///
/// 所以返回值的所有权归 <see cref="VelaShell.Views.Win32WindowChrome" /> 一家:要挂新的消息处理,
/// 加到它的 <c>WndProc</c> 里去,不要再调一次 AddWndProcHookCallback。
/// </remarks>
[TestClass]
[TestCategory("WindowChrome")]
public sealed partial class WndProcHookOwnershipTests
{
    /// <summary>唯一允许注册 WndProc 钩子的文件。</summary>
    private const string OwnerFile = "Win32WindowChrome.cs";

    [TestMethod]
    public void OnlyTheWindowChromeRegistersAWndProcHook()
    {
        var offenders = new List<string>();
        foreach (string file in SourceFiles())
        {
            if (Path.GetFileName(file).Equals(OwnerFile, StringComparison.Ordinal))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // 只揪真实调用;注释/文档里提名(比如解释为什么不能挂)不算。
                if (!HookRegistrationRegex.IsMatch(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                offenders.Add($"{Path.GetFileName(file)}:{i + 1} → {line.Trim()}");
            }
        }
        Assert.IsEmpty(offenders,
            $"WndProc 钩子的返回值所有权归 {OwnerFile} 一家:AddWndProcHookCallback 是 += 多播,"
            + "而 Avalonia 只取最后一个回调的返回值,第二个钩子会把 WM_NCHITTEST 的 HTCLIENT 覆盖成 "
            + "HTNOWHERE,整窗对鼠标失聪(拖不动、按钮点不了)。新的消息处理请并入 Win32WindowChrome.WndProc:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>src 下所有 .cs(跳过 obj/bin)。</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string root = SourceRoot();
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                && !file.Contains($"{sep}bin{sep}", StringComparison.Ordinal));
    }

    /// <summary>从测试输出目录向上找到仓库里的 src 目录。</summary>
    private static string SourceRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "src");
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库的 src 目录(找不到同级的 VelaShell.slnx)。");
    }

    [GeneratedRegex(@"AddWndProcHookCallback\s*\(")]
    private static partial Regex HookRegistrationRegex { get; }
}
