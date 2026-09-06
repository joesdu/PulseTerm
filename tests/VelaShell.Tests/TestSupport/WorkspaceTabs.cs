using VelaShell.Docking;
using VelaShell.ViewModels;

namespace VelaShell.Tests.TestSupport;

/// <summary>
/// 在测试里开标签、切标签的两个动作。
/// </summary>
/// <remarks>
/// Q-02 之后标签集合的唯一事实来源是停靠工作区:<c>ActiveTerminalTab</c> 是从
/// <c>Layout.ActiveDocumentChanged</c> 派生的只读属性,测试不能再直接给它赋值。
/// 把"开一个 / 切过去"包成两个方法,免得每条用例各写一遍找文档的那三行 ——
/// 那三行一旦散开,下次改停靠模型就要改几十处。
/// </remarks>
internal static class WorkspaceTabs
{
    /// <summary>把一个终端标签开进工作区(顺带成为活动标签),返回承载它的文档。</summary>
    /// <param name="viewModel">主窗口视图模型。</param>
    /// <param name="tab">要开的标签。</param>
    /// <returns>承载该标签的停靠文档。</returns>
    public static TerminalDocument Open(this MainWindowViewModel viewModel, TerminalTabViewModel tab)
    {
        var document = new TerminalDocument(tab);
        viewModel.Layout.AddDocument(document);
        return document;
    }

    /// <summary>切到某个已经开着的终端标签(等价于用户点它的标签页)。</summary>
    /// <param name="viewModel">主窗口视图模型。</param>
    /// <param name="tab">要切过去的标签;不在工作区里时是空操作。</param>
    public static void Activate(this MainWindowViewModel viewModel, TerminalTabViewModel tab)
    {
        if (viewModel.Layout.AllDocuments()
                     .OfType<TerminalDocument>()
                     .FirstOrDefault(d => ReferenceEquals(d.Terminal, tab)) is { } document)
        {
            viewModel.Layout.ActivateDocument(document);
        }
    }
}
