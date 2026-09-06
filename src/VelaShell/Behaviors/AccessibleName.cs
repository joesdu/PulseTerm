using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace VelaShell.Behaviors;

/// <summary>
/// 让没有可读文字的按钮把自己的悬停提示当作无障碍名字。
/// </summary>
/// <remarks>
/// <para>
/// 界面上大量按钮的内容只是一个图标(新建、关闭、刷新、折叠侧栏、打开设置……)——
/// 读屏器读到的是"按钮",仅此而已,整个应用对它们来说是一排无名按钮。
/// </para>
/// <para>
/// 但这些按钮**几乎都写过 <c>ToolTip.Tip</c>**(全仓 116 处),文案本来就在,
/// 只是写在了读屏器读不到的地方。这个附加行为把那份文案顺手交给
/// <see cref="AutomationProperties.NameProperty" />:全局样式挂一次,一百多个图标按钮
/// 一次性有了名字,不必逐个补属性。
/// </para>
/// <para>
/// <b>只在名字为空时才写。</b>显式设过 <c>AutomationProperties.Name</c> 的地方一律尊重 ——
/// 那是作者刻意为读屏器写的文案,通常比提示更准确。
/// </para>
/// </remarks>
public static class AccessibleName
{
    /// <summary>附加在按钮上:名字为空时用 <c>ToolTip.Tip</c> 补上。</summary>
    public static readonly AttachedProperty<bool> FromToolTipProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FromToolTip", typeof(AccessibleName));

    static AccessibleName()
    {
        FromToolTipProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is true)
            {
                Apply(control);
                // 提示文案是会变的(连接状态、开关标题),名字要跟着走。
                ToolTip.TipProperty.Changed.AddClassHandler<Control>((c, _) =>
                {
                    if (GetFromToolTip(c))
                    {
                        Apply(c);
                    }
                });
            }
        });
        FromRowLabelProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is not true)
            {
                return;
            }
            // 样式是在控件挂进树时套上的,那一刻同一行的标签往往还没渲染出文字
            // (文案多半来自本地化绑定)。所以挂到 AttachedToVisualTree 上再取一次。
            ApplyRowLabel(control);
            control.AttachedToVisualTree += (sender, _) => ApplyRowLabel((Control)sender!);
        });
    }

    /// <summary>读取附加属性。</summary>
    /// <param name="control">目标控件。</param>
    /// <returns>是否启用。</returns>
    public static bool GetFromToolTip(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(FromToolTipProperty);
    }

    /// <summary>设置附加属性。</summary>
    /// <param name="control">目标控件。</param>
    /// <param name="value">是否启用。</param>
    public static void SetFromToolTip(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(FromToolTipProperty, value);
    }

    /// <summary>
    /// 把提示文案填进无障碍名字(仅当名字为空、且控件本身没有可读文字时)。
    /// </summary>
    /// <param name="control">目标控件。</param>
    public static void Apply(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)))
        {
            return;
        }
        if (HasReadableContent(control))
        {
            // 文字按钮的内容本身就是名字,读屏器读得到,不必再套一层。
            return;
        }
        if (ToolTip.GetTip(control) is string tip && !string.IsNullOrWhiteSpace(tip))
        {
            AutomationProperties.SetName(control, tip);
        }
    }

    /// <summary>按钮的内容是不是一段读得出来的文字。</summary>
    private static bool HasReadableContent(Control control) =>
        control is ContentControl { Content: string text } && !string.IsNullOrWhiteSpace(text);

    // ---- 设置行的开关:名字取同一行的标签 ----

    /// <summary>设置行里承载说明文字的 <c>TextBlock</c> 所用的样式类。</summary>
    private const string RowLabelClass = "row-label";

    /// <summary>
    /// 附加在设置行的控件上:名字为空时取同一行 <c>row-label</c> 的文字。
    /// </summary>
    public static readonly AttachedProperty<bool> FromRowLabelProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FromRowLabel", typeof(AccessibleName));

    /// <summary>读取附加属性。</summary>
    /// <param name="control">目标控件。</param>
    /// <returns>是否启用。</returns>
    public static bool GetFromRowLabel(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(FromRowLabelProperty);
    }

    /// <summary>设置附加属性。</summary>
    /// <param name="control">目标控件。</param>
    /// <param name="value">是否启用。</param>
    public static void SetFromRowLabel(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(FromRowLabelProperty, value);
    }

    /// <summary>
    /// 把同一设置行的标签文字填进无障碍名字。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 设置页里几十个 <c>ToggleSwitch</c> 自己没有任何文字,说明全在同一行左侧的
    /// <c>row-label</c> 上 —— 用眼睛看一目了然,读屏器听到的却只有"开关,已选中"。
    /// </para>
    /// <para>
    /// 逐个补 <c>AutomationProperties.Name</c> 要改三十来处,而且下一个新增的开关必然又会忘。
    /// 这里顺着设置页统一的行结构(<c>Grid</c> 里一个 <c>row-label</c> + 右侧控件)去取,
    /// 全局挂一次即可,新增的行自动就有名字。<c>AccessibleNameTests</c> 逐页扫描把关。
    /// </para>
    /// </remarks>
    /// <param name="control">目标控件。</param>
    public static void ApplyRowLabel(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control))
            || ToolTip.GetTip(control) is string { Length: > 0 })
        {
            return;
        }
        if (FindRowLabel(control) is { } label)
        {
            AutomationProperties.SetName(control, label);
        }
    }

    /// <summary>沿父链向上找最近的一行,取那一行里 <c>row-label</c> 的文字。</summary>
    private static string? FindRowLabel(Control control)
    {
        // 只向上找三层。设置行的结构是 Grid > (StackPanel > TextBlock.row-label | 开关),
        // 再往上就是整页的容器了 —— 那一层扫出来的会是隔壁行的标签,
        // 而给控件一个错的名字比不给名字更糟。
        StyledElement? node = control.Parent;
        for (int depth = 0; depth < 3 && node is not null; depth++, node = node.Parent)
        {
            if (node is Panel panel && LabelIn(panel, 2) is { } text)
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>在一行内部找 <c>row-label</c>,同样限制下探深度。</summary>
    private static string? LabelIn(Panel panel, int depth)
    {
        foreach (Control child in panel.Children)
        {
            if (child is TextBlock { Text: { Length: > 0 } text } block && block.Classes.Contains(RowLabelClass))
            {
                return text;
            }
            if (depth > 0 && child is Panel nested && LabelIn(nested, depth - 1) is { } inner)
            {
                return inner;
            }
        }
        return null;
    }
}
