using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 重名让路的拆名与递增。
/// </summary>
/// <remarks>
/// 本地下载与远端上传原先各写过一遍同样的逻辑,一处用 <c>Path</c> 一处手撸 ——
/// 两边对边缘输入的判断本来就不一定一致,而"下载下来的文件名跟服务器上不一样"
/// 是一类很难被想到去查的现象。合成一处之后,规则才有地方钉。
/// </remarks>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class UniqueNamesTests
{
    [TestMethod]
    public void APlainNameSplitsAtTheDot() =>
        Assert.AreEqual(("file", ".txt"), UniqueNames.SplitExtension("file.txt"));

    /// <summary>取最后一个点。</summary>
    /// <remarks>
    /// <c>archive.tar.gz</c> 让路之后是 <c>archive.tar (1).gz</c> ——
    /// 与用户在资源管理器里看到的习惯一致。按第一个点拆会得到 <c>archive (1).tar.gz</c>。
    /// </remarks>
    [TestMethod]
    public void ADoubleExtensionSplitsAtTheLastDot() =>
        Assert.AreEqual(("archive.tar", ".gz"), UniqueNames.SplitExtension("archive.tar.gz"));

    /// <summary>点在开头不算扩展名。</summary>
    /// <remarks>
    /// <c>.bashrc</c> 整个是主干,让路之后是 <c>.bashrc (1)</c> —— 而不是
    /// <c>(1).bashrc</c>,后者在 Unix 上会变成一个完全不同语义的隐藏文件名。
    /// </remarks>
    [TestMethod]
    public void ADotfileKeepsItsWholeName()
    {
        Assert.AreEqual((".bashrc", ""), UniqueNames.SplitExtension(".bashrc"));
        Assert.AreEqual(".bashrc (1)", UniqueNames.Candidates(".bashrc").First());
    }

    [TestMethod]
    public void ANameWithoutAnyDotHasNoExtension() =>
        Assert.AreEqual(("Makefile", ""), UniqueNames.SplitExtension("Makefile"));

    /// <summary>尾点归给扩展名。</summary>
    /// <remarks>
    /// 与 <c>Path.GetExtension</c> 的结果不同(它会返回空串),采的是原先远端那一侧的口径:
    /// Windows 本身会把尾点吃掉,这种名字只可能来自远端,按远端口径处理更一致。
    /// 这一条写下来是为了让那个分歧是**被选择的**,而不是下次谁改到这里时的意外。
    /// </remarks>
    [TestMethod]
    public void ATrailingDotGoesToTheExtension() =>
        Assert.AreEqual(("file", "."), UniqueNames.SplitExtension("file."));

    [TestMethod]
    public void CandidatesCountUpFromOne()
    {
        string[] first = [.. UniqueNames.Candidates("file.txt").Take(3)];

        Assert.AreSequenceEqual(new[] { "file (1).txt", "file (2).txt", "file (3).txt" }, first);
    }

    [TestMethod]
    public void CandidatesAreBounded()
    {
        // 一万个同名文件时继续找下去只是把界面卡住;那种目录本身已经出了别的问题。
        Assert.AreEqual(UniqueNames.MaxAttempts - 1, UniqueNames.Candidates("f").Count());
    }

    [TestMethod]
    public void CandidatesAreLazy()
    {
        // 调用方通常第一个就用上了;真去枚举一万个候选再挑,等于每次冲突都白算一万次。
        Assert.AreEqual("f (1)", UniqueNames.Candidates("f").First());
    }
}
