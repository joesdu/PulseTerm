namespace VelaShell.ViewModels;

/// <summary>
/// 可拖动浮层位置的持久化载体(<c>IAppDataStore</c> 需要引用类型)。
/// 各浮层在 <c>ui-layout</c> 集合里各占一个文档 Id(如 <c>transfer-panel</c>、
/// <c>notification-panel</c>)。存偏移而非绝对坐标,理由见 <see cref="IDraggablePanel" />。
/// </summary>
public sealed class PanelPosition
{
    /// <summary>相对默认锚点的水平偏移(像素)。</summary>
    public double OffsetX { get; set; }

    /// <summary>相对默认锚点的垂直偏移(像素)。</summary>
    public double OffsetY { get; set; }
}
