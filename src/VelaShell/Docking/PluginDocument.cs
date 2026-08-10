using Avalonia.Controls;
using VelaShell.Docking.Controls;
using VelaShell.Docking.Model;

namespace VelaShell.Docking;

/// <summary>
/// 插件面板的停靠文档:内容是宿主按插件声明式界面树渲染出的控件
/// (docs/plugins/dev-guide.md §UI)。与其它文档一样可拖拽到任意分栏位置。
/// </summary>
public sealed class PluginDocument : DockDocument, IDockViewProvider
{
    private readonly Control _view;

    /// <summary>用已渲染好的内容视图初始化插件停靠文档。</summary>
    public PluginDocument(string id, string title, string pluginId, Control view)
    {
        Id = id;
        Title = title;
        PluginId = pluginId;
        _view = view;
    }

    /// <summary>所属插件 id(标签提示用)。</summary>
    public string PluginId { get; }

    /// <summary>标签的悬停提示。</summary>
    public string Tooltip => $"{Title} · {PluginId}";

    /// <summary>返回缓存的内容视图(面板内容更新走视图内部,不重建控件)。</summary>
    public Control CreateView() => _view;
}
