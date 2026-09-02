using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 输入框旁边那个思考档位下拉。它的全部意义在"临时"两个字上:
/// 改了要对下一条消息生效,但<b>不能</b>动模型配置里保存的那个值。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    /// <summary>一个供应商挂两个模型,用来验"换模型会不会把上一个的临时档位带过去"。</summary>
    private static AiProvider TwoModels(ReasoningLevel first, ReasoningLevel second)
        => new()
        {
            Name = "stub",
            BaseUrl = "http://127.0.0.1:1/v1",
            DefaultProtocol = ChatProtocol.OpenAiChatCompletions,
            Models =
            [
                new AiModelConfig { Model = "m1", Reasoning = first },
                new AiModelConfig { Model = "m2", Reasoning = second }
            ]
        };

    private static async Task<(Window Window, ChatPanelView Panel, ComboBox Combo)> WithReasoningPickerAsync(
        TestPluginContext context, AiProvider provider)
    {
        await new AiSettingsStore(context).SaveAsync(
            new AiSettings { Providers = [provider], ActiveModelId = provider.Models[0].Id });
        (Window window, ChatPanelView panel) = await ShowAsync(context);
        return (window, panel, Find<ComboBox>(panel, "ReasoningCombo"));
    }

    [TestMethod]
    public void ReasoningPicker_StartsOnTheLevelTheModelIsConfiguredWith()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.High, ReasoningLevel.Default));
            try
            {
                // 枚举值就是索引(Default/Off/Low/Medium/High)
                Assert.AreEqual((int)ReasoningLevel.High, combo.SelectedIndex);
                Assert.HasCount(5, (System.Collections.IEnumerable)combo.ItemsSource!);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 这一条是整个功能的要害:在工具条上改档位,<b>不能</b>写回模型配置。
    /// 写回去的话,下次打开设置页会发现档位莫名其妙换了,而用户根本不记得动过设置。
    /// </summary>
    [TestMethod]
    public void PickingALevel_DoesNotWriteBackToTheSavedModelSetting()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.High, ReasoningLevel.Default));
            try
            {
                combo.SelectedIndex = (int)ReasoningLevel.Low;
                await PumpAsync(10);

                AiSettings saved = await store.LoadAsync();
                Assert.AreEqual(ReasoningLevel.High, saved.Providers[0].Models[0].Reasoning,
                    "工具条上的选择是临时的,不该改动保存的配置");
                Assert.AreEqual((int)ReasoningLevel.Low, combo.SelectedIndex, "但界面上得停在选中的那一档");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 选回模型自己那一档 = 取消覆盖,而不是"覆盖成同一个值"——
    /// 否则芯片会一直亮着,而它本该只在"这一轮不一样"时才亮。
    /// </summary>
    [TestMethod]
    public void PickingTheSavedLevelAgain_ClearsTheOverride()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.High, ReasoningLevel.Default));
            try
            {
                object? plain = ToolTip.GetTip(combo);

                combo.SelectedIndex = (int)ReasoningLevel.Low;
                await PumpAsync(10);
                object? lit = ToolTip.GetTip(combo);
                Assert.AreNotEqual(plain, lit, "改过档之后,提示语要换成解释怎么取消的那一条");

                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.AreEqual(plain, ToolTip.GetTip(combo), "选回原档就该恢复成没改过的样子");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 档位下拉要<b>紧挨着</b>模型选择器 —— 它改的正是"这个模型这一问想多深",
    /// 中间隔一段空白就读成了两件不相干的设置。空档要排在两者之后。
    /// </summary>
    [TestMethod]
    public void ReasoningPicker_SitsRightNextToTheModelPicker()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.Default, ReasoningLevel.Default));
            try
            {
                ComboBox model = Find<ComboBox>(panel, "ProviderCombo");
                Assert.AreEqual(Grid.GetColumn(model) + 1, Grid.GetColumn(combo), "就在模型那一列的右边一列");

                var toolbar = Find<Grid>(panel, "InputToolbar");
                Assert.AreEqual(GridUnitType.Auto, toolbar.ColumnDefinitions[Grid.GetColumn(model)].Width.GridUnitType,
                    "模型那列若是 *,下拉会被推到星号列的最右端,中间空一大段");
                Assert.AreEqual(GridUnitType.Star,
                    toolbar.ColumnDefinitions[Grid.GetColumn(combo) + 1].Width.GridUnitType,
                    "空档排在两者之后 —— 收窄时先吃空白,而不是把发送按钮挤出去");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 没改过档时<b>一个外观属性都不该设</b>。
    /// </summary>
    /// <remarks>
    /// 这条是真机上撞出来的:原先在代码里 <c>FindResource</c> 取色写到控件上,
    /// 而本插件允许在拿不到宿主主题令牌的环境下装载(见 <c>Panel_Loads_WithoutHostThemeTokens</c>)——
    /// 那时取回 null,前景色被设成 null,文字整个消失,下拉只剩一个箭头。
    /// </remarks>
    [TestMethod]
    public void ReasoningPicker_Unchanged_LooksExactlyLikeTheModelPicker()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.Default, ReasoningLevel.Default));
            try
            {
                ComboBox model = Find<ComboBox>(panel, "ProviderCombo");
                Assert.AreEqual(model.Foreground, combo.Foreground, "前景色不该被代码写过 —— 写成 null 就没字了");
                Assert.AreEqual(model.Background, combo.Background);
                Assert.AreEqual(model.BorderBrush, combo.BorderBrush);
                Assert.DoesNotContain("overridden", combo.Classes);

                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);
                Assert.Contains("overridden", combo.Classes, "改过档才点亮,而且只经样式类,不写属性");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>models.dev 明说不会思考的模型,档位没得调 —— 灰掉,并说明为什么。</summary>
    [TestMethod]
    public void ReasoningPicker_IsDisabledForModelsThatCannotThink()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            provider.Models[0].SupportsReasoning = false; // 拉过规格库,答案是"不会"
            provider.Models[1].SupportsReasoning = true;
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, provider);
            try
            {
                Assert.IsFalse(combo.IsEnabled);

                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 1;
                await PumpAsync(10);
                Assert.IsTrue(combo.IsEnabled, "换到会思考的模型就该放开");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 手工添加的模型、中转站的私有型号都没拉过规格库 —— 那时是"不知道",不是"不会"。
    /// 凭默认值去灰掉一个其实能思考的模型,比多给一个无效档位糟得多。
    /// </summary>
    [TestMethod]
    public void ReasoningPicker_StaysOpenWhenTheCapabilityIsUnknown()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            AiProvider provider = TwoModels(ReasoningLevel.Default, ReasoningLevel.Default);
            Assert.IsNull(provider.Models[0].SupportsReasoning, "没拉过规格库就该是 null");
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, provider);
            try
            {
                Assert.IsTrue(combo.IsEnabled);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 换模型要清掉。各家的档位含义与默认值本来就不同,留着它等于把
    /// "我给上一个模型选的高"悄悄套到下一个头上。
    /// </summary>
    [TestMethod]
    public void SwitchingModels_DropsTheTemporaryLevel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel, ComboBox combo) =
                await WithReasoningPickerAsync(context, TwoModels(ReasoningLevel.Default, ReasoningLevel.Off));
            try
            {
                combo.SelectedIndex = (int)ReasoningLevel.High;
                await PumpAsync(10);

                Find<ComboBox>(panel, "ProviderCombo").SelectedIndex = 1;
                await PumpAsync(10);

                Assert.AreEqual((int)ReasoningLevel.Off, combo.SelectedIndex,
                    "换到第二个模型,显示的该是它自己配的那一档,不是上一个模型的临时选择");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }
}

