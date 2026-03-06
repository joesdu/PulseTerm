namespace PulseTerm.Core.Models;

public class KnownHost
{
    public string Host { get; set; } = string.Empty;
    
    public int Port { get; set; } = 22;
    
    public string HostKey { get; set; } = string.Empty;
    
    public string KeyType { get; set; } = string.Empty;
    
    public string Fingerprint { get; set; } = string.Empty;
    
    public string Algorithm { get; set; } = string.Empty;
    
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
