using System.Reflection;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.DependencyInjection;
using VelaShell.Shell;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Tests.Layering;

/// <summary>
/// 分层硬性规则的守护测试(docs/架构设计.md §3)。规则只有测试守着才不会漂移:
/// 上一次漂移(跨层用类型名字符串识别异常)编译器完全不报错,靠人肉审查漏了一个月。
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class LayeringRulesTests
{
    private static Assembly CoreAssembly => typeof(SessionProfile).Assembly;
    private static Assembly TerminalAssembly => typeof(InputEncoder).Assembly;
    private static Assembly InfrastructureAssembly => typeof(InfrastructureServiceCollectionExtensions).Assembly;
    private static Assembly AppAssembly => typeof(MainWindowViewModel).Assembly;

    private static string[] ReferencedNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)];

    [TestMethod]
    public void Core_DoesNotReferenceAvaloniaOrUpperLayers()
    {
        string[] refs = ReferencedNames(CoreAssembly);
        Assert.IsFalse(refs.Any(r => r.StartsWith("Avalonia")), "Core 不得引用 Avalonia(保持可测、可复用)");
        Assert.IsFalse(refs.Any(r => r is "VelaShell" or "VelaShell.Terminal" or "VelaShell.Infrastructure" or "VelaShell.Presentation" or "VelaShell.Controls"), "Core 是叶子,不得引用任何上层");
    }

    [TestMethod]
    public void Terminal_OnlyDependsOnCore()
    {
        string[] refs = ReferencedNames(TerminalAssembly);
        Assert.IsFalse(refs.Any(r => r is "VelaShell" or "VelaShell.Infrastructure" or "VelaShell.Presentation" or "VelaShell.Controls"), "Terminal 只依赖 Core,不得依赖 Presentation/Infrastructure/App");
    }

    [TestMethod]
    public void Infrastructure_OnlyDependsOnCore_AndKeepsTmdsSshPrivate()
    {
        string[] infraRefs = ReferencedNames(InfrastructureAssembly);
        Assert.IsFalse(infraRefs.Any(r => r.StartsWith("Avalonia")), "Infrastructure 是无 UI 的 I/O 层,不得引用 Avalonia");
        Assert.IsFalse(infraRefs.Any(r => r is "VelaShell" or "VelaShell.Terminal" or "VelaShell.Presentation" or "VelaShell.Controls"), "Infrastructure 只依赖 Core");

        // Tmds.Ssh 只允许在 Infrastructure 出现:库异常在 TmdsSshInterop 一处翻译成
        // Core 的 VelaSsh*Exception 族,上层只认中立类型(§3 历史教训)。
        foreach (Assembly other in new[] { CoreAssembly, TerminalAssembly, AppAssembly })
        {
            Assert.IsFalse(ReferencedNames(other).Contains("Tmds.Ssh"), $"{other.GetName().Name} 不得直接引用 Tmds.Ssh");
        }
    }

    [TestMethod]
    public void App_LegacyTechnicalLayerNamespaces_StayEmpty()
    {
        // 2026-08-06 按功能重组后,旧的三大技术层命名空间已清空废弃。
        // 新类型必须进 Shell / Features.<功能> / Common(见 docs/CODEMAP.md);
        // 本测试防止"顺手放回老地方"的回潮。
        string[] banned = ["VelaShell.Views", "VelaShell.ViewModels", "VelaShell.Services"];
        string[] offenders =
        [
            .. AppAssembly
                .GetTypes()
                .Where(t => t.Namespace is { } ns && banned.Any(b => ns == b || ns.StartsWith(b + ".")))
                .Select(t => t.FullName ?? t.Name)
        ];
        Assert.AreEqual(0, offenders.Length, $"旧命名空间不得再有类型: {string.Join(", ", offenders.Take(5))}");
    }
}
