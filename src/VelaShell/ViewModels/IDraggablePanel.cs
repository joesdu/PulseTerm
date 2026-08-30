namespace VelaShell.ViewModels;

/// <summary>
/// 可拖动浮层(文件传输提示、消息中心……)的视图模型契约。
/// <para>
/// 存的是**相对默认锚点的偏移**而非绝对坐标:窗口缩放/最大化后浮层仍贴着它原来的
/// 相对位置,不会因为窗口变小而跑到可视区外。越界夹紧由视图负责 ——
/// 只有视图知道父容器与自身的实际尺寸。
/// </para>
/// </summary>
public interface IDraggablePanel
{
    /// <summary>相对默认锚点的水平偏移(像素)。</summary>
    double PanelOffsetX { get; set; }

    /// <summary>相对默认锚点的垂直偏移(像素)。</summary>
    double PanelOffsetY { get; set; }

    /// <summary>拖拽结束时由视图调用:把当前位置落盘,供下次打开恢复。失败不该影响使用。</summary>
    void PersistPanelPosition();
}
