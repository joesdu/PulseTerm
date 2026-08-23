using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 全局设置窗口:系统提示词、上下文压缩、后续提问、网络检索 ——
/// 不归任何一个供应商/模型管的那些。
/// 直接编辑面板共享的 <see cref="AiSettings" /> 实例,保存后经回调通知面板落盘。
/// </summary>
public partial class GlobalSettingsView : UserControl
{
    private readonly IPluginContext _context;
    private readonly AiSettings _settings;
    private readonly Loc _loc;
    private readonly Func<Task> _persist;

    /// <summary>由聊天面板构造(UI 线程)。</summary>
    public GlobalSettingsView(IPluginContext context, AiSettings settings, Loc loc, Func<Task> persist)
    {
        _context = context;
        _settings = settings;
        _loc = loc;
        _persist = persist;
        InitializeComponent();
        ApplyLoc();

        SystemPromptBox.Text = _settings.SystemPrompt ?? "";
        CompactContextCheck.IsChecked = _settings.CompactContext;
        SuggestFollowUpsCheck.IsChecked = _settings.SuggestFollowUps;

        WebSearchOptions web = _settings.WebSearch;
        WebEnabledCheck.IsChecked = web.Enabled;
        WebSearxUrlBox.Text = web.SearxngBaseUrl;
        WebMaxResultsBox.Text = web.MaxResults.ToString();
        WebNativeCheck.IsChecked = web.PreferProviderNative;
        WebPrivateCheck.IsChecked = web.AllowPrivateNetwork;
        WebAllowedHostsBox.Text = web.AllowedPrivateHosts;
        UpdateWebVisibility();

        WebEnabledCheck.IsCheckedChanged += (_, _) => UpdateWebVisibility();
        SaveButton.Click += (_, _) => _ = SaveAsync();
    }

    /// <summary>语言切换时由面板调用。</summary>
    public void ApplyLoc()
    {
        SectionGlobalTitle.Text = _loc["SecGlobal"];
        SystemPromptLabel.Text = _loc["SystemPrompt"];
        CompactContextCheck.Content = _loc["CompactContext"];
        CompactContextHintText.Text = _loc["CompactContextHint"];
        SuggestFollowUpsCheck.Content = _loc["SuggestFollowUps"];
        SuggestFollowUpsHintText.Text = _loc["SuggestFollowUpsHint"];

        SectionWebTitle.Text = _loc["SecWebSearch"];
        WebEnabledCheck.Content = _loc["WebEnabled"];
        WebEnabledHintText.Text = _loc["WebEnabledHint"];
        WebSearxUrlLabel.Text = _loc["WebSearxUrl"];
        WebSearxHintText.Text = _loc["WebSearxHint"];
        WebMaxResultsLabel.Text = _loc["WebMaxResults"];
        WebNativeCheck.Content = _loc["WebNative"];
        WebNativeHintText.Text = _loc["WebNativeHint"];
        WebPrivateCheck.Content = _loc["WebPrivate"];
        WebPrivateHintText.Text = _loc["WebPrivateHint"];
        WebAllowedHostsLabel.Text = _loc["WebAllowedHosts"];
        WebAllowedHostsHintText.Text = _loc["WebAllowedHostsHint"];

        SaveText.Text = _loc["Save"];
    }

    /// <summary>关掉总闸就把整块细节收起来:那些字段一条都用不上,留着只是噪音。</summary>
    private void UpdateWebVisibility() => WebDetailPanel.IsVisible = WebEnabledCheck.IsChecked == true;

    private async Task SaveAsync()
    {
        _settings.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        _settings.CompactContext = CompactContextCheck.IsChecked == true;
        _settings.SuggestFollowUps = SuggestFollowUpsCheck.IsChecked == true;

        WebSearchOptions web = _settings.WebSearch;
        web.Enabled = WebEnabledCheck.IsChecked == true;
        web.SearxngBaseUrl = WebSearxUrlBox.Text?.Trim() ?? "";
        web.PreferProviderNative = WebNativeCheck.IsChecked == true;
        web.AllowPrivateNetwork = WebPrivateCheck.IsChecked == true;
        web.AllowedPrivateHosts = WebAllowedHostsBox.Text ?? "";
        // 填了非数字就当没改,别把用户已有的值清成 0
        if (int.TryParse(WebMaxResultsBox.Text, out int count))
        {
            web.MaxResults = count;
        }
        web.Clamp();
        WebMaxResultsBox.Text = web.MaxResults.ToString();

        try
        {
            await _persist();
            StatusText.Text = _loc["Saved"];
        }
        catch (Exception ex)
        {
            _context.Log.Error("Save AI global settings failed.", ex);
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }
}
