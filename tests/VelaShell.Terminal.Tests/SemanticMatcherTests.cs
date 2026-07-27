using VelaShell.Terminal.Semantics;

namespace VelaShell.Terminal.Tests;

[TestClass]
[TestCategory("Semantics")]
public class SemanticMatcherTests
{
    private readonly SemanticMatcher _matcher = new();

    [TestMethod]
    public void Match_FindsUrl_WithCorrectOffsets()
    {
        const string line = "see https://example.com/path for details";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan url = spans.Single(s => s.Kind == SemanticKind.Url);
        Assert.AreEqual("https://example.com/path", line.Substring(url.Start, url.Length));
    }

    [TestMethod]
    public void Match_FindsErrorKeyword()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("fatal: connection failed");
        Assert.Contains(s => s.Kind == SemanticKind.Error, spans);
    }

    [TestMethod]
    public void Match_FindsWarning()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("WARNING: disk almost full");
        Assert.Contains(s => s.Kind == SemanticKind.Warning, spans);
    }

    [TestMethod]
    public void Match_FindsIpAddress()
    {
        const string line = "listening on 192.168.1.10:22";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan ip = spans.Single(s => s.Kind == SemanticKind.IpAddress);
        Assert.AreEqual("192.168.1.10", line.Substring(ip.Start, ip.Length));
    }

    [TestMethod]
    public void Match_UrlContainingIp_KeepsOnlyUrl()
    {
        // The IP inside the URL must not produce a separate overlapping span.
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("open http://192.168.0.1/admin now");
        Assert.HasCount(1, spans);
        Assert.AreEqual(SemanticKind.Url, spans[0].Kind);
    }

    [TestMethod]
    public void Match_ReturnsSpansOrderedByStart()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("error at https://a.co after warning");
        var starts = spans.Select(s => s.Start).ToList();
        var sorted = starts.OrderBy(x => x).ToList();
        Assert.AreSequenceEqual(sorted, starts);
    }

    [TestMethod]
    public void Match_FindsSuccessKeyword()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("cockpit.service is now active and running");
        Assert.Contains(s => s.Kind == SemanticKind.Success, spans);
    }

    [TestMethod]
    public void Match_YesIsSuccess_NoIsError()
    {
        // sshd -T / systemctl show 这类开关表:yes 绿、no 红,一眼看出哪项没开。
        const string line = "PermitRootLogin no PasswordAuthentication yes";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan error = spans.Single(s => s.Kind == SemanticKind.Error);
        SemanticSpan success = spans.Single(s => s.Kind == SemanticKind.Success);
        Assert.AreEqual("no", line.Substring(error.Start, error.Length));
        Assert.AreEqual("yes", line.Substring(success.Start, success.Length));
    }

    [TestMethod]
    public void Match_NoAndYes_DoNotFireInsideWords()
    {
        // node / nobody / eyes 里的 no、yes 不能被着色。
        Assert.IsEmpty(SemanticMatcher.Match("node nobody eyes")
                                      .Where(s => s.Kind is SemanticKind.Error or SemanticKind.Success));
    }

    [TestMethod]
    public void Match_FindsOptionFlag()
    {
        const string line = "systemctl enable --now cockpit.socket";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan option = spans.Single(s => s.Kind == SemanticKind.Option);
        Assert.AreEqual("--now", line.Substring(option.Start, option.Length));
    }

    [TestMethod]
    public void Match_OptionDoesNotFireInsideHyphenatedWord()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("a well-known re-run host");
        Assert.DoesNotContain(s => s.Kind == SemanticKind.Option, spans);
    }

    [TestMethod]
    public void Match_FindsStandaloneNumber()
    {
        const string line = "There were 38 failed login attempts";
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match(line);
        SemanticSpan number = spans.Single(s => s.Kind == SemanticKind.Number);
        Assert.AreEqual("38", line.Substring(number.Start, number.Length));
    }

    [TestMethod]
    public void Match_IpAddress_IsNotFragmentedIntoNumbers()
    {
        IReadOnlyList<SemanticSpan> spans = SemanticMatcher.Match("login from 10.10.10.1 on ssh");
        Assert.Contains(s => s.Kind == SemanticKind.IpAddress, spans);
        // The dotted quad must stay a single IP span, not split into number spans.
        Assert.DoesNotContain(s => s.Kind == SemanticKind.Number, spans);
    }

    [TestMethod]
    public void Match_EmptyLine_ReturnsNothing()
    {
        Assert.IsEmpty(SemanticMatcher.Match(""));
        Assert.IsEmpty(SemanticMatcher.Match(null));
    }

    [TestMethod]
    public void UrlAt_ReturnsUrlWhenOffsetInside_ElseNull()
    {
        const string line = "go to https://example.com ok";
        int inside = line.IndexOf("example", StringComparison.Ordinal);
        Assert.AreEqual("https://example.com", SemanticMatcher.UrlAt(line, inside));
        Assert.IsNull(SemanticMatcher.UrlAt(line, 0)); // "go" is not a URL
    }
}
