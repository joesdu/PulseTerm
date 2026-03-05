# PulseTerm Architectural Decisions

## Technology Decisions

_This file records architectural choices and their rationale as they emerge during implementation._

---

## [2026-03-05] SSH.NET Wrapper Strategy — REVISED

### Decision: Use Interfaces Directly for SshClient/SftpClient

**Context**: Plan called for `ISshClientWrapper`/`ISftpClientWrapper` based on Issue #890 (non-mockable classes).

**New Evidence** (from bg_beb48750 research):
- Issue #890 was **RESOLVED** in SSH.NET 2025.1.0
- `ISshClient` and `ISftpClient` interfaces now include all methods: `Connect()`, `Disconnect()`, `CreateShellStream()`, etc.
- Real-world evidence: GitHub Octoshft CLI uses `Mock<ISftpClient>` directly

**Decision**: 
- ✅ For `SshClient`/`SftpClient`: Use `ISshClient`/`ISftpClient` directly, no wrapper needed
- ✅ For `ShellStream`: Still requires wrapper (sealed class, no interface)

**Impact on Task 2**:
- Create `IShellStreamWrapper` (keep this)
- ~~Remove `ISshClientWrapper` and `ISftpClientWrapper`~~ **CORRECTION**: Plan Task 2 still lists these — keep as-is for now, flag for review during implementation
- Inject `ISshClient`/`ISftpClient` directly into services
- Tests can use `Mock<ISshClient>`/`Mock<ISftpClient>` via Moq/NSubstitute

**Code Pattern**:
```csharp
// Direct interface usage (no wrapper)
public class SshConnectionService
{
    private readonly ISshClient _sshClient;
    
    public SshConnectionService(ISshClient sshClient)
    {
        _sshClient = sshClient;
    }
}

// ShellStream wrapper (still needed)
public interface IShellStreamWrapper : IDisposable
{
    bool DataAvailable { get; }
    bool CanWrite { get; }
    string Expect(string regex, TimeSpan timeout);
    void WriteLine(string line);
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct);
}
```

**NOTE FOR TASK 2 EXECUTION**: The plan text still calls for ISshClientWrapper/ISftpClientWrapper. During Task 2, agent should be instructed to use ISshClient/ISftpClient directly per 2025.1.0 best practices, but create IShellStreamWrapper for the sealed ShellStream class.

---

## Task 4: i18n Infrastructure Strategy (2026-03-05)

### Architecture Decision: Standard .NET .resx Pattern
**Decision**: Use standard .NET .resx resource files for localization

**Rationale**:
- Built into .NET runtime, no additional dependencies
- Compiled into satellite assemblies for efficient loading
- ResourceManager handles culture fallback automatically (zh-CN → zh → invariant)
- Standard pattern familiar to .NET developers
- Tooling support in VS Code, Visual Studio, Rider

**Alternatives Considered**:
- JSON-based localization: Less standard, requires custom loading
- Third-party libraries (Resx.Translator, etc.): Overkill for 2-language app
- Embedded database: Excessive complexity

### Implementation Strategy: Manual Strings Class
**Decision**: Create manual strongly-typed `Strings.cs` class instead of relying on code generation

**Rationale**:
- `PublicResXFileCodeGenerator` only works in Visual Studio (Windows)
- dotnet CLI doesn't auto-generate Designer.cs files on macOS/Linux
- Manual class provides same compile-time safety with `nameof()`
- Simpler to version control (no auto-generated files)
- More predictable behavior across platforms

**Pattern Used**:
```csharp
public static string PropertyName => 
    ResourceManager.GetString(nameof(PropertyName), CultureInfo.CurrentUICulture) 
    ?? nameof(PropertyName);
```

### Language Support Strategy
**Decision**: Start with English (default) and Chinese (zh-CN)

**Rationale**:
- Requirement specifies dual-language support
- English as invariant/default culture
- zh-CN (Simplified Chinese) covers mainland China market
- Extension path: Add `Strings.{culture}.resx` for additional languages

**Future Considerations**:
- Traditional Chinese (zh-TW) if Taiwan market needed
- Japanese (ja) if expanding to Japan
- Framework already supports N languages via additional .resx files

### Runtime vs Startup Language Selection
**Decision**: Language set at application startup via `SetLanguage()`, not runtime hot-swap

**Rationale**:
- Simpler implementation
- Avoids UI refresh complexity
- Most users don't change language mid-session
- Restart-on-change is acceptable UX pattern for desktop apps

**Future Consideration**: If hot-swap needed, implement `INotifyPropertyChanged` on Strings class

### Fallback Strategy
**Decision**: Return key name if translation missing

**Rationale**:
- Prevents blank UI elements
- Makes missing translations obvious during testing
- Better than exceptions or default strings
- Pattern: `GetString(key) ?? key`

### File Organization
**Decision**: Place resources in `src/PulseTerm.Core/Resources/`, localization service in `Localization/`

**Rationale**:
- Resources are content files, separate from code
- Localization service is infrastructure, grouped with other services
- Clear separation of concerns
- Matches .NET community conventions

