using System.Text;

namespace VelaShell.Terminal.Tests;

/// <summary>连接初始化命令回显抑制器:整块剥除、跨块切分、双次命中、超窗放行、巧合前缀不误扣。</summary>
[TestClass]
public class EchoSuppressorTests
{
    private static readonly string Payload = "prompt_nl() { local c; ((c>1)) && echo; }; PROMPT_COMMAND=prompt_nl";

    private static byte[] Needle => Encoding.UTF8.GetBytes(Payload + "\r\n");

    private static string Run(EchoSuppressor s, params string[] chunks)
    {
        var sb = new StringBuilder();
        foreach (string chunk in chunks)
        {
            sb.Append(Encoding.UTF8.GetString(s.Process(Encoding.UTF8.GetBytes(chunk))));
        }
        return sb.ToString();
    }

    [TestMethod]
    public void WholeChunk_EchoRemoved_SurroundingsKept()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        string result = Run(s, "banner\r\n " + Payload + "\r\npi@host:~$ ");
        Assert.AreEqual("banner\r\n pi@host:~$ ", result);
    }

    [TestMethod]
    public void TwoOccurrences_BothRemoved()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        string result = Run(s, " " + Payload + "\r\npi@host:~$  " + Payload + "\r\npi@host:~$ ");
        Assert.AreEqual(" pi@host:~$  pi@host:~$ ", result);
    }

    [TestMethod]
    public void SplitAcrossChunks_AtEveryBoundary_EchoStillRemoved()
    {
        string whole = "MOTD\r\n " + Payload + "\r\npi@host:~$ ";
        // 从 needle 中段任意切开(前 4 字节内的切分允许漏抑制,见 MinHold 注释)。
        byte[] bytes = Encoding.UTF8.GetBytes(whole);
        int needleStart = whole.IndexOf(Payload, StringComparison.Ordinal);
        for (int split = needleStart + 4; split < bytes.Length - 1; split++)
        {
            var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
            string part1 = Encoding.UTF8.GetString(s.Process(bytes.AsSpan(0, split).ToArray()));
            string part2 = Encoding.UTF8.GetString(s.Process(bytes.AsSpan(split).ToArray()));
            Assert.AreEqual("MOTD\r\n pi@host:~$ ", part1 + part2, $"split={split}");
        }
    }

    [TestMethod]
    public void HitsExhausted_FurtherIdenticalTextPassesThrough()
    {
        var s = new EchoSuppressor(Needle, 1, TimeSpan.FromSeconds(10));
        string result = Run(s, Payload + "\r\n", Payload + "\r\n");
        Assert.AreEqual(Payload + "\r\n", result);
    }

    [TestMethod]
    public void ExpiredWindow_HeldPrefixReleased()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromMilliseconds(1));
        // 先喂一个会被扣住的前缀,等窗口过期后,后续数据应携带被扣字节原样放行。
        string prefix = Payload[..10];
        byte[] first = s.Process(Encoding.UTF8.GetBytes(prefix));
        Thread.Sleep(30);
        byte[] second = s.Process(Encoding.UTF8.GetBytes("XYZ"));
        Assert.AreEqual(prefix + "XYZ",
            Encoding.UTF8.GetString(first) + Encoding.UTF8.GetString(second));
    }

    [TestMethod]
    public void ShortCoincidentalPrefixAtChunkTail_NotHeldBack()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromSeconds(10));
        // 块尾是 needle 的前 2 字节("pr"),低于 MinHold,应立即放行不扣。
        byte[] result = s.Process(Encoding.UTF8.GetBytes("pi@host:~$ pr"));
        Assert.AreEqual("pi@host:~$ pr", Encoding.UTF8.GetString(result));
    }

    /// <summary>
    /// 宿主一旦发现抑制器失效就会弃用实例(<c>SshTerminalBridge</c>),此时块尾扣住的部分命中
    /// 必须取得回来——否则再没有下一次 Process 放行它们,那几个字节被永久吞掉
    /// (与 #291 的 ZMODEM 扣留同源)。
    /// </summary>
    [TestMethod]
    public void ExpiredWithHeldTail_TakeHeldReturnsBytesInsteadOfLosingThem()
    {
        var s = new EchoSuppressor(Needle, 2, TimeSpan.FromMilliseconds(1));
        string prefix = Payload[..10];
        byte[] first = s.Process(Encoding.UTF8.GetBytes(prefix));

        // 扣住了:本次一个字节都没放出来。
        Assert.IsEmpty(first);
        Thread.Sleep(30);
        Assert.IsTrue(s.Expired);

        // 宿主弃用实例前把扣住的尾巴取回来交还终端;取一次即清空。
        Assert.AreEqual(prefix, Encoding.UTF8.GetString(s.TakeHeld()));
        Assert.IsEmpty(s.TakeHeld());
    }
}
