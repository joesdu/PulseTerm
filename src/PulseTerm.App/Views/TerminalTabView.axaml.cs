using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using PulseTerm.App.Services;
using PulseTerm.App.ViewModels;

namespace PulseTerm.App.Views;

public partial class TerminalTabView : UserControl
{
    private readonly IKeyboardShortcutService _shortcutService;

    public TerminalTabView()
        : this(new KeyboardShortcutService())
    {
    }

    public TerminalTabView(IKeyboardShortcutService shortcutService)
    {
        _shortcutService = shortcutService ?? throw new ArgumentNullException(nameof(shortcutService));
        InitializeComponent();

        Focusable = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var modifiers = MapModifiers(e.KeyModifiers);
        var key = MapKey(e.Key);

        if (key == KeyCode.None)
        {
            base.OnKeyDown(e);
            return;
        }

        var action = _shortcutService.Resolve(modifiers, key, ShortcutContext.Terminal);

        switch (action)
        {
            case ShortcutAction.Copy:
                _ = CopySelectionAsync();
                e.Handled = true;
                return;

            case ShortcutAction.Paste:
                _ = PasteFromClipboardAsync();
                e.Handled = true;
                return;

            case ShortcutAction.SendInterrupt:
                SendBytesToTerminal(new byte[] { 0x03 });
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            var bytes = Encoding.UTF8.GetBytes(e.Text);
            SendBytesToTerminal(bytes);
            e.Handled = true;
        }

        base.OnTextInput(e);
    }

    private void SendBytesToTerminal(byte[] data)
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            vm.TerminalEmulator.WriteInput(data);
        }
    }

    private async System.Threading.Tasks.Task CopySelectionAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard == null)
            return;

        var selectedText = GetSelectedText();
        if (!string.IsNullOrEmpty(selectedText))
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    private async System.Threading.Tasks.Task PasteFromClipboardAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard == null)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            SendBytesToTerminal(bytes);
        }
    }

    private IClipboard? GetClipboard()
    {
        return TopLevel.GetTopLevel(this)?.Clipboard;
    }

    private string GetSelectedText()
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            var emulator = vm.TerminalEmulator;
            var sb = new StringBuilder();
            for (int row = 0; row < emulator.Rows; row++)
            {
                var line = emulator.GetBufferLine(row);
                if (!string.IsNullOrEmpty(line))
                {
                    if (sb.Length > 0)
                        sb.AppendLine();
                    sb.Append(line);
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    private static Services.KeyModifiers MapModifiers(Avalonia.Input.KeyModifiers avaloniaModifiers)
    {
        var result = Services.KeyModifiers.None;

        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            result |= Services.KeyModifiers.Ctrl;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
            result |= Services.KeyModifiers.Shift;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
            result |= Services.KeyModifiers.Alt;
        if (avaloniaModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
            result |= Services.KeyModifiers.Meta;

        return result;
    }

    private static KeyCode MapKey(Key avaloniaKey)
    {
        return avaloniaKey switch
        {
            Key.C => KeyCode.C,
            Key.V => KeyCode.V,
            Key.T => KeyCode.T,
            Key.W => KeyCode.W,
            Key.Tab => KeyCode.Tab,
            Key.OemComma => KeyCode.Comma,
            _ => KeyCode.None
        };
    }
}
