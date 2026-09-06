using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 端到端的行复用回归:连续两轮采样后,同 key 的行必须还是同一个对象。
/// </summary>
/// <remarks>
/// 与 <see cref="ResourceMonitorRowMergeTests" /> 的分工:那边钉集合算法本身,
/// 这边钉六个调用点确实传对了 key 与 update —— 传错 key(比如拿会变的文本当 key)
/// 算法照样"正确",但行仍然每轮重建。
/// </remarks>
public sealed partial class ResourceMonitorUiTests
{
    [TestMethod]
    public void ProcessRows_WithTheSamePid_AreReusedAcrossSamples()
    {
        OnUi(() =>
        {
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithGpu());

            Dictionary<int, ProcessRow> before = vm.TopMemoryProcesses.ToDictionary(static row => row.Pid);
            Assert.IsNotEmpty(before, "样本里应当有内存占用最高的进程行。");

            Pump(vm);

            Assert.IsNotEmpty(vm.TopMemoryProcesses);
            foreach (ProcessRow row in vm.TopMemoryProcesses)
            {
                if (before.TryGetValue(row.Pid, out ProcessRow? original))
                {
                    Assert.IsTrue(ReferenceEquals(original, row),
                        $"PID {row.Pid} 每个采样周期都被换成了新对象 —— 行复用没生效。");
                }
            }

            window.Close();
        });
    }

    [TestMethod]
    public void PartitionRows_WithTheSameMountPoint_AreReusedAcrossSamples()
    {
        OnUi(() =>
        {
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithGpu());

            Dictionary<string, PartitionRow> before = vm.Partitions.ToDictionary(static row => row.MountPoint);
            Assert.IsNotEmpty(before);

            Pump(vm);

            foreach (PartitionRow row in vm.Partitions)
            {
                if (before.TryGetValue(row.MountPoint, out PartitionRow? original))
                {
                    Assert.IsTrue(ReferenceEquals(original, row),
                        $"挂载点 {row.MountPoint} 被换成了新行对象。");
                }
            }

            window.Close();
        });
    }

    [TestMethod]
    public void CoreRows_InListView_AreReusedAcrossSamples()
    {
        OnUi(() =>
        {
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithGpu());
            vm.SetCoreViewCommand.Execute("List").Subscribe();
            Dispatcher.UIThread.RunJobs();

            CoreRow[] before = [.. vm.CoreRows];
            Assert.IsNotEmpty(before, "列表视图下应当逐核心一行。");

            Pump(vm);

            Assert.HasCount(before.Length, vm.CoreRows);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.IsTrue(ReferenceEquals(before[i], vm.CoreRows[i]),
                    $"核心 {before[i].Label} 被换成了新行对象。");
            }

            window.Close();
        });
    }

    [TestMethod]
    public void GpuProcessRows_AreReusedAcrossSamples()
    {
        OnUi(() =>
        {
            (ResourceMonitorWindow window, ResourceMonitorWindowViewModel vm) = Open(WithGpu());

            Dictionary<(string, int), GpuProcessRow> before =
                vm.GpuProcesses.ToDictionary(static row => (row.GpuText, row.Pid));

            Pump(vm);

            foreach (GpuProcessRow row in vm.GpuProcesses)
            {
                if (before.TryGetValue((row.GpuText, row.Pid), out GpuProcessRow? original))
                {
                    Assert.IsTrue(ReferenceEquals(original, row),
                        $"GPU 进程 {row.GpuText}/{row.Pid} 被换成了新行对象。");
                }
            }

            window.Close();
        });
    }
}
