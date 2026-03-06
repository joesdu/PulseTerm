namespace PulseTerm.Core.Models;

public class AppSettings
{
    public string Language { get; set; } = "en";
    
    public string Theme { get; set; } = "dark";
    
    public string TerminalFont { get; set; } = "JetBrains Mono";
    
    public int TerminalFontSize { get; set; } = 14;
    
    public int ScrollbackLines { get; set; } = 10000;
    
    public int DefaultPort { get; set; } = 22;
}
