using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Services;

namespace VelaShell.Behaviors;

/// <summary>Temporarily selects a loaded English input locale while a control has focus.</summary>
public static class EnglishInputLocale
{
    private static readonly ConditionalWeakTable<TextBox, BoxState> States = [];

    internal static Func<IInputLocaleSwitcher> InputLocaleSwitcherFactory { get; set; } =
        static () => new InputLocaleSwitcher(new WindowsKeyboardLayoutNative());

    /// <summary>Whether the attached <see cref="TextBox" /> selects a loaded English input locale while focused.</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Enabled", typeof(EnglishInputLocale));

    static EnglishInputLocale() =>
        EnabledProperty.Changed.AddClassHandler<TextBox>(OnEnabledChanged);

    /// <summary>Reads whether English input-locale switching is enabled for a text box.</summary>
    public static bool GetEnabled(TextBox textBox) => textBox.GetValue(EnabledProperty);

    /// <summary>Enables or disables English input-locale switching for a text box.</summary>
    public static void SetEnabled(TextBox textBox, bool value) => textBox.SetValue(EnabledProperty, value);

    private static BoxState State(TextBox textBox) => States.GetValue(textBox, _ => new());

    private static void OnEnabledChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            BoxState state = State(textBox);
            state.ImeWasEnabled ??= InputMethod.GetIsInputMethodEnabled(textBox);
            textBox.SetCurrentValue(InputMethod.IsInputMethodEnabledProperty, false);

            textBox.AddHandler(InputElement.GotFocusEvent, OnGotFocus);
            textBox.AddHandler(InputElement.LostFocusEvent, OnLostFocus);
            textBox.AttachedToVisualTree += OnAttachedToVisualTree;
            textBox.DetachedFromVisualTree += OnDetachedFromVisualTree;
            if (textBox.IsFocused)
            {
                SelectEnglishLocale(textBox);
            }
        }
        else
        {
            textBox.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
            textBox.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
            textBox.AttachedToVisualTree -= OnAttachedToVisualTree;
            textBox.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            RestorePriorLocale(textBox);

            BoxState state = State(textBox);
            if (state.ImeWasEnabled is { } wasEnabled)
            {
                textBox.SetCurrentValue(InputMethod.IsInputMethodEnabledProperty, wasEnabled);
                state.ImeWasEnabled = null;
            }
        }
    }

    private static void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && GetEnabled(textBox))
        {
            SelectEnglishLocale(textBox);
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            RestorePriorLocale(textBox);
        }
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox textBox && textBox.IsFocused && GetEnabled(textBox))
        {
            SelectEnglishLocale(textBox);
        }
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            RestorePriorLocale(textBox);
        }
    }

    private static void SelectEnglishLocale(TextBox textBox)
    {
        BoxState state = State(textBox);
        if (!state.LocaleSwitched && state.LocaleSwitcher.TrySelectEnglish(out nint priorLayout))
        {
            state.LocaleSwitched = true;
            state.PriorLayout = priorLayout;
        }
    }

    private static void RestorePriorLocale(TextBox textBox)
    {
        BoxState state = State(textBox);
        if (!state.LocaleSwitched)
        {
            return;
        }

        state.LocaleSwitched = false;
        state.LocaleSwitcher.Restore(state.PriorLayout);
        state.PriorLayout = nint.Zero;
    }

    private sealed class BoxState
    {
        public IInputLocaleSwitcher LocaleSwitcher { get; } = InputLocaleSwitcherFactory();
        public bool LocaleSwitched;
        public nint PriorLayout;
        public bool? ImeWasEnabled;
    }
}
