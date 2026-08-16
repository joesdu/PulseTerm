using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 全局设置窗口:系统提示词、上下文压缩、后续提问 —— 不归任何一个供应商/模型管的那些。
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
        SaveText.Text = _loc["Save"];
    }

    private async Task SaveAsync()
    {
        _settings.SystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        _settings.CompactContext = CompactContextCheck.IsChecked == true;
        _settings.SuggestFollowUps = SuggestFollowUpsCheck.IsChecked == true;
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
