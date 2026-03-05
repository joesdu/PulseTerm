using Avalonia.Controls;
using AvaloniaTerminal;

namespace PulseTerm.Terminal;

public class AvaloniaTerminalEmulator : ITerminalEmulator
{
    private readonly TerminalControl _terminalControl;
    private readonly TerminalControlModel _model;

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
                cols * 10,
                rows * 20,
                10,  
                20   
            );
        }
    }

    public string GetBufferLine(int row)
    {
        var line = string.Empty;
        for (int col = 0; col < _model.Terminal.Cols; col++)
        {
            if (_model.ConsoleText.TryGetValue((col, row), out var textObj))
            {
                line += textObj.Text;
            }
        }
        return line.TrimEnd();
    }

    public int CursorRow => _model.Terminal.Buffer.Y;
    public int CursorCol => _model.Terminal.Buffer.X;
    public int ScrollbackLines { get; set; } = 10000;
    public Control Control => _terminalControl;
    public int Columns => _model.Terminal.Cols;
    public int Rows => _model.Terminal.Rows;

    private void OnUserInput(byte[] data)
    {
        UserInput?.Invoke(data);
    }
    
    internal void TriggerUserInput(byte[] data)
    {
        OnUserInput(data);
    }
}
