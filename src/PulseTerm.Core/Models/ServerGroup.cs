namespace PulseTerm.Core.Models;

public class ServerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Name { get; set; } = string.Empty;
    
    public string? Icon { get; set; }
    
    public int SortOrder { get; set; }
    
    public List<Guid> Sessions { get; set; } = new();
}
