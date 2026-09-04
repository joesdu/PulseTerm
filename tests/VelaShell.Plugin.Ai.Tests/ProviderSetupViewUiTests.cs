using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 「连接供应商」那一页的 headless 装载。
/// </summary>
/// <remarks>
/// 这一页的交互约定就两条,两条都在这儿钉着:
/// <list type="number">
/// <item><b>点行只展开,绝不自动干事</b> —— 尤其不能一点就把浏览器弹出去。</item>
/// <item><b>展开后只问程序确实不知道的那几样</b> —— 其余收进「高级设置」。</item>
/// </list>
/// 整页是代码搭的,没有 XAML 名字域,所以按控件的 <c>Name</c> 走逻辑树找。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ProviderSetupViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ProviderSetupViewUiTests).Assembly);

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

    private static T Find<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    private static T? FindOrNull<T>(Control root, string name) where T : Control
        => root.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>展开区里此刻<b>真正露在外面</b>的输入框(收在「高级设置」里的不算)。</summary>
    private static List<string> VisibleBoxes(Control root)
        => [.. root.GetLogicalDescendants().OfType<TextBox>()
                   .Where(box => box.Name is not null && IsShown(box))
                   .Select(box => box.Name!)];

    private static bool IsShown(Control control)
    {
        for (StyledElement? node = control; node is not null; node = node.Parent)
        {
            if (node is Control visual && !visual.IsVisible)
            {
                return false;
            }
        }
        return true;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>照用户的操作展开一行:真点在行上(命中测试、冒泡都真跑)。</summary>
    private static async Task ClickRowAsync(Window window, ProviderSetupView view, string entryId)
    {
        Border card = Find<Border>(view, $"SetupRow.{entryId}");
        // 先滚到可见区:目录一长,靠后的行落在窗口外面,坐标算出来是负的,点了什么也不会发生
        card.BringIntoView();
        await PumpAsync(10);
        // 点行的左上角一带:那儿是字母牌/标题,肯定落在行内
        Point spot = card.TranslatePoint(new Point(20, 18), window)!.Value;
        Assert.IsTrue(spot.Y > 0 && spot.Y < window.Height,
            $"{entryId} 那一行没滚进可见区(y={spot.Y}),这一点会落空");
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
        await PumpAsync(20);
    }

    private static async Task<(Window Window, ProviderSetupView View, AiSettings Settings, AiSettingsStore Store)>
        ShowAsync(TestPluginContext context, AiSettings? seed = null, string? focus = null)
    {
        AiSettings settings = seed ?? new AiSettings();
        var store = new AiSettingsStore(context);
        await store.SaveAsync(settings);
        var view = new ProviderSetupView(context, store, settings, new Loc("en"),
            () => store.SaveAsync(settings), focus);
        var window = new Window { Width = 720, Height = 720, Content = view };
        window.Show();
        await PumpAsync(40);
        return (window, view, settings, store);
    }

    // ---- 交互约定 ----

    [TestMethod]
    public void RowsCarryNoActionButtonAndStartCollapsed()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                foreach (ProviderCatalogEntry entry in ProviderCatalog.All)
                {
                    Assert.IsNotNull(FindOrNull<Border>(view, $"SetupRow.{entry.Id}"), entry.Id);
                    // 行上不摆按钮:登录/添加一律在展开区里点
                    Assert.IsNull(FindOrNull<Button>(view, $"SetupAction.{entry.Id}"), entry.Id);
                }
                Assert.IsEmpty(VisibleBoxes(view), "一上来全是收起的");
                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ClickingARow_OnlyExpandsIt_AndNeverOpensTheBrowser()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // OpenRouter 参数最齐(那一路连 client_id 都不需要)—— 最容易被"自动开浏览器"误伤的一条
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "openrouter");

                // 展开了:登录按钮出现
                Assert.AreEqual("Sign in", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                // 但什么都没发生 —— 用户还没决定要不要登,浏览器不该已经弹出去了
                Assert.AreEqual("", Find<TextBlock>(view, "SetupProgressText").Text ?? "",
                    "点行只该展开;弹浏览器要等用户点「登录」");
                Assert.IsFalse(Find<Button>(view, "SetupSecondaryButton").IsVisible,
                    "没在登录,就不该有「取消」");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void ClickingTheSameRowTwice_CollapsesIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "deepseek");
                Assert.IsNotNull(FindOrNull<Button>(view, "SetupPrimaryButton"));

                await ClickRowAsync(window, view, "deepseek");

                Assert.IsNull(FindOrNull<Button>(view, "SetupPrimaryButton"), "再点一次该收起来");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void TheSignInButton_IsWhatOpensTheBrowser()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "openrouter");
                // 参数齐全的那一家,展开区里一个输入框都不该有
                Assert.IsEmpty(VisibleBoxes(view));

                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(40);

                Assert.AreEqual("Browser opened — finish the sign-in there.",
                    Find<TextBlock>(view, "SetupProgressText").Text);
                Assert.AreEqual("Cancel", (string?)Find<Button>(view, "SetupSecondaryButton").Content);

                view.CancelPendingLogin();
                await PumpAsync();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 登录还挂着时重进这一行必须什么都不做:重建会把自己刚起的那次掐掉,
    /// 而用户那边浏览器已经开着了 —— 回调就落到一个没人等的端口上
    /// (真机验收踩过,表现是"浏览器显示登录成功、程序毫无反应")。
    /// </summary>
    [TestMethod]
    public void ActivatingARowAgainWhileSigningIn_DoesNotRestartTheLogin()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context);
            try
            {
                await ClickRowAsync(window, view, "openrouter");
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(40);
                TextBlock progress = Find<TextBlock>(view, "SetupProgressText");

                await ClickRowAsync(window, view, "openrouter");

                // 这一行只要被重建,SetupProgressText 就是一个新的空白实例 —— 那就是"又开了一次"的指纹
                Assert.AreSame(progress, Find<TextBlock>(view, "SetupProgressText"));
                Assert.AreEqual("Browser opened — finish the sign-in there.", progress.Text);

                view.CancelPendingLogin();
                await PumpAsync();
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 只问缺的那几样 ----

    [TestMethod]
    public void AnApiKeyProvider_AsksForTheKeyAndNothingElse()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, focus: "deepseek");
            try
            {
                Assert.AreSequenceEqual(["SetupKeyBox"], VisibleBoxes(view));

                Find<TextBox>(view, "SetupKeyBox").Text = "sk-deepseek";
                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(60);

                Assert.HasCount(1, settings.Providers);
                AiProvider added = settings.Providers[0];
                Assert.AreEqual("deepseek", added.CatalogId);
                Assert.AreEqual("https://api.deepseek.com/v1", added.BaseUrl, "地址取自目录,不用人填");
                Assert.AreEqual(added.Models[0].Id, settings.ActiveModelId);
                Assert.AreEqual("sk-deepseek", await store.GetApiKeyAsync(added.Id));
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.deepseek").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void Advanced_StaysFoldedUntilAsked()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "anthropic");
            try
            {
                Assert.AreSequenceEqual(["SetupKeyBox"], VisibleBoxes(view));

                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                List<string> boxes = VisibleBoxes(view);
                Assert.Contains("SetupNameBox", boxes);
                Assert.Contains("SetupModelBox", boxes);
                Assert.Contains("SetupBaseUrlBox", boxes);
                Assert.AreEqual("https://api.anthropic.com", Find<TextBox>(view, "SetupBaseUrlBox").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void APendingRegistration_AsksForTheClientIdOnceAndPointsAtWhereToGetIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, AiSettings settings, _) =
                await ShowAsync(context, focus: "huggingface");
            try
            {
                Assert.AreEqual("", ProviderCatalog.Find("huggingface")!.CreateProvider().OAuth!.ClientId,
                    "这条用例的前提是客户端 id 还空着;填上之后请把它挪到一键登录那条用例");
                Assert.AreSequenceEqual(["SetupClientIdBox"], VisibleBoxes(view));
                Assert.Contains(
                    b => (string?)b.Content == "Open the registration page", view.GetLogicalDescendants().OfType<Button>(),
                    "空着的客户端 id 旁边必须有去注册的入口");

                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(40);
                Assert.IsEmpty(settings.Providers);
                Assert.AreEqual("Fill in the client ID and the endpoints first.",
                    Find<TextBlock>(view, "SetupProgressText").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AProviderWithNoKnownEndpoint_AsksForItAndNeverPrefillsThePlaceholder()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "azure-openai");
            try
            {
                List<string> boxes = VisibleBoxes(view);
                Assert.Contains("SetupClientIdBox", boxes);
                Assert.Contains("SetupBaseUrlBox", boxes);
                // 占位符不能当默认值端上来 —— 用户十有八九连尖括号一起提交
                Assert.AreEqual("", Find<TextBox>(view, "SetupBaseUrlBox").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void LocalProviders_NeedNoKeyAndSaySo()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, AiSettings settings, _) = await ShowAsync(context, focus: "ollama");
            try
            {
                Assert.IsEmpty(VisibleBoxes(view), "本地服务不需要鉴权,一个框都不该问");

                Click(Find<Button>(view, "SetupPrimaryButton"));
                await PumpAsync(60);

                Assert.HasCount(1, settings.Providers);
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.ollama").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 拉回来的模型 ----

    [TestMethod]
    public void PulledModels_ShowUpAsAPickerInAdvanced()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider groq = ProviderCatalog.Find("groq")!.CreateProvider();
            groq.AvailableModels = ["llama-3.3-70b-versatile", "mixtral-8x7b"];
            var seed = new AiSettings { Providers = [groq] };
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, seed, focus: "groq");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                ComboBox picker = Find<ComboBox>(view, "SetupModelPicker");
                Assert.AreSequenceEqual(groq.AvailableModels, (System.Collections.ICollection)picker.ItemsSource!);
                // 出厂示例正好在列表里,应当已经选中
                Assert.AreEqual("llama-3.3-70b-versatile", picker.SelectedItem);

                picker.SelectedItem = "mixtral-8x7b";
                await PumpAsync();
                Assert.AreEqual("mixtral-8x7b", Find<TextBox>(view, "SetupModelBox").Text,
                    "从下拉里挑一个就该填进模型框");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void WithoutAPulledList_ThereIsNoEmptyPicker()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ProviderSetupView view, _, _) = await ShowAsync(context, focus: "groq");
            try
            {
                Find<ToggleButton>(view, "SetupAdvancedToggle").IsChecked = true;
                await PumpAsync();

                Assert.IsNull(FindOrNull<ComboBox>(view, "SetupModelPicker"), "没拉到过就别摆一个空下拉");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ---- 已连接 / 移除 ----

    [TestMethod]
    public void ASignedInSubscription_ShowsConnectedAndOffersSigningOut()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider openrouter = ProviderCatalog.Find("openrouter")!.CreateProvider();
            var seed = new AiSettings { Providers = [openrouter] };
            var store = new AiSettingsStore(context);
            await store.SaveTokensAsync(openrouter.Id,
                new OAuthTokens { AccessToken = "sk-or-v1-abc", Account = "ops@example.com" });
            var view = new ProviderSetupView(context, store, seed, new Loc("en"), () => store.SaveAsync(seed));
            var window = new Window { Width = 720, Height = 720, Content = view };
            window.Show();
            await PumpAsync(40);
            try
            {
                Assert.AreEqual("Connected", Find<TextBlock>(view, "SetupPill.openrouter").Text);

                view.FocusEntry("openrouter");
                await PumpAsync();
                Assert.IsEmpty(VisibleBoxes(view), "已连接的那一条进去也不该有表单");
                Assert.AreEqual("Sign in again", (string?)Find<Button>(view, "SetupPrimaryButton").Content);
                Button signOut = Find<Button>(view, "SetupSecondaryButton");
                Assert.IsTrue(signOut.IsVisible);
                Assert.AreEqual("Sign out", (string?)signOut.Content);

                Click(signOut);
                await PumpAsync(40);

                Assert.IsNull(await store.GetTokensAsync(openrouter.Id));
                Assert.AreEqual("Not connected", Find<TextBlock>(view, "SetupPill.openrouter").Text);
                Assert.IsNotEmpty(seed.Providers, "退出登录不删供应商,只清凭据");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void RemovingAnApiKeyProvider_TakesItsSecretsWithIt()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider groq = ProviderCatalog.Find("groq")!.CreateProvider();
            var seed = new AiSettings { Providers = [groq], ActiveModelId = groq.Models[0].Id };
            (Window window, ProviderSetupView view, AiSettings settings, AiSettingsStore store) =
                await ShowAsync(context, seed);
            try
            {
                await store.SetApiKeyAsync(groq.Id, "gsk-1");
                await view.RefreshStatusAsync();
                await PumpAsync();
                Assert.AreEqual("Ready", Find<TextBlock>(view, "SetupPill.groq").Text);

                view.FocusEntry("groq");
                await PumpAsync();
                Button remove = Find<Button>(view, "SetupSecondaryButton");
                Assert.AreEqual("Remove", (string?)remove.Content);
                Click(remove);
                await PumpAsync(40);

                Assert.IsEmpty(settings.Providers);
                Assert.IsNull(await store.GetApiKeyAsync(groq.Id), "移除要连机密一起清,别留孤儿凭据");
                Assert.IsNull(settings.ActiveModelId);
                Assert.AreEqual("Not added", Find<TextBlock>(view, "SetupPill.groq").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
