using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class VelaShellDataMigrationTests
{
    private string _base = null!;
    private string _source = null!;
    private string _target = null!;

    [TestInitialize]
    public void Setup()
    {
        _base = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_base, "local-app-data");
        _target = Path.Combine(_base, ".velashell");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_target);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    [TestMethod]
    public void MigrateDirectory_MovesEntireTree_AndRemovesSource()
    {
        Directory.CreateDirectory(Path.Combine(_source, "sonnetdb", "nested"));
        File.WriteAllText(Path.Combine(_source, "secret.key"), "secret");
        File.WriteAllText(Path.Combine(_source, "sonnetdb", "nested", "data.bin"), "database");

        VelaShellDataMigration.MigrateDirectory(_source, _target);

        Assert.IsFalse(Directory.Exists(_source));
        Assert.AreEqual("secret", File.ReadAllText(Path.Combine(_target, "secret.key")));
        Assert.AreEqual("database", File.ReadAllText(Path.Combine(_target, "sonnetdb", "nested", "data.bin")));
        Assert.IsTrue(File.Exists(Path.Combine(_target, VelaShellDataMigration.CompletionMarkerFileName)));
    }

    [TestMethod]
    public void MigrateDirectory_ConflictingTarget_IsBackedUp_AndCurrentDataWins()
    {
        File.WriteAllText(Path.Combine(_source, "settings.json"), "current");
        File.WriteAllText(Path.Combine(_target, "settings.json"), "legacy-dot-directory");

        VelaShellDataMigration.MigrateDirectory(_source, _target);

        Assert.AreEqual("current", File.ReadAllText(Path.Combine(_target, "settings.json")));
        Assert.AreEqual("legacy-dot-directory", File.ReadAllText(Path.Combine(
            _target, VelaShellDataMigration.BackupDirectoryName, "localappdata", "settings.json")));
    }

    [TestMethod]
    public void MigrateDirectory_CompletionMarker_PreventsFutureLegacyAccess()
    {
        VelaShellDataMigration.MigrateDirectory(_source, _target);
        Directory.CreateDirectory(_source);
        File.WriteAllText(Path.Combine(_source, "late.json"), "must-not-import");

        VelaShellDataMigration.MigrateDirectory(_source, _target);

        Assert.IsTrue(Directory.Exists(_source));
        Assert.IsFalse(File.Exists(Path.Combine(_target, "late.json")));
    }

    [TestMethod]
    public void MigrateDirectory_RejectsNestedRoots()
    {
        string nested = Path.Combine(_source, "nested");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            VelaShellDataMigration.MigrateDirectory(_source, nested));
    }
}
