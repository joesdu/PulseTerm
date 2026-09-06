using System.IO.Compression;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 解包时的三道安全闸:路径逃逸、解压炸弹、条目数轰炸。
/// </summary>
/// <remarks>
/// 这些规则原先埋在 2600 行的 <c>PluginManager</c> 里,只能顺着完整安装流程间接验 ——
/// 而那条路上得先造出一个带合法签名的 <c>.vpx</c> 容器,于是触发上限的夹具根本写不出来。
/// 拆成独立的解包器之后,每道闸都能拿一个几十字节的 zip 直接怼。
/// </remarks>
[TestClass]
[TestCategory("PluginPackaging")]
public sealed class PluginPackageExtractorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"vela-extract-{Guid.NewGuid():N}");

    public PluginPackageExtractorTests() => Directory.CreateDirectory(_root);

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

    private string Destination => Path.Combine(_root, "out");

    /// <summary>造一个内存 zip,条目名与内容按给定的来。</summary>
    private static ZipArchive Zip(params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var writing = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = writing.CreateEntry(name);
                using Stream stream = entry.Open();
                stream.Write(content);
            }
        }
        buffer.Position = 0;
        return new(buffer, ZipArchiveMode.Read);
    }

    [TestMethod]
    public void APlainPackageIsExtracted()
    {
        using ZipArchive archive = Zip(
            ("manifest.json", "{}"u8.ToArray()),
            ("lib/plugin.dll", [1, 2, 3]));

        PluginPackageExtractor.ExtractSafely(archive, Destination);

        Assert.AreEqual("{}", File.ReadAllText(Path.Combine(Destination, "manifest.json")));
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3 },
            File.ReadAllBytes(Path.Combine(Destination, "lib", "plugin.dll")));
    }

    /// <summary>写到目标目录之外的条目一律拒绝(zip-slip)。</summary>
    /// <remarks>
    /// 这是最经典的一条:一个 <c>../../evil.dll</c> 就能往应用目录里塞东西,
    /// 而用户以为自己只是装了个插件。
    /// </remarks>
    [TestMethod]
    public void AnEntryEscapingTheDestinationIsRejected()
    {
        using ZipArchive archive = Zip(("../escaped.txt", "x"u8.ToArray()));

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginPackageExtractor.ExtractSafely(archive, Destination));

        Assert.Contains("path escape", error.Message);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "escaped.txt")), "逃逸的文件绝不能落盘。");
    }

    [TestMethod]
    public void ADeepEscapeIsAlsoRejected()
    {
        // 混在正常路径中间的 .. 同样要拦 —— 只看开头是不是 ".." 是挡不住的。
        using ZipArchive archive = Zip(("lib/../../escaped.txt", "x"u8.ToArray()));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginPackageExtractor.ExtractSafely(archive, Destination));
    }

    /// <summary>解压后体积超预算即中止。</summary>
    /// <remarks>
    /// <b>按实际写出的字节记账,而不是信 <c>entry.Length</c></b> —— 中央目录里的长度是包
    /// 自己写的,炸弹包大可以谎报 1 KB 再吐出 10 GB。这条用例正是靠"边写边数"才拦得住:
    /// 全零内容压缩比极高,压出来的 zip 只有几 KB。
    /// </remarks>
    [TestMethod]
    public void AZipBombIsStoppedMidStream()
    {
        byte[] chunk = new byte[16 * 1024 * 1024]; // 全零,压缩后只有几 KB
        var entries = new List<(string, byte[])>();
        for (int i = 0; i < 40; i++)   // 合计 640 MB > 512 MB 上限
        {
            entries.Add(($"bomb{i}.bin", chunk));
        }
        using ZipArchive archive = Zip([.. entries]);

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginPackageExtractor.ExtractSafely(archive, Destination));

        Assert.Contains("unpacked size exceeds", error.Message);
    }

    /// <summary>条目数超限即拒绝,一个字节都不写。</summary>
    /// <remarks>
    /// 与字节预算是两道闸:一百万个空文件加起来没几个字节,但每一个都要建一个目录项 ——
    /// 光是解包就能把文件系统拖住。
    /// </remarks>
    [TestMethod]
    public void TooManyEntriesAreRejectedBeforeAnythingIsWritten()
    {
        var entries = new List<(string, byte[])>();
        for (int i = 0; i <= PluginPackageExtractor.MaxPackageEntries; i++)
        {
            entries.Add(($"f{i}.txt", []));
        }
        using ZipArchive archive = Zip([.. entries]);

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginPackageExtractor.ExtractSafely(archive, Destination));

        Assert.Contains("entries", error.Message);
        Assert.IsFalse(Directory.Exists(Destination), "条目数闸要在动手写之前就拦下。");
    }

    [TestMethod]
    public void DirectoryEntriesAreCreatedNotWritten()
    {
        // 目录条目以 / 结尾且没有内容;当成文件去 File.Create 会抛。
        using ZipArchive archive = Zip(("assets/", []), ("assets/icon.svg", "<svg/>"u8.ToArray()));

        PluginPackageExtractor.ExtractSafely(archive, Destination);

        Assert.IsTrue(Directory.Exists(Path.Combine(Destination, "assets")));
        Assert.IsTrue(File.Exists(Path.Combine(Destination, "assets", "icon.svg")));
    }
}
