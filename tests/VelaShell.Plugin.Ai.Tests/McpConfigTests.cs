using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>MCP 配置:参数/环境变量/请求头解析、工具名前缀清洗与设置往返。</summary>
[TestClass]
public sealed class McpConfigTests
{
    private static readonly string[] FilesystemServerArgs = ["-y", "@modelcontextprotocol/server-filesystem", "C:\\data"];
    private static readonly string[] QuotedArgs = ["--root", "C:\\My Files\\docs", "a\"b"];
    private static readonly string[] SingleEmptyToken = [""];

    [TestMethod]
    public void SplitArguments_SplitsOnWhitespace()
    {
        Assert.AreSequenceEqual(
            FilesystemServerArgs, McpConfigParser.SplitArguments("-y  @modelcontextprotocol/server-filesystem   C:\\data").ToArray());
    }

    [TestMethod]
    public void SplitArguments_QuotesPreserveSpaces_AndDoubledQuoteEscapes()
    {
        Assert.AreSequenceEqual(
            QuotedArgs, McpConfigParser.SplitArguments("--root \"C:\\My Files\\docs\" \"a\"\"b\"").ToArray());
    }

    [TestMethod]
    public void SplitArguments_EmptyQuotes_YieldEmptyToken()
    {
        Assert.AreSequenceEqual(SingleEmptyToken, McpConfigParser.SplitArguments("\"\"").ToArray());
        Assert.IsEmpty(McpConfigParser.SplitArguments("   "));
        Assert.IsEmpty(McpConfigParser.SplitArguments(null));
    }

    [TestMethod]
    public void ParseEnvironmentLines_IgnoresBlanksAndInvalid_AllowsEqualsInValue()
    {
        Dictionary<string, string?> env = McpConfigParser.ParseEnvironmentLines(
            "API_KEY=abc\r\n\r\nnot-a-pair\r\nCONN=a=b=c\r\n =missing-key");

        Assert.HasCount(2, env);
        Assert.AreEqual("abc", env["API_KEY"]);
        Assert.AreEqual("a=b=c", env["CONN"]);
    }

    [TestMethod]
    public void ParseHeaderLines_SplitsOnFirstColon()
    {
        Dictionary<string, string> headers = McpConfigParser.ParseHeaderLines(
            "Authorization: Bearer x:y\r\nplain line\r\nX-Api-Key:  k1 ");

        Assert.HasCount(2, headers);
        Assert.AreEqual("Bearer x:y", headers["Authorization"]);
        Assert.AreEqual("k1", headers["X-Api-Key"]);
    }

    [TestMethod]
    public void SanitizeToolPrefix_CollapsesIllegalChars_TruncatesAndFallsBack()
    {
        Assert.AreEqual("my-server_1", McpConfigParser.SanitizeToolPrefix("my-server 1"));
        Assert.AreEqual("a_b", McpConfigParser.SanitizeToolPrefix("a!!@@##b"));
        Assert.AreEqual("mcp", McpConfigParser.SanitizeToolPrefix("  "));
        Assert.AreEqual("mcp", McpConfigParser.SanitizeToolPrefix("!!!"));
        Assert.AreEqual(24, McpConfigParser.SanitizeToolPrefix(new string('x', 60)).Length);
    }

    [TestMethod]
    public async Task McpServers_RoundTripThroughSettingsStore()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var settings = new AiSettings
        {
            McpServers =
            [
                new McpServerConfig
                {
                    Name = "files",
                    Transport = McpTransportType.Stdio,
                    Command = "npx",
                    Arguments = "-y @modelcontextprotocol/server-filesystem C:\\data",
                    EnvironmentVariables = "DEBUG=1"
                },
                new McpServerConfig
                {
                    Name = "remote",
                    Enabled = false,
                    Transport = McpTransportType.Http,
                    Url = "https://example.com/mcp",
                    Headers = "Authorization: Bearer token"
                }
            ]
        };

        await store.SaveAsync(settings);
        AiSettings loaded = await store.LoadAsync();

        Assert.HasCount(2, loaded.McpServers);
        Assert.AreEqual("files", loaded.McpServers[0].Name);
        Assert.AreEqual(McpTransportType.Stdio, loaded.McpServers[0].Transport);
        Assert.AreEqual("npx", loaded.McpServers[0].Command);
        Assert.IsTrue(loaded.McpServers[0].Enabled);
        Assert.AreEqual(McpTransportType.Http, loaded.McpServers[1].Transport);
        Assert.IsFalse(loaded.McpServers[1].Enabled);
        Assert.AreEqual("https://example.com/mcp", loaded.McpServers[1].Url);
    }
}
