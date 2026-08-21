using VelaShell.Infrastructure.Persistence;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Hosting;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 宿主自我登记(<c>~/.velashell/host.json</c>):<c>vela-plugin dev init</c> 完全依赖它
/// 找到本机安装,所以条目里的几项关键字段必须真实可用。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class HostRegistrationServiceTests
{
    [TestMethod]
    public void BuildEntry_DescribesThisInstallation()
    {
        var paths = new VelaShellStoragePaths(Path.Combine(Path.GetTempPath(), "velashell-tests", "reg"));
        HostRegistryEntry? entry = HostRegistrationService.BuildEntry(paths);

        Assert.IsNotNull(entry);
        Assert.IsTrue(File.Exists(entry.ExePath), $"登记的可执行文件必须真实存在:{entry.ExePath}");
        Assert.AreEqual(VelaPluginApi.Level, entry.ApiLevel);
        Assert.AreEqual(VelaPluginApi.SdkVersion, entry.SdkVersion);
        Assert.AreEqual(paths.RootDirectory, entry.DataRoot);
        Assert.AreEqual(paths.UserPluginDirectory, entry.UserPluginRoot);
        // Avalonia 版本是插件工程必须对齐的那一个值,漏了它工具链就只能靠 SDK 包里的硬编码猜。
        Assert.IsNotNull(entry.AvaloniaVersion);
        Assert.DoesNotContain("+", entry.AvaloniaVersion, "构建元数据后缀应被去掉");
    }

    [TestMethod]
    public void Register_WithOverriddenDataRoot_DoesNotAdvertiseItself()
    {
        // --data-root 起的是插件开发者的调试实例,数据根是临时的:
        // 让它登记会把工具链指到一个随时会被删掉的配置上。
        string temp = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        string? previous = VelaShellStoragePaths.RootDirectoryOverride;
        try
        {
            VelaShellStoragePaths.RootDirectoryOverride = temp;
            var paths = new VelaShellStoragePaths();
            HostRegistrationService.Register(paths);
            Assert.IsFalse(File.Exists(paths.HostRegistryFile));
        }
        finally
        {
            VelaShellStoragePaths.RootDirectoryOverride = previous;
        }
    }

    [TestMethod]
    public void StoragePaths_HonourExplicitRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "velashell-tests", "explicit-root");
        var paths = new VelaShellStoragePaths(root);

        Assert.AreEqual(Path.GetFullPath(root), paths.RootDirectory);
        Assert.AreEqual(Path.Combine(paths.RootDirectory, HostRegistry.FileName), paths.HostRegistryFile);
        Assert.AreEqual(Path.Combine(paths.RootDirectory, "sonnetdb"), paths.SonnetDbDirectory);
        Assert.AreEqual(Path.Combine(paths.RootDirectory, "dev-shadow"), paths.DevPluginShadowDirectory);
    }

    [TestMethod]
    public void StoragePaths_MalformedRoot_FallsBackToDefault()
    {
        // 写坏的 --data-root 该表现为"参数没生效",而不是应用起不来。
        Assert.AreEqual(VelaShellStoragePaths.DefaultRootDirectory,
            new VelaShellStoragePaths("\0not-a-path").RootDirectory);
        Assert.AreEqual(VelaShellStoragePaths.DefaultRootDirectory,
            new VelaShellStoragePaths("   ").RootDirectory);
    }
}
