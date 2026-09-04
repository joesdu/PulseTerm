using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;

namespace VelaShell.Behaviors;

/// <summary>
/// 数字输入框的兜底:内容被清空时把界面上的类型转换异常换成一句人话,并在焦点离开时
/// 把值恢复成上一个有效值。全局挂在 <c>NumericUpDown</c> 样式上(见 Themes/DockStyles.axaml),
/// 无需逐个输入框声明。
/// </summary>
/// <remarks>
/// <c>NumericUpDown.Value</c> 是 <c>decimal?</c>,而设置项与连接配置里的目标属性是
/// <c>int</c> / <c>double</c>。用户按退格把框删空的那一瞬间,控件把 Value 置成 null,
/// 绑定引擎转换不了,就把异常对象本身当成校验错误摆到界面上 —— 那正是用户看到的
/// "System.InvalidCastException: Could not convert '(null)' (null) to System.Int32."。
/// <para>
/// 两件事分开修。文案:<see cref="DataValidationErrors.ErrorConverterProperty" /> 把任何错误
/// 换成"请输入 0 到 60 之间的数字",范围直接取控件自己的 Minimum / Maximum,不必逐处配文案。
/// 状态:焦点离开时空值自动回到上一个有效值 —— 清空只是编辑过程中的一个中间态,
/// 不该在用户走开之后还留一个红着的空框(那时目标属性其实一直是旧值,界面在说谎)。
/// </para>
/// <para>
/// 提示怎么显示是另一件事,不在这里:文案挂在 DockStyles.axaml 那条 <c>DataValidationErrors</c>
/// 的错误模板上(红色警告图标 + 悬停提示),换成图标是因为整段文字会把输入框那一列撑开。
/// </para>
/// </remarks>
public static class NumericInputGuard
{
    /// <summary>每个输入框上一个能用的值;控件回收时随之释放。</summary>
    private static readonly ConditionalWeakTable<NumericUpDown, LastValidValue> LastValid = [];

    /// <summary>附加在 <see cref="NumericUpDown" /> 上:是否接管空值的提示文案与恢复。</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<NumericUpDown, bool>("Enabled", typeof(NumericInputGuard));

    static NumericInputGuard() =>
        EnabledProperty.Changed.AddClassHandler<NumericUpDown>(OnEnabledChanged);

    /// <summary>读取某个数字输入框是否启用了空值兜底。</summary>
    public static bool GetEnabled(NumericUpDown box) => box.GetValue(EnabledProperty);

    /// <summary>开启或关闭某个数字输入框的空值兜底。</summary>
    public static void SetEnabled(NumericUpDown box, bool value) => box.SetValue(EnabledProperty, value);

    /// <summary>
    /// 该输入框当前该显示的提示文案:两端都有界时报出区间,否则只说"请输入数字"。
    /// 边界按控件的 <c>FormatString</c> 渲染,小数框(行高 0.8–2.0)才不会被显示成 "0.8 到 2"。
    /// </summary>
    internal static string Hint(NumericUpDown box) =>
        box.Minimum > decimal.MinValue && box.Maximum < decimal.MaxValue
            ? Strings.Format("Validation_NumberRange", Display(box, box.Minimum), Display(box, box.Maximum))
            : Strings.Get("Validation_NumberRequired");

    private static void OnEnabledChanged(NumericUpDown box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // 闭包捕获控件本身:错误转换器签名里拿不到目标控件,而提示文案要用它的 Minimum/Maximum。
            DataValidationErrors.SetErrorConverter(box, _ => Hint(box));
            box.PropertyChanged += OnBoxPropertyChanged;
            box.LostFocus += OnLostFocus;
            Remember(box);
        }
        else
        {
            box.LostFocus -= OnLostFocus;
            box.PropertyChanged -= OnBoxPropertyChanged;
            DataValidationErrors.SetErrorConverter(box, null);
        }
    }

    private static void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == NumericUpDown.ValueProperty && sender is NumericUpDown box)
        {
            Remember(box);
        }
    }

    private static void Remember(NumericUpDown box)
    {
        if (box.Value is { } value)
        {
            LastValid.GetValue(box, static _ => new LastValidValue()).Value = value;
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not NumericUpDown box || box.Value is not null)
        {
            return;
        }

        // SetCurrentValue 而不是直接赋值:直接赋值写的是本地值,优先级高过绑定,
        // 会把这个框和视图模型的双向绑定就地掐断,此后改设置再也传不回去。
        LastValid.TryGetValue(box, out LastValidValue? last);
        decimal fallback = last?.Value ?? 0m;
        box.SetCurrentValue(NumericUpDown.ValueProperty,
                            box.Minimum <= box.Maximum ? Math.Clamp(fallback, box.Minimum, box.Maximum) : fallback);
    }

    private static string Display(NumericUpDown box, decimal value)
    {
        if (string.IsNullOrEmpty(box.FormatString))
        {
            return value.ToString(CultureInfo.CurrentCulture);
        }
        try
        {
            return value.ToString(box.FormatString, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            // FormatString 是可以从 XAML 随便写的字符串;它不合法时提示文案照样得出得来。
            return value.ToString(CultureInfo.CurrentCulture);
        }
    }

    private sealed class LastValidValue
    {
        internal decimal Value { get; set; }
    }
}
