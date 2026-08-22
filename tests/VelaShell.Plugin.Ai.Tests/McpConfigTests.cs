using VelaShell.Plugin.Ai.Agent;
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
            FilesystemServerArgs, [.. McpConfigParser.SplitArguments("-y  @modelcontextprotocol/server-filesystem   C:\\data")]);
    }

    [TestMethod]
    public void SplitArguments_QuotesPreserveSpaces_AndDoubledQuoteEscapes()
    {
        Assert.AreSequenceEqual(
            QuotedArgs, [.. McpConfigParser.SplitArguments("--root \"C:\\My Files\\docs\" \"a\"\"b\"")]);
    }

    [TestMethod]
    public void SplitArguments_EmptyQuotes_YieldEmptyToken()
    {
        Assert.AreSequenceEqual(SingleEmptyToken, [.. McpConfigParser.SplitArguments("\"\"")]);
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
    public void HttpEndpoint_RequiresHttps_ExceptForLoopbackDevelopment()
    {
        Assert.AreEqual("https://example.com/mcp", McpManager.ValidateHttpEndpoint("https://example.com/mcp").ToString());
        Assert.AreEqual("http://localhost:3000/mcp", McpManager.ValidateHttpEndpoint("http://localhost:3000/mcp").ToString());
        Assert.AreEqual("http://127.0.0.1:3000/mcp", McpManager.ValidateHttpEndpoint("http://127.0.0.1:3000/mcp").ToString());

        InvalidOperationException plainHttp = Assert.ThrowsExactly<InvalidOperationException>(
            () => McpManager.ValidateHttpEndpoint("http://example.com/mcp"));
        Assert.Contains("HTTPS", plainHttp.Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => McpManager.ValidateHttpEndpoint("file:///tmp/mcp"));
        Assert.ThrowsExactly<InvalidOperationException>(() => McpManager.ValidateHttpEndpoint("not a url"));
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

    /// <summary>
    /// 工作目录:空 = ~/.velashell/mcp(与日志同一棵树);~ 前缀按主目录展开(Process.Start 不认 ~,
    /// 原样传下去就是 "目录名称无效");相对路径挂在默认目录下;绝对路径原样。
    /// </summary>
    [TestMethod]
    public void McpWorkspace_Resolve_DefaultsToDotVelashellMcp_AndExpandsTilde()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dot = Path.Combine(home, ".velashell", "mcp");

        Assert.AreEqual(dot, McpWorkspace.Resolve(null));
        Assert.AreEqual(dot, McpWorkspace.Resolve("   "));
        Assert.AreEqual(home, McpWorkspace.Resolve("~"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(home, "work", "mcp")), McpWorkspace.Resolve("~/work/mcp"));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(dot, "xmind")), McpWorkspace.Resolve("xmind"), "相对路径挂在默认目录下");
        string absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcp-abs"));
        Assert.AreEqual(absolute, McpWorkspace.Resolve(absolute));
    }
}
