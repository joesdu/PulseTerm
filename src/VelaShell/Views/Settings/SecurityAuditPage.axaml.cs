using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Data;
using VelaShell.Core.Recording;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>安全审计设置页:提供会话录制回放等安全审计相关入口。</summary>
public partial class SecurityAuditPage : UserControl
{
    /// <summary>初始化安全审计设置页并加载 XAML 组件。</summary>
    public SecurityAuditPage()
    {
        InitializeComponent();
    }

    /// <summary>打开会话录制回放中心(设计 NceE6),非模态独立窗口。</summary>
    private void OpenRecordingPlayer_Click(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app || app.Services is null)
        {
            return;
        }
        if (app.Services.GetService<ISessionRecordingStore>() is not { } store)
        {
            return;
        }
        var viewModel = new RecordingPlayerViewModel(store, app.Services.GetService<ISettingsService>());
        var window = new RecordingPlayerView { DataContext = viewModel };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
        _ = viewModel.InitializeAsync();
    }
}
