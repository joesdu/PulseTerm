using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class VelaShellStoragePathsTests
{
    [TestMethod]
    public void Paths_AreGenerated_Under_UserProfile_DotVelaShell_Root()
    {
        var paths = new VelaShellStoragePaths();

        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell"),
            paths.RootDirectory);
        Assert.EndsWith("settings.json", paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("sonnetdb", paths.SonnetDbDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("secret.key", paths.SecretKeyFile, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell", "plugins"),
            paths.UserPluginDirectory);
        Assert.AreEqual(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelaShell"),
            paths.LegacyLocalAppDataDirectory);
    }
}
