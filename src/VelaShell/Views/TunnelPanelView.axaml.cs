using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>隧道(端口转发)面板视图,展示与管理隧道相关的 UI。</summary>
public partial class TunnelPanelView : UserControl
{
    private readonly Func<string, Task<bool>> _confirmDeleteHandler;
    private TunnelPanelViewModel? _viewModel;

    /// <summary>初始化 <see cref="TunnelPanelView"/> 并加载 XAML 组件。</summary>
    public TunnelPanelView()
    {
        InitializeComponent();
        _confirmDeleteHandler = ConfirmDeleteAsync;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is { } previous && ReferenceEquals(previous.ConfirmDelete, _confirmDeleteHandler))
        {
            previous.ConfirmDelete = null;
        }

        _viewModel = DataContext as TunnelPanelViewModel;
        if (_viewModel is { } vm)
        {
            vm.ConfirmDelete = _confirmDeleteHandler;
        }
    }

    private async void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        await new TunnelHelpDialog().ShowDialog(owner);
    }

    private async Task<bool> ConfirmDeleteAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.ConfirmDeleteTitle,
            message,
            Strings.Delete,
            kind: MessageDialogKind.Warning,
            danger: true
        );
    }
}