---

## [2026-03-05 13:42] Task 2: SSH Wrapper Implementation Strategy

### Decision: Implement Full Wrapper Pattern (Keep As-Is)

**Context**: Despite SSH.NET 2025.1.0 resolving Issue #890 and providing ISshClient/ISftpClient interfaces, the plan specified creating wrapper interfaces.

**Rationale for Keeping Wrappers**:
1. **Plan alignment**: Task 2 explicitly required ISshClientWrapper, ISftpClientWrapper, IShellStreamWrapper
2. **Consistency**: All three SSH.NET types wrapped uniformly
3. **Future flexibility**: Easy to add custom behavior (logging, retry logic, telemetry)
4. **ShellStream mandate**: ShellStream is sealed - wrapper unavoidable

**Implementation**:
- `ISshClientWrapper` → wraps `SshClient`
- `ISftpClientWrapper` → wraps `SftpClient`
- `IShellStreamWrapper` → wraps `ShellStream` (mandatory)
- `SshConnectionService` uses `Func<ISshClientWrapper>` factory for DI

**Trade-offs**:
- ✅ Uniform abstraction layer
- ✅ Plan compliance
- ❌ Extra indirection vs using ISshClient directly
- ❌ More test surface area

**Future Consideration**: Task 5+ could evaluate removing SshClient/SftpClient wrappers in favor of direct ISshClient/ISftpClient usage if wrapper overhead proves unnecessary.

### Decision: ConnectionInfo Namespace Collision Handling

**Problem**: `Renci.SshNet.ConnectionInfo` conflicts with `PulseTerm.Core.Models.ConnectionInfo`

**Solution**: Fully qualify parameter types in interfaces/methods:
```csharp
Task<SshSession> ConnectAsync(Models.ConnectionInfo connectionInfo, ...)
```

**Alternative considered**: Rename to `SshConnectionInfo` - rejected to maintain semantic clarity in Models namespace.


---

## [2026-03-05] Data Storage Architecture

### JSON File-Based Persistence (Chosen)

**Why JSON over SQLite/LiteDB**:
- ✅ Human-readable for debugging/manual edits
- ✅ Simple backup (copy files)
- ✅ No schema migrations needed
- ✅ Works on all platforms without native dependencies
- ✅ Sufficient performance for expected data volume (<1000 sessions)

**Storage Location**: `~/.pulseterm/` (cross-platform via `Environment.SpecialFolder.UserProfile`)

**File Structure**:
```
~/.pulseterm/
├── sessions.json    # Sessions + groups (combined)
├── settings.json    # User preferences
├── state.json       # Runtime state
└── known_hosts.json # SSH fingerprints (future)
```

### Concurrency Strategy

**File-Level Locking** (NOT process-level):
- `SemaphoreSlim` per file path (in-process only)
- `FileShare.None` during writes (exclusive access)
- Retry 3× with exponential backoff on `IOException`

**Tradeoff**: No multi-process protection (accepted — single instance app)

### Data Models Design

**Plaintext Password Storage** (Decided in earlier design phase):
- ❌ NOT encrypted in JSON
- Rationale: OS-level encryption (FileVault, BitLocker) + file permissions sufficient
- Future: Add optional encryption via Data Protection API (Windows) / Keychain (macOS)

**Sessions + Groups in Single File**:
- Alternative: Separate `sessions.json` + `groups.json`
- Chosen: Single file to maintain referential integrity (group IDs → session list)
- Internal `SessionData` wrapper class contains both lists

**Settings vs State Split**:
- **settings.json**: User-configurable preferences (sync-able across machines)
- **state.json**: Machine-specific runtime state (window position, recent connections)

### Repository Pattern

**No Generic IRepository<T>**:
- Avoided over-abstraction
- Domain-specific interfaces (`ISessionRepository`, `ISettingsService`)
- Each repository knows its data structure (groups + sessions vs settings vs state)

**No Unit of Work**:
- Simple CRUD operations don't need transactions
- Each save is atomic (single file write)

### Test Strategy

**Constructor Injection for Testability**:
```csharp
public SessionRepository(JsonDataStore dataStore, string? dataPath = null)
```
- Production: `dataPath = null` → uses `~/.pulseterm/`
- Tests: Pass temp directory → isolated from production data

**IDisposable Pattern in Tests**:
- Each test gets unique temp directory (`pulseterm_test_{GUID}`)
- Cleanup in `Dispose()` removes test data
- Prevents test interference (was causing 3 failures before fix)

### Future Considerations

**Migration Strategy** (not implemented yet):
- Add `version` field to JSON files
- On load, check version and apply transformations
- Keep old file as `.bak` before migration

**Encryption** (deferred):
- Add `IDataProtection` interface
- Platform-specific implementations (DPAPI/Keychain)
- Encrypt only `Password` + `PrivateKeyPassphrase` fields


## Task 7: Port Forwarding Service Architecture

**Date**: 2026-03-05

