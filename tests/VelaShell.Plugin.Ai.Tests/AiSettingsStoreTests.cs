using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>设置存储:配置往返、机密隔离与三种协议的客户端构造。</summary>
[TestClass]
public sealed class AiSettingsStoreTests
{
    [TestMethod]
    public async Task Load_WithoutSavedSettings_ReturnsDefaults()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        AiSettings settings = await store.LoadAsync();

        Assert.IsEmpty(settings.Providers);
        Assert.IsFalse(settings.AgentMode);
        Assert.IsFalse(settings.AutoApproveCommands);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsProviders()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var settings = new AiSettings
        {
            Providers =
            [
                new AiProviderConfig
                {
                    Name = "Claude",
                    Protocol = ChatProtocol.AnthropicMessages,
                    BaseUrl = "https://api.anthropic.com",
                    Model = "claude-opus-5",
                    MaxTokens = 4096
                }
            ],
            AgentMode = true
        };
        settings.ActiveProviderId = settings.Providers[0].Id;

        await store.SaveAsync(settings);
        AiSettings loaded = await store.LoadAsync();

        Assert.HasCount(1, loaded.Providers);
        Assert.AreEqual("Claude", loaded.Providers[0].Name);
        Assert.AreEqual(ChatProtocol.AnthropicMessages, loaded.Providers[0].Protocol);
        Assert.AreEqual(4096, loaded.Providers[0].MaxTokens);
        Assert.AreEqual(settings.ActiveProviderId, loaded.ActiveProviderId);
        Assert.IsTrue(loaded.AgentMode);
    }

    [TestMethod]
    public async Task ApiKey_SetGetDelete_UsesSecretStore()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);

        await store.SetApiKeyAsync("p1", "sk-secret");
        Assert.AreEqual("sk-secret", await store.GetApiKeyAsync("p1"));

        // 空值 = 清除
        await store.SetApiKeyAsync("p1", "");
        Assert.IsNull(await store.GetApiKeyAsync("p1"));
    }

    [TestMethod]
    public async Task CreateClient_EachProtocol_BuildsChatClient()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        foreach (ChatProtocol protocol in Enum.GetValues<ChatProtocol>())
        {
            var provider = new AiProviderConfig
            {
                Name = "t",
                Protocol = protocol,
                BaseUrl = protocol == ChatProtocol.AnthropicMessages ? "https://example.com/v1" : "https://example.com/v1",
                Model = "test-model"
            };

            IChatClient client = await store.CreateClientAsync(provider, "sk-test");

            Assert.IsNotNull(client, protocol.ToString());
        }
    }
}
