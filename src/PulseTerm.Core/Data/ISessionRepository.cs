using PulseTerm.Core.Models;

namespace PulseTerm.Core.Data;

public interface ISessionRepository
{
    Task<List<ServerGroup>> GetAllGroupsAsync();
    Task<SessionProfile?> GetSessionAsync(Guid id);
    Task SaveSessionAsync(SessionProfile session);
    Task DeleteSessionAsync(Guid id);
    Task SaveGroupAsync(ServerGroup group);
    Task DeleteGroupAsync(Guid id);
}