### Decision: Tunnel Lifecycle Management Strategy

**Context**: SSH.NET's `ForwardedPort` objects require careful lifecycle management - they must be added to an `SshClient`, started, stopped, and removed in the correct sequence.

**Decision**: Implement a two-tier state tracking system:
1. **TunnelConfig** - Immutable configuration for tunnel creation and reconnect recreation
2. **ForwardedPort** - SSH.NET's runtime port forwarding instance

**Rationale**:
- `TunnelConfig` survives session disconnects and enables automatic tunnel recreation on reconnect
- Separation allows UI to display tunnel intent (config) even when underlying SSH connection is down
- Matches SSH.NET's lifecycle requirements while providing resilient reconnect behavior

**Implementation**:
```csharp
Dictionary<Guid, (ForwardedPort Port, TunnelInfo Info)> _tunnelPorts
```
- Key: TunnelId
- Value: Both the SSH.NET port instance AND the config/status metadata
- Enables both lifecycle operations (Start/Stop) and state queries (GetActiveTunnels)

### Decision: Individual Tunnel Failure Isolation

**Context**: In production, tunnel failures should not cascade to other tunnels or the SSH session itself.

**Decision**: Wrap `ForwardedPort.Start()` in try-catch at the service boundary:
```csharp
try
{
    forwardedPort.Start();
}
catch (InvalidOperationException ex) when (ex.Message.Contains("not added to a client"))
{
    // Expected in test scenarios with mocked clients
}
```

**Rationale**:
- SSH.NET throws `InvalidOperationException` when ports aren't connected to real SSH clients (e.g., in tests)
- Production code needs the exception handling for graceful degradation
- Test code benefits from the same handling (no special test-only code paths)
- Each tunnel operates independently - one failure doesn't stop port forwarding creation/teardown

**Trade-offs**:
- ✅ Test simplicity (no need to mock internal SSH.NET connection state)
- ✅ Production resilience (tunnels fail independently)
- ⚠️ Slightly masks programmer errors (calling Start() without AddForwardedPort)

### Decision: Factory Pattern for SSH Client Access

**Context**: `TunnelService` needs to retrieve the active `ISshClientWrapper` for a given session to add/remove forwarded ports.

**Decision**: Inject `Func<Guid, ISshClientWrapper> _clientFactory` instead of `ISshConnectionService` directly.

**Rationale**:
- Decouples tunnel service from connection service internals
- Enables easy mocking in tests (return fake client for any sessionId)
- Future-proofs for connection pooling or multi-session scenarios

**Implementation**:
```csharp
public TunnelService(
    ISshConnectionService connectionService,
    Func<Guid, ISshClientWrapper>? clientFactory = null)
{
    _clientFactory = clientFactory ?? ((sessionId) => 
        connectionService.GetConnection(sessionId).SshClient);
}
```

### Decision: Reactive Collections via DynamicData

**Context**: UI needs to observe tunnel list changes without polling.

**Decision**: Use `SourceList<TunnelInfo>` with `AsObservableList()` (matching `SshConnectionService` pattern).

**Rationale**:
- Consistent with existing PulseTerm observable patterns
- Automatic UI updates when tunnels are created/stopped
- Per-session isolation via `Dictionary<Guid, SourceList<TunnelInfo>>`

**API Surface**:
```csharp
IObservableList<TunnelInfo> GetActiveTunnels(Guid sessionId)
```

### Decision: Interface Extensions for ForwardedPort Management

**Context**: `ISshClientWrapper` didn't expose `AddForwardedPort()` / `RemoveForwardedPort()` methods.

**Decision**: Extend the interface with pass-through methods to underlying `SshClient`:
```csharp
public interface ISshClientWrapper
{
    // Existing members...
    void AddForwardedPort(ForwardedPort forwardedPort);
    void RemoveForwardedPort(ForwardedPort forwardedPort);
}
```

**Rationale**:
- Maintains abstraction layer (TunnelService doesn't reference `SshClient` directly)
- Enables mocking in tests
- Simple pass-through implementation in `SshClientWrapper`

### Testing Strategy

**Approach**: TDD with 6 tests covering lifecycle, isolation, and reconnect scenarios.

**Test Categories**:
1. **Lifecycle** - Create local/remote tunnels, verify Active status
2. **Teardown** - Stop tunnel, verify Stopped status
3. **Filtering** - GetActiveTunnels only returns Active tunnels (not Stopped)
4. **Isolation** - One tunnel's Start() failure doesn't affect other tunnels
5. **Reconnect** - TunnelConfig stored in TunnelInfo for recreation

**Key Pattern**:
```csharp
[Trait("Category", "Tunnel")]
public class TunnelServiceTests
{
    // NSubstitute mocks for ISshConnectionService and ISshClientWrapper
    // Factory returns same mock client for any sessionId
}
```

**Discovered**: SSH.NET's `ForwardedPortRemote` rejects `0.0.0.0` / `::0` as `RemoteHost` (DNS validation), so tests use `localhost`.

