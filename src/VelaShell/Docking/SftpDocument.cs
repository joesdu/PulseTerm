using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Docking;

/// <summary>独立的、与会话绑定的双栏 SFTP 文件浏览器的停靠文档。</summary>
public sealed class SftpDocument : DockDocument, IDockViewProvider
{
    /// <summary>从给定的视图模型初始化 SFTP 停靠文档。</summary>
    public SftpDocument(SftpDocumentViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Id = viewModel.SessionId.ToString("N");
        Title = viewModel.Title;
    }

    /// <summary>SFTP 文档的后台视图模型。</summary>
    public SftpDocumentViewModel ViewModel { get; }

    /// <summary>从连接配置派生的强调色画刷,用于视觉标识。</summary>
    public IBrush ConnectionAccentBrush => ConnectionAccent.BrushFor(ViewModel.Profile.Id);

    /// <summary>显示连接详情与配置信息的提示文本。</summary>
    public string ConnectionTooltip =>
        $"{Title} · SFTP · {ViewModel.Profile.Username}@{ViewModel.Profile.Host}:{ViewModel.Profile.Port}";

    /// <summary>创建用于停靠的 SFTP 文档视图。</summary>
    public Control CreateView() => new SftpDocumentView { DataContext = ViewModel };
}
