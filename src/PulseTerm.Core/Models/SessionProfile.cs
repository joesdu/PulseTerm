namespace PulseTerm.Core.Models;

public class SessionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Name { get; set; } = string.Empty;
    
    public string Host { get; set; } = string.Empty;
    
    public int Port { get; set; } = 22;
    
    public string Username { get; set; } = string.Empty;
    
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;
    
    public string? Password { get; set; }
    
    public string? PrivateKeyPath { get; set; }
    
    public string? PrivateKeyPassphrase { get; set; }
    
    public Guid? GroupId { get; set; }
    
    public DateTime? LastConnectedAt { get; set; }
    
    public List<string> Tags { get; set; } = new();
}
