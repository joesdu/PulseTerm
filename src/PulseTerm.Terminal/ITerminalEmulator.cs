using Avalonia.Controls;

namespace PulseTerm.Terminal;

/// <summary>
/// Interface for terminal emulator that can be swapped between implementations
/// (AvaloniaTerminal, xterm.js, etc.)
/// </summary>
public interface ITerminalEmulator : IDisposable
{
    /// <summary>
    /// Feed raw bytes from SSH stream to terminal
    /// </summary>
    void Feed(byte[] data);
    
    /// <summary>
    /// Resize the terminal
    /// </summary>
    void Resize(int cols, int rows);
    
    /// <summary>
    /// Event fired when user types input (to send to SSH)
    /// </summary>
    event Action<byte[]>? UserInput;
    
    /// <summary>
    /// Programmatically send input bytes as if the user typed them
    /// </summary>
    void WriteInput(byte[] data);
    
    /// <summary>
    /// Get the content of a specific line in the terminal buffer
    /// </summary>
    string GetBufferLine(int row);
    
    /// <summary>
    /// Current cursor row position
    /// </summary>
    int CursorRow { get; }
    
    /// <summary>
    /// Current cursor column position
    /// </summary>
    int CursorCol { get; }
    
    /// <summary>
    /// Number of scrollback lines to keep
    /// </summary>
    int ScrollbackLines { get; set; }
    
    /// <summary>
    /// The Avalonia control to embed in UI
    /// </summary>
    Control Control { get; }
    
    /// <summary>
    /// Current number of columns
    /// </summary>
    int Columns { get; }
    
    /// <summary>
    /// Current number of rows
    /// </summary>
    int Rows { get; }
    
    ScrollbackBuffer ScrollbackBuffer { get; }
    
    int TotalLines { get; }
    
    int ViewportRow { get; }
}
