using System.Text;
using Avalonia.Input;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 把 Avalonia 的按键事件翻译成宿主所期望的字节序列,遵循应用光标键模式、VT52 模式
/// 以及 xterm 的修饰键编码。文本(可打印)输入则单独以 UTF-8 编码。
/// </summary>
public static class InputEncoder
{
    private static readonly byte[] Empty = [];

    /// <summary>把普通文本(来自 IME / TextInput)以 UTF-8 编码。</summary>
    public static byte[] EncodeText(string text) => string.IsNullOrEmpty(text) ? Empty : Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// 编码一次非文本按键。当该按键不产生直接序列时返回 null
    /// (此时控件应改由 TextInput 事件获取字符)。
    /// </summary>
    public static byte[]? Encode(Key key, KeyModifiers mods, TerminalModes modes, TerminalType type)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        bool shift = mods.HasFlag(KeyModifiers.Shift);

        // 控制键 + 字母组合映射到 C0 控制字符。
        if (ctrl && !alt)
        {
            byte? c0 = ControlByte(key, shift);
            if (c0 is { } b)
            {
                return WithAlt([b], false);
            }
        }
        bool app = modes.ApplicationCursorKeys && type != TerminalType.Vt52;
        bool vt52 = type == TerminalType.Vt52;
        int mod = ModifierCode(mods);
        return key switch
        {
            Key.Up => Cursor('A', app, vt52, mod, alt),
            Key.Down => Cursor('B', app, vt52, mod, alt),
            Key.Right => Cursor('C', app, vt52, mod, alt),
            Key.Left => Cursor('D', app, vt52, mod, alt),
            Key.Home => Cursor('H', app, vt52, mod, alt),
            Key.End => Cursor('F', app, vt52, mod, alt),
            Key.Insert => Tilde(2, mod, alt),
            Key.Delete => Tilde(3, mod, alt),
            Key.PageUp => Tilde(5, mod, alt),
            Key.PageDown => Tilde(6, mod, alt),
            Key.Enter => WithAlt(modes.NewLineMode ? "\r\n"u8.ToArray() : "\r"u8.ToArray(), alt),
            Key.Tab => shift ? Esc("[Z") : WithAlt([0x09], alt),
            // Ctrl+Backspace = 删除光标前一个单词(#127,与 Tabby 一致):发 ESC+DEL,
            // 即 readline 的 M-DEL(backward-kill-word)与 zsh 的 backward-kill-word 默认绑定,
            // 也与 Alt+Backspace 同序列。原先发的 0x08(^H)在 readline 里等价于退一格,
            // 与普通 Backspace 毫无区别,达不到删词效果。
            Key.Back => ctrl ? [0x1B, 0x7F] : WithAlt([0x7F], alt),
            Key.Escape => WithAlt([0x1B], alt),
            Key.F1 => Function(1, 'P', mod, alt, vt52),
            Key.F2 => Function(2, 'Q', mod, alt, vt52),
            Key.F3 => Function(3, 'R', mod, alt, vt52),
            Key.F4 => Function(4, 'S', mod, alt, vt52),
            Key.F5 => Tilde(15, mod, alt),
            Key.F6 => Tilde(17, mod, alt),
            Key.F7 => Tilde(18, mod, alt),
            Key.F8 => Tilde(19, mod, alt),
            Key.F9 => Tilde(20, mod, alt),
            Key.F10 => Tilde(21, mod, alt),
            Key.F11 => Tilde(23, mod, alt),
            Key.F12 => Tilde(24, mod, alt),
            _ => null
        };
    }

    private static byte[] Cursor(char final, bool app, bool vt52, int mod, bool alt)
    {
        if (vt52)
        {
            return Encoding.ASCII.GetBytes($"\e{final}");
        }
        if (mod > 1)
        {
            return Encoding.ASCII.GetBytes($"\e[1;{mod}{final}");
        }
        string prefix = app ? "\eO" : "\e[";
        return WithAlt(Encoding.ASCII.GetBytes($"{prefix}{final}"), false);
    }

    private static byte[] Tilde(int code, int mod, bool alt)
    {
        string seq = mod > 1 ? $"\e[{code};{mod}~" : $"\e[{code}~";
        return WithAlt(Encoding.ASCII.GetBytes(seq), alt && mod == 1);
    }

    private static byte[] Function(int number, char final, int mod, bool alt, bool vt52)
    {
        if (vt52)
        {
            return Encoding.ASCII.GetBytes($"\e{final}");
        }
        if (mod > 1)
        {
            return Encoding.ASCII.GetBytes($"\e[1;{mod}{final}");
        }
        return WithAlt(Encoding.ASCII.GetBytes($"\eO{final}"), alt && mod == 1);
    }

    private static byte[] Esc(string tail) => Encoding.ASCII.GetBytes("\e" + tail);

    /// <summary>按住 Alt 时在前面加 ESC(xterm 的 meta 发送 ESC 约定)。</summary>
    private static byte[] WithAlt(byte[] seq, bool alt)
    {
        if (!alt)
        {
            return seq;
        }
        byte[] result = new byte[seq.Length + 1];
        result[0] = 0x1B;
        Array.Copy(seq, 0, result, 1, seq.Length);
        return result;
    }

    /// <summary>xterm 修饰键参数:1 + shift(1) + alt(2) + ctrl(4) + meta(8)。</summary>
    private static int ModifierCode(KeyModifiers mods)
    {
        int m = 0;
        if (mods.HasFlag(KeyModifiers.Shift))
        {
            m += 1;
        }
        if (mods.HasFlag(KeyModifiers.Alt))
        {
            m += 2;
        }
        if (mods.HasFlag(KeyModifiers.Control))
        {
            m += 4;
        }
        if (mods.HasFlag(KeyModifiers.Meta))
        {
            m += 8;
        }
        return m + 1;
    }

    private static byte? ControlByte(Key key, bool shift)
    {
        // 字母 A-Z -> 0x01..0x1A
        if (key is >= Key.A and <= Key.Z)
        {
            return (byte)(key - Key.A + 1);
        }
        return key switch
        {
            Key.Space => 0x00,
            Key.OemOpenBrackets => 0x1B, // Ctrl+[
            Key.OemBackslash or Key.Oem5 => 0x1C, // Ctrl+反斜杠
            Key.OemCloseBrackets => 0x1D,
            Key.D2 when shift => 0x00, // Ctrl+@
            Key.D3 => 0x1B,
            Key.D4 => 0x1C,
            Key.D5 => 0x1D,
            Key.D6 => 0x1E, // Ctrl+^
            Key.D7 => 0x1F, // Ctrl+_
            Key.OemMinus => 0x1F,
            _ => null
        };
    }
}
