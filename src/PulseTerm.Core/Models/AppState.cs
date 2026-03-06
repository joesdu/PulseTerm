namespace PulseTerm.Core.Models;

public class AppState
{
    public List<string> RecentConnections { get; set; } = new();
    
    public WindowPosition? WindowPosition { get; set; }
    
    public WindowSize? WindowSize { get; set; }
    
    public string? LastActiveTab { get; set; }
}

public class WindowPosition
{
    public int X { get; set; }
    
    public int Y { get; set; }
}

public class WindowSize
{
    public int Width { get; set; }
    
    public int Height { get; set; }
}
