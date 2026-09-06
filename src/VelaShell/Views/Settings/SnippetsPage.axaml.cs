using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>代码片段设置页:管理可复用的命令片段(Snippets)。</summary>
/// <remarks>
/// 列表行里的「编辑」「删除」走代码后置而不是绑定。
/// <para>
/// 行的 DataContext 是 <see cref="QuickCommandViewModel" />(条目自己),要够到页面的
/// <see cref="SettingsViewModel" /> 就得写 <c>$parent[UserControl].DataContext.…</c> ——
/// 而 <c>.DataContext</c> 在绑定路径里是**显式的一环**:页面刚 new 出来、还没挂进可视树
/// 那一刻它就是 null,于是每开一次设置就往调试输出刷一串 "Value is null"。
/// (Avalonia 的编译绑定里 <c>RelativeSource</c> 给的同样是控件对象,绕不开这一环。)
/// </para>
/// <para>
/// 从 <c>sender</c> 取条目、从自己的 DataContext 取页面视图模型,两者都不经过绑定,
/// 也就没有那个空窗期。同目录的密钥管理页与常规页出于同样的理由也是这么写的。
/// </para>
/// </remarks>
public partial class SnippetsPage : UserControl
{
    /// <summary>初始化代码片段设置页并加载 XAML 组件。</summary>
    public SnippetsPage() => InitializeComponent();

    private void EditSnippet_Click(object? sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is var (snippets, command))
        {
            snippets.BeginEditCommand.Execute(command).Subscribe();
        }
    }

    private void DeleteSnippet_Click(object? sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is var (snippets, command))
        {
            snippets.DeleteCommandCommand.Execute(command).Subscribe();
        }
    }

    /// <summary>取"这一行是哪个片段"与"页面的片段视图模型";任一缺失即返回 null。</summary>
    private (QuickCommandsViewModel Snippets, QuickCommandViewModel Command)? Resolve(object? sender) =>
        sender is Control { DataContext: QuickCommandViewModel command }
        && DataContext is SettingsViewModel { Snippets: { } snippets }
            ? (snippets, command)
            : null;
}
