using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 设置页(供应商 › 模型两层)的 headless 装载:左栏行序、选中层切换右侧表单、
/// 新增供应商 / 模型与保存的落盘结果。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SettingsViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsViewUiTests).Assembly);

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 20)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(Window Window, SettingsView View, AiSettings Settings, AiSettingsStore Store)> ShowAsync(
        TestPluginContext context, AiSettings settings)
    {
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var view = new SettingsView(context, store, settings, new Loc("en"), () => { });
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        await PumpAsync();
        return (window, view, settings, store);
    }

    private static AiSettings TwoProviders()
    {
        var routin = new AiProvider
        {
            Name = "Routin",
            BaseUrl = "https://routin.example/v1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models =
            [
                new AiModelConfig { Model = "gpt-5" },
                new AiModelConfig { Name = "Claude", Model = "claude-opus-5", Protocol = ChatProtocol.AnthropicMessages, HasOwnApiKey = true }
            ]
        };
        var ollama = new AiProvider { Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Models = [new AiModelConfig { Model = "llama3.1" }] };
        return new AiSettings { Providers = [routin, ollama], ActiveModelId = routin.Models[1].Id };
    }

    [TestMethod]
    public void List_ShowsProvidersWithIndentedModels_AndSelectsActiveModel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, TwoProviders());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                var rows = (List<ProviderNavItem>)list.ItemsSource!;
                Assert.HasCount(5, rows, "2 个供应商 + 3 个模型");
                Assert.IsTrue(rows[0].IsProvider);
                Assert.AreEqual("Routin", rows[0].Text);
                Assert.IsFalse(rows[1].IsProvider);
                Assert.AreEqual("gpt-5", rows[1].Text, "没填名称的模型显示模型 id");
                Assert.AreEqual("Claude", rows[2].Text);
                Assert.IsGreaterThan(rows[0].Indent.Left, rows[1].Indent.Left, "模型行缩进");
                Assert.IsTrue(rows[3].IsProvider);
                Assert.AreEqual("Ollama", rows[3].Text);
                // 起手选中的是当前活跃模型,右侧是模型表单
                Assert.AreEqual(2, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.AreEqual("claude-opus-5", view.GetControl<TextBox>("ModelBox").Text);
                // 协议下拉:0 = 继承,Anthropic 覆盖 = 枚举值 + 1
                Assert.AreEqual((int)ChatProtocol.AnthropicMessages + 1, view.GetControl<ComboBox>("ProtocolCombo").SelectedIndex);
                Assert.IsTrue(view.GetControl<CheckBox>("OwnKeyCheck").IsChecked);
                Assert.IsTrue(view.GetControl<StackPanel>("OwnKeyPanel").IsVisible);
                Assert.IsTrue(view.GetControl<StackPanel>("PromptCachePanel").IsVisible, "解出的协议是 Anthropic,提示词缓存要露出来");

                // 切到供应商行:表单换成供应商那套
                list.SelectedIndex = 0;
                await PumpAsync();
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                Assert.AreEqual("Routin", view.GetControl<TextBox>("ProviderNameBox").Text);
                Assert.AreEqual("https://routin.example/v1", view.GetControl<TextBox>("ProviderBaseUrlBox").Text);
                Assert.AreEqual((int)ChatProtocol.OpenAiChatCompletions, view.GetControl<ComboBox>("ProviderProtocolCombo").SelectedIndex);

                // 切到继承协议的模型:下拉在第 0 项,且缓存开关随供应商默认协议(OpenAI)隐藏
                list.SelectedIndex = 1;
                await PumpAsync();
                Assert.AreEqual(0, view.GetControl<ComboBox>("ProtocolCombo").SelectedIndex);
                Assert.IsFalse(view.GetControl<StackPanel>("OwnKeyPanel").IsVisible);
                Assert.IsFalse(view.GetControl<StackPanel>("PromptCachePanel").IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AddProviderAndModel_ThenSave_PersistsTwoLayerShape()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, SettingsView view, AiSettings settings, AiSettingsStore store) = await ShowAsync(context, new AiSettings());
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                Assert.IsFalse(view.GetControl<Button>("AddModelButton").IsEnabled, "没选中供应商时不能加模型");

                // 「新增供应商」现在开的是「连接供应商」那一页,供应商由它加完再回调进来
                string? requested = "unset";
                view.ProviderCatalogRequested += id => requested = id;
                view.GetControl<Button>("AddButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.IsNull(requested, "点「新增供应商」是开目录页,不带具体条目");

                AiProvider openai = ProviderCatalog.Find("openai")!.CreateProvider();
                settings.Providers.Add(openai);
                settings.ActiveModelId ??= openai.Models[0].Id;
                view.ReloadFromCatalog(openai.Id);
                await PumpAsync();
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("OpenAI", settings.Providers[0].Name);
                Assert.HasCount(1, settings.Providers[0].Models);
                Assert.AreEqual(settings.Providers[0].Models[0].Id, settings.ActiveModelId, "头一个模型自动成为活跃模型");
                Assert.AreEqual(0, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ProviderEditor").IsVisible);

                // 改地址与 Key,保存
                view.GetControl<TextBox>("ProviderBaseUrlBox").Text = "https://routin.example/v1";
                view.GetControl<TextBox>("ProviderApiKeyBox").Text = "sk-shared";
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.AreEqual("https://routin.example/v1", settings.Providers[0].BaseUrl);
                Assert.AreEqual("sk-shared", await store.GetApiKeyAsync(settings.Providers[0].Id));

                // 加第二个模型:挂在选中供应商下,并选中它
                view.GetControl<Button>("AddModelButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(2, settings.Providers[0].Models);
                Assert.AreEqual(2, list.SelectedIndex);
                Assert.IsTrue(view.GetControl<StackPanel>("ModelEditor").IsVisible);
                view.GetControl<TextBox>("ModelBox").Text = "claude-opus-5";
                view.GetControl<ComboBox>("ProtocolCombo").SelectedIndex = (int)ChatProtocol.AnthropicMessages + 1;
                view.GetControl<Button>("SaveButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                AiModelConfig added = settings.Providers[0].Models[1];
                Assert.AreEqual("claude-opus-5", added.Model);
                Assert.AreEqual(ChatProtocol.AnthropicMessages, added.Protocol);
                Assert.IsFalse(added.HasOwnApiKey);
                // 解析:协议来自模型覆盖,地址与 Key 归属继承供应商
                ResolvedModel resolved = settings.FindModel(added.Id)!;
                Assert.AreEqual("https://routin.example/v1", resolved.BaseUrl);
                Assert.AreEqual(settings.Providers[0].Id, resolved.ApiKeyOwnerId);
                Assert.AreEqual("sk-shared", await store.GetApiKeyAsync(resolved.ApiKeyOwnerId));

                // 落盘的是两层结构
                AiSettings reloaded = await store.LoadAsync();
                Assert.HasCount(1, reloaded.Providers);
                Assert.HasCount(2, reloaded.Providers[0].Models);
                Assert.AreEqual(ChatProtocol.AnthropicMessages, reloaded.Providers[0].Models[1].Protocol);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void DeleteProvider_NeedsSecondClick_AndRemovesModelsWithSecrets()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiSettings seed = TwoProviders();
            string routinId = seed.Providers[0].Id;
            string claudeId = seed.Providers[0].Models[1].Id;
            await context.Secrets.SetAsync($"apikey:{routinId}", "sk-routin");
            await context.Secrets.SetAsync($"apikey:{claudeId}", "sk-claude");
            (Window window, SettingsView view, AiSettings settings, _) = await ShowAsync(context, seed);
            try
            {
                ListBox list = view.GetControl<ListBox>("ProvidersList");
                list.SelectedIndex = 0; // Routin
                await PumpAsync();
                Button delete = view.GetControl<Button>("DeleteButton");

                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(2, settings.Providers, "第一击只是提示,不删");
                Assert.Contains("2", view.GetControl<TextBlock>("StatusText").Text ?? "", "提示里带着将被连带删除的模型数");

                delete.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("Ollama", settings.Providers[0].Name);
                Assert.AreEqual(settings.Providers[0].Models[0].Id, settings.ActiveModelId, "活跃模型随供应商没了,落到剩下的第一个");
                Assert.IsNull(await context.Secrets.GetAsync($"apikey:{routinId}"));
                Assert.IsNull(await context.Secrets.GetAsync($"apikey:{claudeId}"), "模型的独立 Key 也要清");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
