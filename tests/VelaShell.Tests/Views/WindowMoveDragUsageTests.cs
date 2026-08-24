using System.Text.RegularExpressions;

namespace VelaShell.Tests.Views;

/// <summary>
/// 自绘标题栏起拖必须走 <c>BeginWindowMoveDrag</c> 的约定检查(静态扫描 .cs,不实例化窗口)。
/// </summary>
/// <remarks>
/// Avalonia 12 的 Win32 <c>BeginMoveDrag</c> 在系统移动模态循环结束后,会给窗口补一条
/// <c>WM_LBUTTONUP(wParam: 0, lParam: 0)</c>。那个 <c>lParam = 0</c> 被 Avalonia 解码成
/// 客户区 (0,0) 的指针弹起,于是 pointer-over 落到窗口左上角 —— 自绘窗体的左上角正压着
/// NorthWest 缩放抓取区(TopLeftCorner 光标),光标便闪一下对角双箭头(#264);没有抓取区的
/// 对话框则会在左上角留下一块悬停高亮。
///
/// 修法在 <c>VelaShell.Views.WindowMoveDrag</c>:装 WndProc 钩子把那条消息的坐标换成光标真实
/// 位置。但它只在【经 BeginWindowMoveDrag 起拖】时才装得上,任何一处直接调
/// <c>BeginMoveDrag</c> 的窗体就重新长回这个毛病,故在此把约定钉死。
///
/// PluginHost 是独立进程、按依赖纪律不引用主程序工程,自带一份同源实现,不在本扫描范围内。
/// </remarks>
[TestClass]
[TestCategory("WindowChrome")]
public sealed partial class WindowMoveDragUsageTests
{
    /// <summary>允许出现裸 <c>BeginMoveDrag</c> 的文件:修复本身就在这里。</summary>
    private const string FixFile = "WindowMoveDrag.cs";

    [TestMethod]
    public void SelfDrawnTitleBars_AlwaysDragThroughTheFixedEntryPoint()
    {
        var offenders = new List<string>();
        foreach (string file in MainAppSourceFiles())
        {
            if (Path.GetFileName(file).Equals(FixFile, StringComparison.Ordinal))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // 只揪真实调用(BeginMoveDrag( 前面不是 BeginWindow…),注释与文档里的提名不算。
                string line = lines[i];
                if (!BareBeginMoveDragRegex.IsMatch(line) || IsCommentLine(line))
                {
                    continue;
                }
                offenders.Add($"{Path.GetFileName(file)}:{i + 1} → {line.Trim()}");
            }
        }
        Assert.IsEmpty(offenders,
            "自绘标题栏起拖一律走 window.BeginWindowMoveDrag(e):直接调 BeginMoveDrag 会漏掉"
            + " Avalonia 合成的 (0,0) 幽灵弹起纠正,点标题栏时光标闪成缩放样式(#264):\n"
            + string.Join("\n", offenders));
    }

    private static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal);
    }

    /// <summary>主程序 src 下所有 .cs(跳过 obj/bin 与独立进程 PluginHost)。</summary>
    private static IEnumerable<string> MainAppSourceFiles()
    {
        string root = SourceRoot();
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                && !file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                && !file.Contains($"{sep}VelaShell.PluginHost{sep}", StringComparison.Ordinal));
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

    [GeneratedRegex(@"(?<!BeginWindow)(?<!\w)BeginMoveDrag\s*\(")]
    private static partial Regex BareBeginMoveDragRegex { get; }
}
