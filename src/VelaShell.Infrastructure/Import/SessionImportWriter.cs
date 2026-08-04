using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

namespace VelaShell.Infrastructure.Import;

/// <summary>把一批 <see cref="ImportedSession" /> 写入仓储的共享逻辑(各来源的导入服务复用)。</summary>
internal static class SessionImportWriter
{
    /// <summary>
    /// 新建一个分组承载选中的受支持会话并逐条持久化;密码由仓储 AES 重新加密落盘,
    /// 仅当密码成功还原时才设 <see cref="SessionProfile.RememberPassword" />。
    /// </summary>
    public static async Task<SessionImportOutcome> WriteAsync(
        ISessionRepository repository,
        IReadOnlyList<ImportedSession> items,
        string groupName,
        string tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var toImport = items.Where(static i => i.IsSupported).ToList();
        if (toImport.Count == 0)
        {
            return new SessionImportOutcome { Imported = 0, PasswordsRecovered = 0, GroupId = null };
        }

        List<ServerGroup> groups = await repository.GetAllGroupsAsync().ConfigureAwait(false);
        int nextSort = groups.Count == 0 ? 0 : groups.Max(static g => g.SortOrder) + 1;
        var group = new ServerGroup
        {
            Name = string.IsNullOrWhiteSpace(groupName) ? tag : groupName,
            SortOrder = nextSort
        };

        int recovered = 0;
        foreach (ImportedSession item in toImport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = new SessionProfile
            {
                ConnectionType = item.ConnectionType,
                Name = item.Name,
                Host = item.Host,
                Port = item.Port,
                Username = item.Username,
                AuthMethod = AuthMethod.Password,
                Password = item.Password,
                RememberPassword = item.PasswordRecovered,
                GroupId = group.Id,
                Tags = [tag.ToLowerInvariant()]
            };
            await repository.SaveSessionAsync(profile).ConfigureAwait(false);
            group.Sessions.Add(profile.Id);
            if (item.PasswordRecovered)
            {
                recovered++;
            }
        }
        await repository.SaveGroupAsync(group).ConfigureAwait(false);

        return new SessionImportOutcome
        {
            Imported = toImport.Count,
            PasswordsRecovered = recovered,
            GroupId = group.Id
        };
    }
}
