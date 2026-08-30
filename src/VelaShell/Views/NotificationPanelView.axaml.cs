using Avalonia.Controls;
using VelaShell.Behaviors;

namespace VelaShell.Views;

/// <summary>消息中心面板视图:侧边栏铃铛切换的非模态浮层,表头可拖动、位置跨会话保留。</summary>
public partial class NotificationPanelView : UserControl
{
    /// <summary>创建消息中心面板视图并加载其可视组件,接线表头拖拽。</summary>
    public NotificationPanelView()
    {
        InitializeComponent();

        // 拖拽 + 越界夹紧 + 松手落盘,与文件传输提示共用一份实现。
        if (this.FindControl<Border>("DragHandle") is { } handle)
        {
            PanelDragHandler.Attach(this, handle);
        }
    }
}
