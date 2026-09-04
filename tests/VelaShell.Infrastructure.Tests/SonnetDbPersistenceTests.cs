using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
public sealed class SonnetDbPersistenceTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly AesSecretProtector _protector;
    private readonly string _testDirectory;

    public SonnetDbPersistenceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_sndbtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _protector = new(Path.Combine(_testDirectory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task SessionRepository_SaveAndLoad_RoundTrips()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var group = new ServerGroup { Name = "生产环境", SortOrder = 1 };
        var profile = new SessionProfile
        {
            Name = "web-prod-01",
            Host = "192.168.1.100",
            Port = 22,
            Username = "root",
            Password = "s3cret",
            GroupId = group.Id,
        };
        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(profile);
        List<ServerGroup> groups = await repo.GetAllGroupsAsync();
        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();
        Assert.HasCount(1, groups);
        Assert.AreEqual("生产环境", groups[0].Name);
        Assert.HasCount(1, sessions);
        Assert.AreEqual("web-prod-01", sessions[0].Name);
        Assert.AreEqual("s3cret", sessions[0].Password, "读出的密码应已解密");
        Assert.AreEqual(ConnectionType.SSH, sessions[0].ConnectionType);
        SessionProfile? single = await repo.GetSessionAsync(profile.Id);
        Assert.IsNotNull(single);
        Assert.AreEqual(profile.Host, single.Host);
    }

    [TestMethod]
    public async Task SessionRepository_SftpProfile_RoundTripsThroughProtectedDocument()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile
        {
            Name = "files",
            Host = "sftp.example.com",
            Username = "deploy",
            Password = "secret",
            ConnectionType = ConnectionType.SFTP,
        };

        await repo.SaveSessionAsync(profile);

        SessionProfile loaded = (await repo.GetSessionAsync(profile.Id))!;
        Assert.AreEqual(ConnectionType.SFTP, loaded.ConnectionType);
        Assert.AreEqual("secret", loaded.Password);
    }

    /// <summary>
    /// 「认证后执行命令」是**每条配置各一份**的设置,必须真的跟着这条配置落盘 ——
    /// 仓储写的是 <c>Protect</c> 出来的副本,漏抄一个字段的表现就是"存的时候好好的,
    /// 重开软件命令没了",而且不报任何错。
    /// </summary>
    [TestMethod]
    public async Task SessionRepository_PostAuthCommand_RoundTripsPerProfile()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var bastion = new SessionProfile
        {
            Name = "bastion",
            Host = "10.0.0.1",
            Username = "ops",
            PostAuthCommand = "sudo su -",
            PostAuthCommandDelaySeconds = 3,
        };
        var dev = new SessionProfile
        {
            Name = "dev",
            Host = "10.0.0.2",
            Username = "dev",
            PostAuthCommand = "tmux attach",
            PostAuthCommandDelaySeconds = 0,
        };

        await repo.SaveSessionAsync(bastion);
        await repo.SaveSessionAsync(dev);

        SessionProfile loadedBastion = (await repo.GetSessionAsync(bastion.Id))!;
        SessionProfile loadedDev = (await repo.GetSessionAsync(dev.Id))!;
        Assert.AreEqual("sudo su -", loadedBastion.PostAuthCommand);
        Assert.AreEqual(3, loadedBastion.PostAuthCommandDelaySeconds);
        // 两条配置各配各的,互不串味 —— 这正是它区别于全局「连接后执行命令」的地方。
        Assert.AreEqual("tmux attach", loadedDev.PostAuthCommand);
        Assert.AreEqual(0, loadedDev.PostAuthCommandDelaySeconds);
    }

    [TestMethod]
    public async Task SessionRepository_LegacyDocumentWithoutConnectionType_DefaultsToSsh()
    {
        var profileId = Guid.NewGuid();
        string json = $$"""{"id":"{{profileId}}","name":"legacy","host":"legacy.example.com","username":"ops","authMethod":0}""";
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ProfilesCollection, store =>
        {
            store.Upsert(profileId.ToString("D"), json);
            return null;
        });
        var repo = new SonnetDbSessionRepository(_engine, _protector);

        SessionProfile loaded = (await repo.GetSessionAsync(profileId))!;

        Assert.AreEqual(ConnectionType.SSH, loaded.ConnectionType);
    }

    [TestMethod]
    public async Task SessionRepository_Password_IsEncryptedAtRest()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var profile = new SessionProfile
        {
            Name = "n",
            Host = "h",
            Username = "u",
            Password = "plaintext-pass",
        };
        await repo.SaveSessionAsync(profile);
        string rawJson = await _engine.WithCollectionAsync(
            SonnetDbEngine.ProfilesCollection,
            store => store.Get(profile.Id.ToString("D"))!.Json
        );
        Assert.DoesNotContain("plaintext-pass", rawJson, "落盘 JSON 不应包含明文密码");
        Assert.Contains("enc1:", rawJson, "落盘密码应带加密前缀");
    }

    [TestMethod]
    public async Task SessionRepository_DeleteGroup_DetachesSessions()
    {
        var repo = new SonnetDbSessionRepository(_engine, _protector);
        var group = new ServerGroup { Name = "g" };
        var profile = new SessionProfile
        {
            Name = "n",
            Host = "h",
            Username = "u",
            GroupId = group.Id,
        };
        await repo.SaveGroupAsync(group);
        await repo.SaveSessionAsync(profile);
        await repo.DeleteGroupAsync(group.Id);
        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();
        Assert.IsNull(sessions.Single().GroupId);
        Assert.IsEmpty(await repo.GetAllGroupsAsync());
    }

    [TestMethod]
    public async Task SessionRepository_ImportsLegacyJson_Once()
    {
        string legacyFile = Path.Combine(_testDirectory, "sessions.json");
        var legacyId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            legacyFile,
            $$"""{"groups":[{"id":"{{Guid.NewGuid()}}","name":"旧分组","sortOrder":0,"sessions":[]}],"sessions":[{"id":"{{legacyId}}","name":"legacy","host":"1.2.3.4","port":22,"username":"ops","authMethod":0,"password":"oldpass","tags":[]}]}"""
        );
        var repo = new SonnetDbSessionRepository(_engine, _protector, legacyFile);
        List<SessionProfile> sessions = await repo.GetAllSessionsAsync();
        Assert.HasCount(1, sessions);
        Assert.AreEqual("legacy", sessions[0].Name);
        Assert.AreEqual("oldpass", sessions[0].Password);
        Assert.IsFalse(File.Exists(legacyFile), "导入成功后旧文件应被改名");
        Assert.IsTrue(File.Exists(legacyFile + ".migrated.bak"));
    }

    [TestMethod]
    public async Task SettingsService_SaveAndLoad_RoundTrips_AndRaisesEvent()
    {
        var service = new SonnetDbSettingsService(_engine);
        AppSettings? observed = null;
        service.SettingsSaved += s => observed = s;
        AppSettings settings = await service.GetSettingsAsync();
        settings.Theme = "light";
        settings.TerminalFontSize = 16;
        await service.SaveSettingsAsync(settings);
        AppSettings reloaded = await service.GetSettingsAsync();
        Assert.AreEqual("light", reloaded.Theme);
        Assert.AreEqual(16, reloaded.TerminalFontSize);
        Assert.IsNotNull(observed);
        AppState state = await service.GetStateAsync();
        state.LastActiveTab = "tab-1";
        await service.SaveStateAsync(state);
        Assert.AreEqual("tab-1", (await service.GetStateAsync()).LastActiveTab);
    }

    [TestMethod]
    public async Task SettingsService_PersistsNestedOptionGroups()
    {
        var service = new SonnetDbSettingsService(_engine);
        AppSettings settings = await service.GetSettingsAsync();
        settings.General.AutoReconnect = false;
        settings.General.ReconnectIntervalSeconds = 15;
        settings.Appearance.CursorColor = "#FF00FF";
        settings.TerminalBehavior.CopyOnSelect = true;
        settings.Transfer.ConflictPolicy = "rename";
        settings.Security.AlertWebhook = true;
        settings.Keys.DefaultKeyName = "id_ed25519";
        await service.SaveSettingsAsync(settings);
        AppSettings reloaded = await service.GetSettingsAsync();
        Assert.IsFalse(reloaded.General.AutoReconnect);
        Assert.AreEqual(15, reloaded.General.ReconnectIntervalSeconds);
        Assert.AreEqual("#FF00FF", reloaded.Appearance.CursorColor);
        Assert.IsTrue(reloaded.TerminalBehavior.CopyOnSelect);
        Assert.AreEqual("rename", reloaded.Transfer.ConflictPolicy);
        Assert.IsTrue(reloaded.Security.AlertWebhook);
        Assert.AreEqual("id_ed25519", reloaded.Keys.DefaultKeyName);
        Assert.HasCount(8, reloaded.Appearance.AnsiNormal);
    }

    [TestMethod]
    public async Task HostKeyService_TrustAndVerify_Works()
    {
        var service = new SonnetDbHostKeyService(_engine);
        Assert.AreEqual(
            HostKeyVerification.Unknown,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc")
        );
        await service.TrustHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc");
        Assert.AreEqual(
            HostKeyVerification.Trusted,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:abc")
        );
        Assert.AreEqual(
            HostKeyVerification.Changed,
            await service.VerifyHostKeyAsync("host1", 22, "ssh-ed25519", "SHA256:different")
        );
        await service.RemoveKnownHostAsync("host1", 22);
        Assert.IsEmpty(await service.GetKnownHostsAsync());
    }

    [TestMethod]
    public async Task RecentConnections_RecordAndQuery_DedupesAndOrdersByTimeDesc()
    {
        var service = new SonnetDbRecentConnectionService(_engine);
        var profileId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await service.RecordAsync(
            new()
            {
                ProfileId = profileId,
                Name = "web-prod-01",
                GroupName = "生产环境",
                Host = "192.168.1.100",
                Port = 22,
                Username = "root",
                ConnectionType = ConnectionType.SFTP,
                ConnectedAt = now.AddHours(-2),
                Success = true,
                DurationMs = 120,
            }
        );
        await service.RecordAsync(
            new()
            {
                ProfileId = profileId,
                Name = "web-prod-01",
                GroupName = "生产环境",
                Host = "192.168.1.100",
                Port = 22,
                Username = "root",
                ConnectionType = ConnectionType.SFTP,
                ConnectedAt = now.AddMinutes(-5),
                Success = true,
                DurationMs = 80,
            }
        );
        await service.RecordAsync(
            new()
            {
                Name = "deploy@staging.example.com",
                Host = "staging.example.com",
                Port = 22,
                Username = "deploy",
                ConnectedAt = now.AddDays(-1),
                Success = true,
                DurationMs = 300,
            }
        );
        await service.RecordAsync(
            new()
            {
                Name = "failed@10.0.0.9",
                Host = "10.0.0.9",
                Port = 22,
                Username = "x",
                ConnectedAt = now,
                Success = false,
                DurationMs = 5000,
            }
        );
        List<RecentConnectionEntry> recent = await service.GetRecentAsync(10);
        Assert.HasCount(2, recent, "同一配置去重、失败连接不出现");
        Assert.AreEqual("web-prod-01", recent[0].Name);
        Assert.AreEqual("生产环境", recent[0].GroupName);
        Assert.AreEqual(profileId, recent[0].ProfileId);
        Assert.AreEqual(ConnectionType.SFTP, recent[0].ConnectionType);
        Assert.AreEqual("deploy@staging.example.com", recent[1].Name);
        Assert.IsGreaterThan(recent[1].ConnectedAt, recent[0].ConnectedAt);
        await service.ClearAsync();
        Assert.IsEmpty(await service.GetRecentAsync(10));
    }

    [TestMethod]
    public async Task RecentConnections_InvalidConnectionType_FallsBackToSsh()
    {
        await _engine.WritePointAsync(
            SonnetDbEngine.ConnHistoryMeasurement,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["host"] = "legacy.example.com",
                ["username"] = "ops",
            },
            new Dictionary<string, SonnetDB.Model.FieldValue>
            {
                ["name"] = SonnetDB.Model.FieldValue.FromString("legacy"),
                ["port"] = SonnetDB.Model.FieldValue.FromLong(22),
                ["connection_type"] = SonnetDB.Model.FieldValue.FromLong(99),
                ["success"] = SonnetDB.Model.FieldValue.FromBool(true),
            }
        );

        RecentConnectionEntry entry = (await new SonnetDbRecentConnectionService(_engine).GetRecentAsync(1)).Single();

        Assert.AreEqual(ConnectionType.SSH, entry.ConnectionType);
    }

    [TestMethod]
    public async Task RecentConnections_ConnectionTypeBeyondInt32Range_FallsBackToSsh()
    {
        await _engine.WritePointAsync(
            SonnetDbEngine.ConnHistoryMeasurement,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["host"] = "overflow.example.com",
                ["username"] = "ops",
            },
            new Dictionary<string, SonnetDB.Model.FieldValue>
            {
                ["name"] = SonnetDB.Model.FieldValue.FromString("overflow"),
                ["port"] = SonnetDB.Model.FieldValue.FromLong(22),
                ["connection_type"] = SonnetDB.Model.FieldValue.FromLong((long)int.MaxValue + 1),
                ["success"] = SonnetDB.Model.FieldValue.FromBool(true),
            }
        );

        RecentConnectionEntry entry = (await new SonnetDbRecentConnectionService(_engine).GetRecentAsync(1)).Single();

        Assert.AreEqual(ConnectionType.SSH, entry.ConnectionType);
    }

    [TestMethod]
    public async Task AuditLog_WriteAndQuery_FiltersByCategory()
    {
        var service = new SonnetDbAuditLogService(_engine);
        await service.WriteAsync(
            new()
            {
                Category = "connection",
                Action = "connect",
                Detail = "root@h1",
            }
        );
        await service.WriteAsync(
            new()
            {
                Category = "settings",
                Action = "save",
                Detail = "theme",
            }
        );
        List<AuditEntry> all = await service.QueryAsync(10);
        Assert.HasCount(2, all);
        List<AuditEntry> connections = await service.QueryAsync(10, "connection");
        Assert.HasCount(1, connections);
        Assert.AreEqual("connect", connections[0].Action);
    }

    [TestMethod]
    public void SecretProtector_RoundTrips_And_PassesThroughLegacyPlaintext()
    {
        string? cipher = _protector.Protect("你好 password!");
        Assert.IsNotNull(cipher);
        Assert.StartsWith("enc1:", cipher);
        Assert.AreEqual("你好 password!", _protector.Unprotect(cipher));
        Assert.AreEqual("legacy-plain", _protector.Unprotect("legacy-plain"));
        Assert.AreEqual(cipher, _protector.Protect(cipher), "已加密的值不应二次加密");
        Assert.IsNull(_protector.Protect(null));
    }

    [TestMethod]
    public async Task AppDataStore_UpsertGetDelete_Works()
    {
        var store = new SonnetDbAppDataStore(_engine);
        await store.UpsertAsync("ui_config", "layout", new TestDoc { Value = "docked" });
        TestDoc? loaded = await store.GetAsync<TestDoc>("ui_config", "layout");
        Assert.IsNotNull(loaded);
        Assert.AreEqual("docked", loaded.Value);
        Assert.HasCount(1, await store.GetAllAsync<TestDoc>("ui_config"));
        await store.DeleteAsync("ui_config", "layout");
        Assert.IsNull(await store.GetAsync<TestDoc>("ui_config", "layout"));
    }

    [TestMethod]
    public async Task QuickCommands_V1Document_MigratesToV2AndCreatesBackup()
    {
        var store = new SonnetDbAppDataStore(_engine);
        await store.UpsertAsync(
            "quick_commands",
            "commands",
            new
            {
                marker = "preserved",
                commands = new[]
                {
                    new
                    {
                        id = Guid.Empty,
                        name = "first",
                        category = "",
                        commandText = "echo first",
                        description = "one",
                        isBuiltIn = false,
                    },
                    new
                    {
                        id = Guid.Empty,
                        name = "second",
                        category = "Ops",
                        commandText = "echo second",
                        description = "two",
                        isBuiltIn = false,
                    },
                    new
                    {
                        id = Guid.Empty,
                        name = "third",
                        category = "ops",
                        commandText = "echo third",
                        description = "three",
                        isBuiltIn = false,
                    },
                },
            }
        );
        var repository = new SonnetDbQuickCommandRepository(store);

        QuickCommandLoadResult result = await repository.LoadAsync();

        Assert.IsTrue(result.Migrated);
        Assert.AreEqual(QuickCommandData.CurrentSchemaVersion, result.Data.SchemaVersion);
        Assert.HasCount(3, result.Data.Commands);
        Assert.AreEqual(
            QuickCommandGroupCatalog.DefaultGroupId,
            result.Data.Commands.Single(command => command.Name == "first").GroupId
        );
        QuickCommandGroup[] opsGroups =
        [
            .. result.Data.Groups.Where(group =>
                string.Equals(group.Name, "Ops", StringComparison.OrdinalIgnoreCase)
            ),
        ];
        Assert.HasCount(1, opsGroups);
        Assert.IsTrue(
            result
                .Data.Commands.Where(command => command.Name is "second" or "third")
                .All(command => command.GroupId == opsGroups[0].Id)
        );
        Assert.IsTrue(result.Data.Commands.All(command => command.Id != Guid.Empty));
        LegacyQuickCommandDataProbe? backup = await store.GetAsync<LegacyQuickCommandDataProbe>(
            "quick_commands",
            "commands.v1.backup"
        );
        Assert.IsNotNull(backup);
        Assert.AreEqual("preserved", backup.Marker);

        Guid[] ids = [.. result.Data.Commands.Select(command => command.Id)];
        var secondRepository = new SonnetDbQuickCommandRepository(store);
        QuickCommandLoadResult secondLoad = await secondRepository.LoadAsync();
        Assert.AreSequenceEqual(
            ids, [.. secondLoad.Data.Commands.Select(command => command.Id)]
        );
    }

    [TestMethod]
    public async Task QuickCommands_LegacyJson_ImportsAsV2ThenRenamesFile()
    {
        string legacyPath = Path.Combine(_testDirectory, "quick-commands.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {"commands":[{"name":"legacy","category":"Custom","commandText":"uptime","description":"old"}]}
            """
        );
        var store = new SonnetDbAppDataStore(_engine);
        var repository = new SonnetDbQuickCommandRepository(store, legacyPath);

        QuickCommandLoadResult result = await repository.LoadAsync();

        Assert.IsTrue(result.Migrated);
        Assert.ContainsSingle(command => command.Name == "legacy", result.Data.Commands);
        Assert.IsFalse(File.Exists(legacyPath));
        Assert.IsTrue(File.Exists(legacyPath + ".migrated.bak"));
    }

    [TestMethod]
    public async Task QuickCommands_NewerSchema_IsNotOverwritten()
    {
        var store = new SonnetDbAppDataStore(_engine);
        await store.UpsertAsync(
            "quick_commands",
            "commands",
            new { schemaVersion = 99, marker = "keep" }
        );
        var repository = new SonnetDbQuickCommandRepository(store);

        QuickCommandLoadResult result = await repository.LoadAsync();

        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        VersionProbe? original = await store.GetAsync<VersionProbe>("quick_commands", "commands");
        Assert.IsNotNull(original);
        Assert.AreEqual(99, original.SchemaVersion);
        Assert.AreEqual("keep", original.Marker);
    }

    [TestMethod]
    public async Task QuickCommands_SyncV1_MigratesCategoriesAndIgnoresBuiltIns()
    {
        var store = new SonnetDbAppDataStore(_engine);
        var repository = new SonnetDbQuickCommandRepository(store);

        await repository.ApplySyncAsync(
            new()
            {
                SchemaVersion = 1,
                Commands =
                [
                    new()
                    {
                        Name = "remote",
                        Category = " Ops ",
                        CommandText = "uptime",
                        Description = "from v1",
                    },
                    new()
                    {
                        Name = "built in",
                        Category = "System",
                        CommandText = "systemctl status",
                        IsBuiltIn = true,
                    },
                ],
            }
        );

        QuickCommandData data = (await repository.LoadAsync()).Data;
        QuickCommand command = Assert.ContainsSingle(data.Commands);
        QuickCommandGroup group = data.Groups.Single(item => item.Id == command.GroupId);
        Assert.AreEqual("Ops", group.Name);
        Assert.AreEqual("remote", command.Name);
    }

    [TestMethod]
    public async Task QuickCommands_SyncV2_RoundTripsGroupsSortAndCompatibilityCategory()
    {
        var store = new SonnetDbAppDataStore(_engine);
        var repository = new SonnetDbQuickCommandRepository(store);
        var groupId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        await repository.ApplySyncAsync(
            new()
            {
                SchemaVersion = 2,
                Groups =
                [
                    new()
                    {
                        Id = groupId,
                        Name = "Deploy",
                        Kind = QuickCommandGroupKind.User,
                        SortOrder = 7,
                    },
                ],
                Commands =
                [
                    new()
                    {
                        Id = commandId,
                        GroupId = groupId,
                        Name = "release",
                        Category = "legacy ignored",
                        CommandText = "./release.sh",
                        SortOrder = 3,
                    },
                ],
            }
        );

        QuickCommandSyncData exported = await repository.ExportSyncAsync();

        Assert.AreEqual(2, exported.SchemaVersion);
        QuickCommandGroup group = exported.Groups.Single(item => item.Id == groupId);
        QuickCommandSyncItem command = exported.Commands.Single(item => item.Id == commandId);
        Assert.AreEqual(7, group.SortOrder);
        Assert.AreEqual(3, command.SortOrder);
        Assert.AreEqual(groupId, command.GroupId);
        Assert.AreEqual("Deploy", command.Category);
    }

    [TestMethod]
    public async Task QuickCommands_SyncV2_InvalidGroupFallsBackToDefault()
    {
        var repository = new SonnetDbQuickCommandRepository(new SonnetDbAppDataStore(_engine));
        await repository.ApplySyncAsync(
            new()
            {
                SchemaVersion = 2,
                Commands =
                [
                    new()
                    {
                        Id = Guid.NewGuid(),
                        GroupId = Guid.NewGuid(),
                        Name = "orphan",
                        CommandText = "pwd",
                    },
                ],
            }
        );

        QuickCommand command = Assert.ContainsSingle((await repository.LoadAsync()).Data.Commands);
        Assert.AreEqual(QuickCommandGroupCatalog.DefaultGroupId, command.GroupId);
    }

    [TestMethod]
    public async Task QuickCommands_SyncNewerSchema_RejectsWithoutOverwritingLocalData()
    {
        var repository = new SonnetDbQuickCommandRepository(new SonnetDbAppDataStore(_engine));
        await repository.SaveAsync(
            new()
            {
                Groups = QuickCommandGroupCatalog.CreateSystemGroups(),
                Commands = [new() { Name = "local", CommandText = "pwd" }],
            }
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ApplySyncAsync(new() { SchemaVersion = 99 })
        );

        Assert.AreEqual(
            "local",
            Assert.ContainsSingle((await repository.LoadAsync()).Data.Commands).Name
        );
    }

    [TestMethod]
    public async Task Engine_Reopen_PersistsDocumentsAndTimeSeries()
    {
        string dir = Path.Combine(_testDirectory, "reopen-db");
        using (var engine = new SonnetDbEngine(dir))
        {
            var repo = new SonnetDbSessionRepository(engine, _protector);
            await repo.SaveSessionAsync(
                new()
                {
                    Name = "persist-me",
                    Host = "h",
                    Username = "u",
                }
            );
            var recents = new SonnetDbRecentConnectionService(engine);
            await recents.RecordAsync(
                new()
                {
                    Name = "r1",
                    Host = "h",
                    Username = "u",
                    Success = true,
                }
            );
        }
        using (var engine = new SonnetDbEngine(dir))
        {
            var repo = new SonnetDbSessionRepository(engine, _protector);
            Assert.AreEqual("persist-me", (await repo.GetAllSessionsAsync()).Single().Name);
            var recents = new SonnetDbRecentConnectionService(engine);
            Assert.HasCount(1, await recents.GetRecentAsync(5));
        }
    }

    private sealed class TestDoc
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class LegacyQuickCommandDataProbe
    {
        public string Marker { get; set; } = string.Empty;

        public List<object> Commands { get; set; } = [];
    }

    private sealed class VersionProbe
    {
        public int SchemaVersion { get; set; }

        public string Marker { get; set; } = string.Empty;
    }
}
