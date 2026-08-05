using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace VelaShell.Controls;

/// <summary>
/// 承载一个必须在同一时刻只出现在一个位置的共享 <see cref="Control" />(实时终端画面)。
/// Avalonia 禁止一个控件拥有两个可视父级,因此当 Dock 在第二个展示器中呈现同一文档时
/// (拆分、标签拖拽、或拖出为浮动窗口),共享控件会抛出"已拥有可视父级"异常。
/// 本宿主通过"抢夺"目标来解决:每当一个宿主挂载(或其目标变更)时,它先把目标从
/// 先前的父级分离,再收养它。由于同一时刻只有当前可见的视图被挂载,
/// 目标最终始终只被父级化一次。
/// </summary>
public sealed class ReparentingHost : Decorator
{
    /// <summary>标识 <see cref="Target" /> 样式化属性。</summary>
    public static readonly StyledProperty<Control?> TargetProperty =
        AvaloniaProperty.Register<ReparentingHost, Control?>(nameof(Target));

    static ReparentingHost()
    {
        TargetProperty.Changed.AddClassHandler<ReparentingHost>((host, _) => host.Reattach());
    }

    /// <summary>
    /// 需要被独占托管的共享控件(如实时终端画面)。设置后本宿主会先将其从原父级
    /// 分离,再收养为自身子级,确保该控件在可视树中始终只有一个父级。
    /// </summary>
    public Control? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>挂载到可视树时将目标控件重新收养为子级。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reattach();
    }

    /// <summary>从可视树分离时释放对目标控件的收养,避免重复父级。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (ReferenceEquals(Child, Target))
        {
            Child = null;
        }
    }

    private void Reattach()
    {
        Control? target = Target;
        if (target is null)
        {
            Child = null;
            return;
        }
        if (ReferenceEquals(Child, target))
        {
            return;
        }
        DetachFromCurrentParent(target);
        Child = target;
    }

    private static void DetachFromCurrentParent(Control control)
    {
        switch (control.Parent)
        {
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, control):
                contentControl.Content = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, control):
                presenter.Content = null;
                break;
            case Panel panel:
                panel.Children.Remove(control);
                break;
        }
    }
}
