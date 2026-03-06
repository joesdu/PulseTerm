using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using Xunit;

namespace PulseTerm.Core.Tests.Data;

[Trait("Category", "DataStore")]
public class SessionRepositoryTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _sessionsPath;
    private readonly JsonDataStore _dataStore;

    public SessionRepositoryTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pulseterm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _sessionsPath = Path.Combine(_testDirectory, "sessions.json");
        _dataStore = new JsonDataStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task GetAllGroupsAsync_FileDoesNotExist_ShouldReturnEmptyList()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);

        var groups = await repo.GetAllGroupsAsync();

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSessionAsync_NewSession_ShouldAddToList()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Server",
            Host = "192.168.1.100"
        };

        await repo.SaveSessionAsync(session);
        var retrieved = await repo.GetSessionAsync(session.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Test Server");
        retrieved.Host.Should().Be("192.168.1.100");
    }

    [Fact]
    public async Task SaveSessionAsync_ExistingSession_ShouldUpdate()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            Host = "192.168.1.100"
        };

        await repo.SaveSessionAsync(session);
        session.Name = "Updated";
        await repo.SaveSessionAsync(session);
        var retrieved = await repo.GetSessionAsync(session.Id);

        retrieved!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task GetSessionAsync_NonExistentId_ShouldReturnNull()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);

        var result = await repo.GetSessionAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteSessionAsync_ShouldRemoveSessionAndUpdateGroups()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var sessionId = Guid.NewGuid();
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Sessions = new List<Guid> { sessionId }
        };
        var session = new SessionProfile { Id = sessionId, Name = "Test" };

        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(session);
        await repo.DeleteSessionAsync(sessionId);

        var retrievedSession = await repo.GetSessionAsync(sessionId);
        var groups = await repo.GetAllGroupsAsync();

        retrievedSession.Should().BeNull();
        groups.First().Sessions.Should().NotContain(sessionId);
    }

    [Fact]
    public async Task SaveGroupAsync_NewGroup_ShouldAddToList()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Icon = "server",
            SortOrder = 1
        };

        await repo.SaveGroupAsync(group);
        var groups = await repo.GetAllGroupsAsync();

        groups.Should().ContainSingle();
        groups.First().Name.Should().Be("Production");
        groups.First().Icon.Should().Be("server");
    }

    [Fact]
    public async Task SaveGroupAsync_ExistingGroup_ShouldUpdate()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            SortOrder = 1
        };

        await repo.SaveGroupAsync(group);
        group.Name = "Updated";
        group.SortOrder = 5;
        await repo.SaveGroupAsync(group);
        var groups = await repo.GetAllGroupsAsync();

        groups.First().Name.Should().Be("Updated");
        groups.First().SortOrder.Should().Be(5);
    }

    [Fact]
    public async Task DeleteGroupAsync_ShouldRemoveGroupAndClearSessionGroupIds()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var groupId = Guid.NewGuid();
        var group = new ServerGroup { Id = groupId, Name = "Test" };
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Session",
            GroupId = groupId
        };

        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(session);
        await repo.DeleteGroupAsync(groupId);

        var groups = await repo.GetAllGroupsAsync();
        var retrievedSession = await repo.GetSessionAsync(session.Id);

        groups.Should().BeEmpty();
        retrievedSession!.GroupId.Should().BeNull();
    }

    [Fact]
    public async Task MultipleSessionsAndGroups_ShouldPersistCorrectly()
    {
        var repo = new SessionRepository(_dataStore, _sessionsPath);
        var group1 = new ServerGroup { Id = Guid.NewGuid(), Name = "Group1" };
        var group2 = new ServerGroup { Id = Guid.NewGuid(), Name = "Group2" };
        var session1 = new SessionProfile { Id = Guid.NewGuid(), Name = "Session1", GroupId = group1.Id };
        var session2 = new SessionProfile { Id = Guid.NewGuid(), Name = "Session2", GroupId = group2.Id };

        await repo.SaveGroupAsync(group1);
        await repo.SaveGroupAsync(group2);
        await repo.SaveSessionAsync(session1);
        await repo.SaveSessionAsync(session2);

        var groups = await repo.GetAllGroupsAsync();
        var retrievedSession1 = await repo.GetSessionAsync(session1.Id);
        var retrievedSession2 = await repo.GetSessionAsync(session2.Id);

        groups.Should().HaveCount(2);
        retrievedSession1!.GroupId.Should().Be(group1.Id);
        retrievedSession2!.GroupId.Should().Be(group2.Id);
    }
}
