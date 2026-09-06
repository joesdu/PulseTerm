using System.Text;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件目录的内容指纹 —— 安装收据据此判断"装好之后有没有人动过它"。
/// </summary>
/// <remarks>
/// 指纹对不上的插件会被当作被篡改而拒绝加载,所以它是篡改检测的地基。
/// 这段逻辑原先埋在 2600 行的 <c>PluginManager</c> 里,只能顺着安装流程间接验;
/// 拆出来之后,"路径也进哈希""长度前缀不能省""停用标记不算内容"这几条才有地方钉。
/// </remarks>
[TestClass]
[TestCategory("PluginPackaging")]
public sealed class PluginContentHashTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"vela-hash-{Guid.NewGuid():N}");

    public PluginContentHashTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 留给系统清临时目录。
        }
        GC.SuppressFinalize(this);
    }

    private string Plugin(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string directory, string relative, string content)
    {
        string path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    [TestMethod]
    public void TheSameContentHashesTheSame()
    {
        string a = Plugin("a");
        string b = Plugin("b");
        foreach (string dir in (string[])[a, b])
        {
            Write(dir, "manifest.json", "{}");
            Write(dir, "lib/plugin.dll", "binary");
        }

        Assert.AreEqual(PluginContentHash.Compute(a), PluginContentHash.Compute(b));
    }

    [TestMethod]
    public void ChangingAByteChangesTheHash()
    {
        string dir = Plugin("a");
        Write(dir, "lib/plugin.dll", "binary");
        string before = PluginContentHash.Compute(dir);

        Write(dir, "lib/plugin.dll", "binaru");

        Assert.AreNotEqual(before, PluginContentHash.Compute(dir));
    }

    /// <summary>文件名也进哈希 —— 改名同样算篡改。</summary>
    /// <remarks>
    /// 只哈希内容的话,把 <c>plugin.dll</c> 改名成 <c>evil.dll</c> 再让清单指过去,
    /// 指纹一动不动。
    /// </remarks>
    [TestMethod]
    public void RenamingAFileChangesTheHash()
    {
        string dir = Plugin("a");
        Write(dir, "plugin.dll", "same");
        string before = PluginContentHash.Compute(dir);

        File.Move(Path.Combine(dir, "plugin.dll"), Path.Combine(dir, "evil.dll"));

        Assert.AreNotEqual(before, PluginContentHash.Compute(dir));
    }

    /// <summary>挪动文件边界不能算出同一个指纹。</summary>
    /// <remarks>
    /// 这就是"长度前缀不能省"的理由:少了它,<c>ab</c>+<c>c</c> 与 <c>a</c>+<c>bc</c>
    /// 喂进哈希的字节流一模一样,攻击者可以借此在总内容不变的前提下重排文件。
    /// </remarks>
    [TestMethod]
    public void MovingContentAcrossFileBoundariesChangesTheHash()
    {
        string a = Plugin("a");
        Write(a, "f1", "ab");
        Write(a, "f2", "c");

        string b = Plugin("b");
        Write(b, "f1", "a");
        Write(b, "f2", "bc");

        Assert.AreNotEqual(PluginContentHash.Compute(a), PluginContentHash.Compute(b));
    }

    /// <summary>停用标记不算插件内容。</summary>
    /// <remarks>
    /// 它是宿主写的。算进去的话,用户在设置页停用一次插件就改变了指纹,
    /// 下次启动即被判定为"被篡改"——一个纯粹自伤的误报。
    /// </remarks>
    [TestMethod]
    public void TheDisabledMarkerDoesNotAffectTheHash()
    {
        string dir = Plugin("a");
        Write(dir, "manifest.json", "{}");
        string before = PluginContentHash.Compute(dir);

        File.WriteAllText(Path.Combine(dir, PluginContentHash.DisabledMarkerName), "");

        Assert.AreEqual(before, PluginContentHash.Compute(dir));
    }

    /// <summary>但插件自己带的同名文件仍然算内容。</summary>
    /// <remarks>只排根目录下那一个;子目录里的同名文件是插件的东西。</remarks>
    [TestMethod]
    public void ANestedFileWithTheMarkerNameStillCounts()
    {
        string dir = Plugin("a");
        Write(dir, "manifest.json", "{}");
        string before = PluginContentHash.Compute(dir);

        Write(dir, $"sub/{PluginContentHash.DisabledMarkerName}", "payload");

        Assert.AreNotEqual(before, PluginContentHash.Compute(dir));
    }

    [TestMethod]
    public void AnEmptyDirectoryStillHashes()
    {
        // 空插件目录不该抛;它只是一份"什么都没有"的指纹。
        Assert.IsNotEmpty(PluginContentHash.Compute(Plugin("empty")));
    }
}
