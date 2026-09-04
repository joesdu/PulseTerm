using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 对外的 MCP 服务端:外部 agent(Claude Code / Codex)真的连得上、看得到工具、调得动。
/// </summary>
/// <remarks>
/// 这几条用例走的是<b>真的 HTTP</b>,不是对着内部方法打桩 —— 这条路上最容易坏的恰恰是
/// 协议层面的细节(鉴权头、会话头、JSON-RPC 信封),打桩全都测不到。
/// </remarks>
[TestClass]
public sealed class McpEndpointTests
{
    /// <summary>找一个空闲端口。写死端口会让并行跑测试的机器互相打架。</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>本机回环不该走代理 —— 宿主给 DefaultProxy 装过东西,测试里绕开它。</summary>
    private static HttpClient CreateClient(int port, string? token)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
        return client;
    }

    private static async Task<(McpEndpoint Endpoint, int Port, string Token)> StartAsync(
        TestPluginContext context, ChatMode mode = ChatMode.Plan)
    {
        var store = new McpServerSettingsStore(context);
        int port = FreePort();
        await store.SaveAsync(new McpServerSettings { Enabled = true, Port = port, Mode = mode });
        string token = await store.TokenAsync();
        var endpoint = new McpEndpoint(context, store);
        await endpoint.ReloadAsync();
        return (endpoint, port, token);
    }

    private static async Task<JsonDocument> RpcAsync(HttpClient client, object request, string? sessionId = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(request)
        };
        if (sessionId is not null)
        {
            message.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }
        using HttpResponseMessage response = await client.SendAsync(message);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Initialize_ReturnsSessionIdAndServerInfo()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            Assert.IsTrue(endpoint.IsRunning);
            using HttpClient client = CreateClient(port, token);

            using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new { protocolVersion = "2025-06-18", clientInfo = new { name = "test", version = "1" } }
                })
            };
            using HttpResponseMessage response = await client.SendAsync(message);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? ids));
            Assert.IsFalse(string.IsNullOrEmpty(ids!.First()));
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement result = document.RootElement.GetProperty("result");
            Assert.AreEqual("velashell", result.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.AreEqual("2025-06-18", result.GetProperty("protocolVersion").GetString());
        }
    }

    /// <summary>没有令牌就进不来。本机端口谁都能敲,这是唯一的门。</summary>
    [TestMethod]
    public async Task Request_WithoutToken_IsRejected()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string _) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token: null);

            using HttpResponseMessage response = await client.PostAsJsonAsync("mcp",
                new { jsonrpc = "2.0", id = 1, method = "initialize" });

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task Request_WithWrongToken_IsRejected()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token + "x");

            using HttpResponseMessage response = await client.PostAsJsonAsync("mcp",
                new { jsonrpc = "2.0", id = 1, method = "initialize" });

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// 计划档只给只读工具 —— 外部 agent 默认不该能在生产机上敲命令。
    /// </summary>
    [TestMethod]
    public async Task ToolsList_InPlanMode_ExposesOnlyReadOnlyTools()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);

            string[] names =
            [
                .. document.RootElement.GetProperty("result").GetProperty("tools")
                           .EnumerateArray().Select(t => t.GetProperty("name").GetString()!)
            ];
            Assert.Contains("list_sessions", names);
            Assert.Contains("use_session", names);
            Assert.DoesNotContain("run_command", names);
            Assert.DoesNotContain("write_remote_file", names);
        }
    }

    [TestMethod]
    public async Task ToolsList_InAgentMode_ExposesWriteTools()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context, ChatMode.Agent);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 2, method = "tools/list" }, sessionId);

            string[] names =
            [
                .. document.RootElement.GetProperty("result").GetProperty("tools")
                           .EnumerateArray().Select(t => t.GetProperty("name").GetString()!)
            ];
            Assert.Contains("run_command", names);
        }
    }

    [TestMethod]
    public async Task ToolsCall_ListSessions_ReturnsTheHostsSessions()
    {
        using var context = new TestPluginContext();
        context.FakeSessions.AddConnected(host: "prod-1", username: "root");
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);
            string sessionId = await InitializeAsync(client);

            using JsonDocument document = await RpcAsync(client, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "list_sessions", arguments = new { } }
            }, sessionId);

            JsonElement result = document.RootElement.GetProperty("result");
            Assert.IsFalse(result.GetProperty("isError").GetBoolean());
            string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("prod-1", text);
        }
    }

    /// <summary>没走 initialize 就调工具,要给一个说得清的错,而不是空指针。</summary>
    [TestMethod]
    public async Task ToolsList_WithoutSession_ReportsAnError()
    {
        using var context = new TestPluginContext();
        (McpEndpoint endpoint, int port, string token) = await StartAsync(context);
        await using (endpoint)
        {
            using HttpClient client = CreateClient(port, token);

            using JsonDocument document = await RpcAsync(client,
                new { jsonrpc = "2.0", id = 9, method = "tools/list" });

            Assert.AreEqual(-32001, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    /// <summary>桥接与服务端都关着时,不该占任何端口。</summary>
    [TestMethod]
    public async Task Disabled_DoesNotListen()
    {
        using var context = new TestPluginContext();
        var store = new McpServerSettingsStore(context);
        await store.SaveAsync(new McpServerSettings { Enabled = false, Port = FreePort() });

        await using var endpoint = new McpEndpoint(context, store);
        await endpoint.ReloadAsync();

        Assert.IsFalse(endpoint.IsRunning);
        Assert.IsNull(endpoint.Url);
    }

    private static async Task<string> InitializeAsync(HttpClient client)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "initialize" })
        };
        using HttpResponseMessage response = await client.SendAsync(message);
        return response.Headers.GetValues("Mcp-Session-Id").First();
    }
}
