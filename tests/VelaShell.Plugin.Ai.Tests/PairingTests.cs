using VelaShell.Plugin.Ai.Bridge;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 配对码本身:一次性、会过期、猜错有上限。
/// </summary>
/// <remarks>
/// 这个码在有效期内能把一个陌生聊天放进白名单,所以它的三条边界都是安全边界,
/// 不是"顺手加的便利功能"。
/// </remarks>
[TestClass]
public sealed class PairingServiceTests
{
    [TestMethod]
    public void Issue_ProducesASixDigitCode()
    {
        string code = new PairingService().Issue();

        Assert.AreEqual(6, code.Length);
        Assert.IsTrue(code.All(char.IsAsciiDigit), $"expected six digits, got '{code}'");
    }

    [TestMethod]
    public void Redeem_AcceptsTheCodeExactlyOnce()
    {
        var pairing = new PairingService();
        string code = pairing.Issue();

        Assert.IsTrue(pairing.TryRedeem(code, out _));
        Assert.IsFalse(pairing.TryRedeem(code, out _), "a pairing code must not be reusable");
        Assert.IsNull(pairing.Code);
    }

    [TestMethod]
    public void Redeem_RejectsAWrongCode()
    {
        var pairing = new PairingService();
        string code = pairing.Issue();
        string wrong = code == "000000" ? "111111" : "000000";

        Assert.IsFalse(pairing.TryRedeem(wrong, out _));
        Assert.IsTrue(pairing.TryRedeem(code, out _), "one wrong guess should not kill a valid code");
    }

    /// <summary>猜错够多次就作废 —— 六位数字挡得住随手试,挡不住脚本。</summary>
    [TestMethod]
    public void Redeem_KillsTheCodeAfterTooManyWrongGuesses()
    {
        var pairing = new PairingService();
        string code = pairing.Issue();
        string wrong = code == "000000" ? "111111" : "000000";

        for (int i = 0; i < 5; i++)
        {
            Assert.IsFalse(pairing.TryRedeem(wrong, out _));
        }

        Assert.IsNull(pairing.Code, "the code should be dead after five wrong guesses");
        Assert.IsFalse(pairing.TryRedeem(code, out _), "even the right code must not work once it has been burnt");
    }

    [TestMethod]
    public void Redeem_FailsWhenNoCodeWasIssued()
        => Assert.IsFalse(new PairingService().TryRedeem("123456", out _));

    [TestMethod]
    public void Issue_InvalidatesThePreviousCode()
    {
        var pairing = new PairingService();
        string first = pairing.Issue();
        pairing.Issue();

        Assert.IsFalse(pairing.TryRedeem(first, out _));
    }

    [TestMethod]
    public void Revoke_DropsTheCode()
    {
        var pairing = new PairingService();
        string code = pairing.Issue();

        pairing.Revoke();

        Assert.IsNull(pairing.Code);
        Assert.IsFalse(pairing.TryRedeem(code, out _));
    }

    [TestMethod]
    public void Pending_KeepsTheMostRecentFirstAndDeduplicates()
    {
        var pairing = new PairingService();
        pairing.Remember(new PendingChat("ch1", "a", true, "Ann", DateTimeOffset.UtcNow.AddMinutes(-5)));
        pairing.Remember(new PendingChat("ch1", "b", false, "Bob", DateTimeOffset.UtcNow));
        // 同一个聊天再敲一次:不新增一行,但显示名跟上最新的那个人
        pairing.Remember(new PendingChat("ch1", "a", true, "Cara", DateTimeOffset.UtcNow));

        IReadOnlyList<PendingChat> pending = pairing.Pending();

        Assert.HasCount(2, pending);
        Assert.AreEqual("b", pending[0].ChatId);
        Assert.AreEqual("Cara", pending.Single(p => p.ChatId == "a").UserName);
    }

    [TestMethod]
    public void Forget_RemovesOneChat()
    {
        var pairing = new PairingService();
        pairing.Remember(new PendingChat("ch1", "a", true, "Ann", DateTimeOffset.UtcNow));

        pairing.Forget("ch1", "a");

        Assert.IsEmpty(pairing.Pending());
    }
}
