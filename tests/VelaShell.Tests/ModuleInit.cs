using System.Runtime.CompilerServices;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;

// 本程序集的 UI 测试共用**同一条** headless UI 线程(各测试类都走
// HeadlessUnitTestSession.GetOrStartForAssembly),Dispatch 的工作项在那条线程上顺序执行。
// 并行跑测试只会让多个工作项互相等同一条线程,把顺序问题变成死锁。
// 这条约束此前只写在 EnglishInputLocaleUiTests 一个类上,既不完整、也容易让人误以为
// 「卡死是并行引起的」——真正的根因是共享 UI 线程被单个未返回的工作项占死(见 velashell.runsettings)。
[assembly: DoNotParallelize]

namespace VelaShell.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            RxAppBuilder.CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized by another path
        }
    }
}