/// <summary>临时档位在模型层的语义(与界面无关,单独测)。</summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ReasoningOverrideTests
{
    private static ResolvedModel Model(ReasoningLevel configured)
    {
        var config = new AiModelConfig { Model = "m", Reasoning = configured };
        return new ResolvedModel(
            new AiProvider { Name = "p", BaseUrl = "http://127.0.0.1:1/v1", Models = [config] }, config);
    }

    [TestMethod]
    public void WithReasoning_LeavesTheOriginalAlone()
    {
        ResolvedModel original = Model(ReasoningLevel.Low);

        ResolvedModel raised = original.WithReasoning(ReasoningLevel.High);

        Assert.AreEqual(ReasoningLevel.High, raised.Reasoning);
        // 界面会缓存并共享 ResolvedModel —— 就地改的话,某一轮的临时选择会渗到别处去
        Assert.AreEqual(ReasoningLevel.Low, original.Reasoning, "原对象不能被改动");
        Assert.AreEqual(ReasoningLevel.Low, original.Config.Reasoning, "更不该动到底下的配置");
    }

    [TestMethod]
    public void WithReasoning_Null_MeansUseWhateverTheModelSays()
    {
        ResolvedModel original = Model(ReasoningLevel.Medium);

        Assert.AreSame(original, original.WithReasoning(null));
        Assert.AreEqual(ReasoningLevel.Medium, original.WithReasoning(null).Reasoning);
    }

    /// <summary>临时档位得真的走到请求里去,而不是只改了个界面显示。</summary>
    [TestMethod]
    public void TheTemporaryLevel_ReachesTheRequest()
    {
        var options = new Microsoft.Extensions.AI.ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Model(ReasoningLevel.Default).WithReasoning(ReasoningLevel.High));

        Assert.IsNotNull(options.Reasoning);
        Assert.AreEqual(Microsoft.Extensions.AI.ReasoningEffort.High, options.Reasoning.Effort);
    }

    [TestMethod]
    public void WithoutAnOverride_DefaultStillMeansSendNothing()
    {
        var options = new Microsoft.Extensions.AI.ChatOptions();

        AiSettingsStore.ApplyReasoning(options, Model(ReasoningLevel.Default));

        Assert.IsNull(options.Reasoning, "Default 的含义就是请求里不带 reasoning 参数");
    }

    /// <summary>
    /// models.dev 的 <c>reasoning</c> 要落到模型配置上,而且 <c>false</c> 必须能写进去 ——
    /// 其余几项走的是"0 就当没说过",照搬那条规矩的话永远回不到 false。
    /// </summary>
    [TestMethod]
    public void PullingSpecs_RecordsWhetherTheModelCanThink()
    {
        var model = new AiModelConfig { Model = "m" };

        ModelsDevCatalog.Apply(model, new ModelSpec("m", "M", 128000, 8192, 1, 2, 0, true));
        Assert.IsTrue(model.SupportsReasoning);

        ModelsDevCatalog.Apply(model, new ModelSpec("m", "M", 128000, 8192, 1, 2, 0, false));
        Assert.IsFalse(model.SupportsReasoning, "拉到「不会思考」就得真的写下来");
    }

    [TestMethod]
    public void ReasoningAdjustable_OnlyClosesWhenWeActuallyKnow()
    {
        var unknown = new AiModelConfig { Model = "m" };
        var cannot = new AiModelConfig { Model = "m", SupportsReasoning = false };
        var can = new AiModelConfig { Model = "m", SupportsReasoning = true };
        AiProvider p = new() { Name = "p", BaseUrl = "http://127.0.0.1:1/v1", Models = [unknown, cannot, can] };

        Assert.IsTrue(new ResolvedModel(p, unknown).ReasoningAdjustable, "不知道就放开");
        Assert.IsFalse(new ResolvedModel(p, cannot).ReasoningAdjustable);
        Assert.IsTrue(new ResolvedModel(p, can).ReasoningAdjustable);
    }
}
