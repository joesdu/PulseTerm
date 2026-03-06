using System;
using System.Text;
using Avalonia.Controls;
using AvaloniaTerminal;

namespace PulseTerm.Terminal;

public class AvaloniaTerminalEmulator : ITerminalEmulator
{
    private const int DefaultCharWidth = 10;
    private const int DefaultCharHeight = 20;

    private readonly TerminalControl _terminalControl;
    private readonly TerminalControlModel _model;
    private bool _disposed;

    public AvaloniaTerminalEmulator()
    {
        _model = new TerminalControlModel();
        _model.UserInput += OnUserInput;
        
        _terminalControl = new TerminalControl
        {
            Model = _model,
            FontFamily = "Cascadia Mono",
            FontSize = 14
        };
        
        ScrollbackBuffer = new ScrollbackBuffer(ScrollbackLines);
    }

    public event Action<byte[]>? UserInput;

    public void Feed(byte[] data)
    {
        _model.Feed(data, data.Length);
    }

    public void Resize(int cols, int rows)
    {
        var textSize = _terminalControl.Bounds;
        if (textSize.Width > 0 && textSize.Height > 0)
        {
            _model.Resize(
                cols * DefaultCharWidth,
                rows * DefaultCharHeight,
                DefaultCharWidth,
                DefaultCharHeight
            );
        }
    }

    public string GetBufferLine(int row)
    {
        var sb = new StringBuilder(_model.Terminal.Cols);
        for (int col = 0; col < _model.Terminal.Cols; col++)
        {
            if (_model.ConsoleText.TryGetValue((col, row), out var textObj))
                sb.Append(textObj.Text);
        }
        return sb.ToString().TrimEnd();
    }

    public int CursorRow => _model.Terminal.Buffer.Y;
    public int CursorCol => _model.Terminal.Buffer.X;
    public int ScrollbackLines { get; set; } = 10000;
    public Control Control => _terminalControl;
    public int Columns => _model.Terminal.Cols;
    public int Rows => _model.Terminal.Rows;
    public ScrollbackBuffer ScrollbackBuffer { get; }
    public int TotalLines => ScrollbackBuffer.TotalLines;
    public int ViewportRow => ScrollbackBuffer.ViewportRow;

    private void OnUserInput(byte[] data)
    {
        UserInput?.Invoke(data);
    }

    public void WriteInput(byte[] data)
    {
        UserInput?.Invoke(data);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _model.UserInput -= OnUserInput;
    }
}
