using System.Text.Json;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateManifestTests
{
    private const string SampleJson = """
        {
          "version": "0.2.0",
          "tag": "v0.2.0",
          "assets": {
            "win-x64":   { "name": "VelaShell-0.2.0-win-x64.zip",     "sha256": "aa11", "size": 100 },
            "win-arm64": { "name": "VelaShell-0.2.0-win-arm64.zip",   "sha256": "bb22", "size": 200 },
            "osx-x64":   { "name": "VelaShell-0.2.0-osx-x64.tar.gz",  "sha256": "cc33", "size": 300 },
            "osx-arm64": { "name": "VelaShell-0.2.0-osx-arm64.tar.gz","sha256": "dd44", "size": 400 },
            "linux-x64": { "name": "VelaShell-0.2.0-linux-x64.tar.gz","sha256": "ee55", "size": 500 },
            "linux-arm64":{ "name": "VelaShell-0.2.0-linux-arm64.tar.gz", "sha256": "ff66", "size": 600 }
          }
        }
        """;

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_ValidManifest_ExposesAllFields()
    {
        var manifest = UpdateManifest.Parse(SampleJson);
        Assert.AreEqual("0.2.0", manifest.Version);
        Assert.AreEqual("v0.2.0", manifest.Tag);
        Assert.HasCount(6, manifest.Assets);
        UpdateAsset asset = manifest.Assets["win-x64"];
        Assert.AreEqual("VelaShell-0.2.0-win-x64.zip", asset.Name);
        Assert.AreEqual("aa11", asset.Sha256);
        Assert.AreEqual(100, asset.Size);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AssetForCurrentPlatform_KnownRids_ReturnsMatch()
    {
        // 测试跑在 win/osx/linux 的 x64/arm64 上,CurrentRid 一定命中样例清单。
        var manifest = UpdateManifest.Parse(SampleJson);
        string? rid = UpdateManifest.CurrentRid();
        Assert.IsNotNull(rid);
        UpdateAsset? asset = manifest.AssetForCurrentPlatform();
        Assert.IsNotNull(asset);
        Assert.AreEqual(manifest.Assets[rid].Name, asset.Name);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AssetForCurrentPlatform_MissingRid_ReturnsNull()
    {
        var manifest = UpdateManifest.Parse("""
            { "version": "0.2.0", "tag": "v0.2.0",
              "assets": { "solaris-sparc": { "name": "n", "sha256": "s", "size": 1 } } }
            """);
        Assert.IsNull(manifest.AssetForCurrentPlatform());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_MissingVersion_Throws()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            UpdateManifest.Parse("""{ "tag": "v1", "assets": { "a": { "name": "n", "sha256": "s" } } }"""));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_EmptyAssets_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() =>
            UpdateManifest.Parse("""{ "version": "1.0.0", "tag": "v1.0.0", "assets": {} }"""));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_MalformedJson_Throws()
    {
        // JsonReaderException 等派生类也算,用非严格断言。
        Assert.Throws<JsonException>(() => UpdateManifest.Parse("not json"));
    }
}
