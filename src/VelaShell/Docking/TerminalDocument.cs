using Avalonia.Controls;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Docking;

/// <summary>
/// 承载单个 SSH/本地终端标签的可停靠文档。包装终端(而不是让
/// <see cref="TerminalTabViewModel" /> 自己成为文档)使既有标签集合与测试不受
/// 停靠层影响。浮动/Pin 由产品决策禁用,VelaDock 未建模,天然不存在。
/// </summary>
public sealed class TerminalDocument : DockDocument, IDockViewProvider
{
    /// <summary>用给定终端标签视图模型创建可停靠文档,并同步其 Id 与标题。</summary>
    /// <param name="terminal">被本文档包装的终端标签视图模型。</param>
    public TerminalDocument(TerminalTabViewModel terminal)
    {
        Terminal = terminal;
        Id = terminal.Id.ToString("N");
        Title = terminal.Title;
    }

    /// <summary>本文档所承载的终端标签视图模型。</summary>
    public TerminalTabViewModel Terminal { get; }

    /// <summary>每个文档只被 DockWorkspaceControl 调用一次,视图随后全程复用。</summary>
    public Control CreateView() => new TerminalTabView { DataContext = Terminal };
}
