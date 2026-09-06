using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Views;

/// <summary>运行时反馈的浮层通道(见 <c>ToastHostViewModel</c>)。</summary>
public partial class ToastHostView : UserControl
{
    /// <summary>构造。</summary>
    public ToastHostView() => AvaloniaXamlLoader.Load(this);
}
