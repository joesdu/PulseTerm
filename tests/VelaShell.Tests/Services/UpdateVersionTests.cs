using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateVersionTests
{
    [TestMethod]
    [TestCategory("Update")]
    [DataRow("1.2.3", 1, 2, 3, "")]
    [DataRow("v1.2.3", 1, 2, 3, "")]
    [DataRow("0.1.0-beta", 0, 1, 0, "beta")]
    [DataRow("0.1.0.0", 0, 1, 0, "")]
    [DataRow("1.2", 1, 2, 0, "")]
    [DataRow("1.2.3+build.5", 1, 2, 3, "")]
    public void TryParse_ValidInput_Succeeds(string text, int major, int minor, int patch, string pre)
    {
        Assert.IsTrue(UpdateVersion.TryParse(text, out UpdateVersion v));
        Assert.AreEqual(major, v.Major);
        Assert.AreEqual(minor, v.Minor);
        Assert.AreEqual(patch, v.Patch);
        Assert.AreEqual(pre, v.PreRelease);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("1")]
    [DataRow("1.2.3.4.5")]
    [DataRow("1.-2.3")]
    [DataRow("1.2.3-")]
    public void TryParse_InvalidInput_Fails(string? text)
    {
        Assert.IsFalse(UpdateVersion.TryParse(text, out _));
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("1.0.0", "0.9.9")]
    [DataRow("0.2.0", "0.1.9")]
    [DataRow("0.1.1", "0.1.0")]
    // 正式版大于同数字的预发布版
    [DataRow("0.1.0", "0.1.0-beta")]
    // 预发布逐段比较:数字段按数值,且数字段小于非数字段
    [DataRow("0.1.0-beta.2", "0.1.0-beta.1")]
    [DataRow("0.1.0-beta.10", "0.1.0-beta.9")]
    [DataRow("0.1.0-beta", "0.1.0-alpha")]
    [DataRow("0.1.0-beta.1", "0.1.0-beta")]
    public void CompareTo_LeftIsNewer(string newer, string older)
    {
        Assert.IsTrue(UpdateVersion.TryParse(newer, out UpdateVersion a));
        Assert.IsTrue(UpdateVersion.TryParse(older, out UpdateVersion b));
        Assert.IsTrue(a > b, $"{newer} should be newer than {older}");
        Assert.IsTrue(b < a);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CompareTo_EqualVersions_AreEqual()
    {
        Assert.IsTrue(UpdateVersion.TryParse("1.2.3-beta", out UpdateVersion a));
        Assert.IsTrue(UpdateVersion.TryParse("v1.2.3-beta", out UpdateVersion b));
        Assert.IsTrue(a == b);
        Assert.AreEqual(0, a.CompareTo(b));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void ToString_RoundTrips()
    {
        Assert.IsTrue(UpdateVersion.TryParse("1.2.3-beta.1", out UpdateVersion v));
        Assert.AreEqual("1.2.3-beta.1", v.ToString());
    }
}
