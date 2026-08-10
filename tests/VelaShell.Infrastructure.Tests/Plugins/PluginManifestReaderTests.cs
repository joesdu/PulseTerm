using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Manifest;

namespace VelaShell.Infrastructure.Tests.Plugins;

[TestClass]
[TestCategory("Plugins")]
public class PluginManifestReaderTests
{
    [TestMethod]
    public void Parse_ValidManifestWithCommentsAndTrailingCommas_Succeeds()
    {
        const string json = """
            {
              // 注释与尾逗号都允许
              "id": "acme.my-plugin",
              "version": "1.2.0-beta.1",
              "displayName": "My Plugin",
              "entry": "MyPlugin.dll",
              "apiLevel": 1,
            }
            """;
        PluginManifest manifest = PluginManifestReader.Parse(json);
        Assert.AreEqual("acme.my-plugin", manifest.Id);
        Assert.AreEqual("1.2.0-beta.1", manifest.Version);
        Assert.AreEqual(1, manifest.ApiLevel);
    }

    [TestMethod]
    public void Parse_MissingRequiredField_GivesReadableError()
    {
        PluginManifestException ex = Assert.ThrowsExactly<PluginManifestException>(() =>
            PluginManifestReader.Parse("""{ "id": "a.b", "version": "1.0.0", "displayName": "X" }"""));
        StringAssert.Contains(ex.Message, "JSON");
    }

    [TestMethod]
    [DataRow("Acme.Plugin")] // 大写
    [DataRow(".starts-with-dot")]
    [DataRow("ends-with-dash-")]
    [DataRow("has space")]
    [DataRow("")]
    public void Validate_BadId_Rejected(string id)
    {
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse($$"""
            { "id": "{{id}}", "version": "1.0.0", "displayName": "X", "entry": "X.dll" }
            """));
    }

    [TestMethod]
    [DataRow("not-a-version")]
    [DataRow("1")]
    [DataRow("v1.0.0")]
    public void Validate_BadVersion_Rejected(string version)
    {
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse($$"""
            { "id": "a.b", "version": "{{version}}", "displayName": "X", "entry": "X.dll" }
            """));
    }

    [TestMethod]
    [DataRow("../escape.dll")] // 目录逃逸
    [DataRow("sub/../../escape.dll")]
    [DataRow("C:/abs/path.dll")]
    [DataRow("/abs/path.dll")]
    [DataRow("not-a-dll.exe")]
    [DataRow("")]
    public void Validate_BadEntry_Rejected(string entry)
    {
        Assert.ThrowsExactly<PluginManifestException>(() => PluginManifestReader.Parse($$"""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "{{entry}}" }
            """));
    }

    [TestMethod]
    public void Validate_SubdirectoryEntry_Allowed()
    {
        PluginManifest manifest = PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "bin/X.dll" }
            """);
        Assert.AreEqual("bin/X.dll", manifest.Entry);
    }
}
