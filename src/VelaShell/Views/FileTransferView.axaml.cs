using Avalonia.Controls;
using VelaShell.Behaviors;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>文件传输视图,展示传输进度与结果提示;表头可拖动,位置跨会话保留。</summary>
public partial class FileTransferView : UserControl
{
    /// <summary>初始化视图,接线指针悬停(暂停自动隐藏)与表头拖拽。</summary>
    public FileTransferView()
    {
        InitializeComponent();

        // 悬停在提示上会暂停其自动隐藏,以便查看结果;指针离开后
        // 3 秒倒计时恢复(§9)。
        PointerEntered += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(true);
        PointerExited += (_, _) => (DataContext as FileTransferViewModel)?.SetPointerOver(false);

        // 拖拽 + 越界夹紧 + 松手落盘,与消息中心共用一份实现。
        if (this.FindControl<Border>("DragHandle") is { } handle)
        {
            PanelDragHandler.Attach(this, handle);
        }
    }
}
