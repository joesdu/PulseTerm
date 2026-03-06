# PulseTerm — Full-Featured SSH Terminal Client

## TL;DR

> **Quick Summary**: Build a complete, cross-platform SSH terminal management desktop application using .NET 8 + Avalonia UI 11.x + ReactiveUI, implementing ALL features from the PulseTerm Penpot design (sidebar session tree, multi-tab terminal, SFTP file browser, file transfer, tunnel management, quick commands, status bar).
> 
> **Deliverables**:
> - Cross-platform desktop app (Windows, macOS, Linux)
> - SSH terminal with VT100/xterm-256color emulation + custom scrollback
> - Session management with server groups, quick connect, recent connections
> - SFTP file browser with upload/download progress
> - SSH tunnel management (local + remote port forwarding)
> - Quick commands panel with search + categories
> - Dark + Light themes matching design tokens
> - Chinese (zh-CN) + English (en) i18n
> - Auto-update via Velopack
> 
> **Estimated Effort**: XL (14 phases, 50+ tasks)
> **Parallel Execution**: YES — 4 waves where applicable
> **Critical Path**: Scaffold → SSH Service → Terminal Spike → Core UI Shell → Terminal Integration → SFTP → Tunnels → Polish

---

## Context

### Original Request
Build a production-ready SSH terminal client (like Termius/FinalShell) matching the PulseTerm-zh.pen design file, using .NET 8 + Avalonia UI. All features from the design must be implemented.

### Interview Summary
**Key Discussions**:
- **Scope**: ALL features from design, not MVP/phased — full implementation
- **Tech stack**: .NET 8 LTS + Avalonia UI 11.x + ReactiveUI + SSH.NET
- **Platform**: Cross-platform (Windows + macOS + Linux)
- **Terminal**: SSH only, no local shell support
- **Persistence**: JSON files for session/config storage
- **Security**: Plaintext password storage initially (encryption later enhancement)
- **i18n/Themes**: Multi-language (zh-CN + en) + multi-theme (Dark + Light)
- **Auto-update**: Velopack (NOT Squirrel — Squirrel is Windows-only)
- **Testing**: TDD workflow (tests first, then implement)
- **MVVM**: ReactiveUI (native Avalonia integration, reactive streams ideal for SSH/terminal)

**Research Findings**:
- SSH.NET 2025.1.0: async/await, SFTP, port forwarding, ShellStream — BUT ShellStream disposal bug on large output (Issue #1762), NOT mockable (Issue #890)
- AvaloniaTerminal 1.0.0-alpha.7: alpha quality, 23 stars, single maintainer — NO scrollback, incomplete resize, no mouse support
- XtermSharp: basic CJK wcwidth support, but grapheme clusters missing
- Avalonia cross-platform: macOS keyboard freeze issue (#18482), Linux Wayland incomplete
- v2rayN reference project: ReactiveUI + Avalonia, 29.9k stars

### Metis Review
**Identified Gaps (addressed)**:
- Terminal emulator is alpha with no scrollback → Phase 0 spike + custom scrollback buffer + ITerminalEmulator abstraction
- SSH.NET not mockable → ISshClientWrapper/ISftpClientWrapper thin interfaces
- UTF-8 boundary splitting on ShellStream → Utf8StreamDecoder implementation
- Private key auth missing from requirements → Added (RSA/ED25519/ECDSA)
- Host key verification not discussed → Trust-on-first-use (TOFU) + fingerprint storage
- Status bar CPU/Memory/Network requires separate SSH subsystem → Descoped to connection info + latency only
- ShellStream disposal on large output → Watchdog + stream recovery layer
- CJK rendering validation needed → Included in terminal spike acceptance criteria

---

## Work Objectives

### Core Objective
Build a complete SSH terminal management desktop application matching the PulseTerm Penpot design, with robust terminal emulation, session management, SFTP file operations, tunnel management, and cross-platform packaging.

### Concrete Deliverables
- `src/PulseTerm.App/` — Avalonia UI application project
- `src/PulseTerm.Core/` — Core logic (SSH, models, services)
- `src/PulseTerm.Terminal/` — Terminal emulation bridge + custom scrollback
- `tests/PulseTerm.Core.Tests/` — Core unit tests
- `tests/PulseTerm.Terminal.Tests/` — Terminal tests
- `tests/PulseTerm.App.Tests/` — UI/ViewModel tests
- Cross-platform binaries via `dotnet publish` for win-x64, osx-arm64, linux-x64
- Velopack auto-update integration

### Definition of Done
- [ ] `dotnet build src/PulseTerm.sln --warnaserror` → Build succeeded, 0 warnings, 0 errors
- [ ] `dotnet test tests/ --logger "console;verbosity=normal"` → All tests pass
- [ ] `dotnet publish src/PulseTerm.App -r win-x64 --self-contained -c Release` → Produces runnable binary
- [ ] `dotnet publish src/PulseTerm.App -r osx-arm64 --self-contained -c Release` → Produces runnable binary
- [ ] `dotnet publish src/PulseTerm.App -r linux-x64 --self-contained -c Release` → Produces runnable binary
- [ ] All 7 UI modules from design file are implemented and functional
- [ ] Dark + Light themes match design tokens
- [ ] zh-CN + en language support working

### Must Have
- SSH connection with password + private key authentication
- Terminal emulation (VT100/xterm-256color) with scrollback
- Multi-tab terminal sessions
- Session tree with server groups
- SFTP file browser with upload/download
- SSH tunnel management (local + remote port forwarding)
- Quick commands panel
- Host key trust-on-first-use verification
- Dark theme matching design exactly
- Cross-platform builds

### Must NOT Have (Guardrails)
- ❌ Local shell / PowerShell / bash terminal support
- ❌ SSH Agent forwarding, Jump hosts, GSSAPI, Certificate-based auth
- ❌ Sixel graphics, OSC hyperlinks, mouse reporting in terminal
- ❌ Drag-and-drop file transfer from OS file manager
- ❌ SCP protocol (SFTP only)
- ❌ Dynamic SOCKS port forwarding
- ❌ Remote system metrics (CPU/Memory/Network) in status bar — connection info + latency only
- ❌ Custom user theme editor or per-session themes
- ❌ Runtime language hot-switch (set at startup via settings)
- ❌ Quick command templates with variables, scheduling, or history sharing
- ❌ `ISshProvider`, `ISshConnectionFactory`, `AbstractSshSession` over-abstractions
- ❌ `BaseViewModel`, `BaseService`, `BaseModel` premature base classes — `ReactiveObject` is sufficient
- ❌ `IRepository<T>`, Unit of Work patterns — single `JsonDataStore` with `LoadAsync<T>/SaveAsync<T>`
- ❌ `IOptions<T>`, hot-reload config — `System.Text.Json` to/from POCO
- ❌ XML doc comments on obvious properties/methods
- ❌ NuGet packages beyond agreed stack without justification
- ❌ Customizable keybindings — hardcoded: terminal gets raw keys, Ctrl+Shift+C/V for copy/paste in terminal

---

## Verification Strategy

> **UNIVERSAL RULE: ZERO HUMAN INTERVENTION**
>
> ALL tasks in this plan MUST be verifiable WITHOUT any human action.
> This is NOT conditional — it applies to EVERY task, regardless of test strategy.
>
> **FORBIDDEN** — acceptance criteria that require:
> - "User manually tests..." / "用户手动测试..."
> - "User visually confirms..." / "用户目视确认..."
> - "User interacts with..." / "用户直接操作..."
> - ANY step where a human must perform an action
>
> **ALL verification is executed by the agent** using tools (Playwright, interactive_bash, curl, etc.). No exceptions.

### Test Decision
- **Infrastructure exists**: NO (greenfield project)
- **Automated tests**: YES (TDD — tests first)
- **Framework**: xUnit + Moq/NSubstitute (unit), Avalonia.Headless.XUnit (UI), Docker SSH server (integration)
- **Agent-Executed QA**: ALWAYS (mandatory for all tasks)

### TDD Workflow

Each TODO follows RED-GREEN-REFACTOR:
1. **RED**: Write failing test first → `dotnet test` → FAIL
2. **GREEN**: Implement minimum code to pass → `dotnet test` → PASS
3. **REFACTOR**: Clean up while keeping green → `dotnet test` → PASS

### Test Infrastructure Setup (Task 1)
- xUnit test projects created for each src project
- Moq/NSubstitute for mocking
- Avalonia.Headless.XUnit for headless UI tests
- Docker Compose with SSH server for integration tests

### SSH.NET Mockability Pattern
SSH.NET classes are NOT mockable (non-virtual methods, Issue #890). ALL SSH interactions go through thin wrapper interfaces:
```
ISshClientWrapper → wraps SshClient (Connect, CreateShellStream, CreateForwardedPort)
ISftpClientWrapper → wraps SftpClient (ListDirectory, UploadFile, DownloadFile)
```

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately):
└── Task 1: Project scaffold + build system + test infrastructure

Wave 2 (After Wave 1):
├── Task 2: SSH.NET wrapper interfaces + connection service + tests
├── Task 3: JSON data store + session/config models + tests
└── Task 4: i18n infrastructure (.resx setup)

Wave 3 (After Task 2):
├── Task 5: Terminal spike — SSH.NET ↔ AvaloniaTerminal bridge (CRITICAL)
├── Task 6: SFTP service + tests (depends on Task 2 wrappers)
└── Task 7: Port forwarding service + tests (depends on Task 2 wrappers)

Wave 4 (After Task 5 spike passes):
├── Task 8: Theme system + design tokens from .pen file
└── Task 9: Custom scrollback buffer implementation

Wave 5 (After Wave 4):
├── Task 10: Core UI shell — MainWindow + sidebar + tab bar
└── Task 11: Host key TOFU service + tests

Wave 6 (After Wave 5):
├── Task 12: Terminal tab integration (terminal control + toolbar)
├── Task 13: Session tree ViewModel + quick connect
└── Task 14: Settings service + connection profiles

Wave 7 (After Wave 6):
├── Task 15: SFTP file browser panel
├── Task 16: File transfer panel with progress
├── Task 17: Tunnel management panel
└── Task 18: Quick commands panel

Wave 8 (After Wave 7):
├── Task 19: Status bar
├── Task 20: Light theme
└── Task 21: Keyboard shortcuts + copy/paste

Wave 9 (Final):
├── Task 22: Cross-platform packaging + Velopack auto-update
├── Task 23: Integration testing + cross-platform QA
└── Task 24: Polish + edge cases + final QA
```

### Dependency Matrix

| Task | Depends On | Blocks | Can Parallelize With |
|------|-----------|--------|---------------------|
| 1 | None | 2,3,4 | None (must be first) |
| 2 | 1 | 5,6,7 | 3, 4 |
| 3 | 1 | 10,13,14 | 2, 4 |
| 4 | 1 | 20 | 2, 3 |
| 5 | 2 | 8,9,12 | 6, 7 |
| 6 | 2 | 15,16 | 5, 7 |
| 7 | 2 | 17 | 5, 6 |
| 8 | 5 | 10 | 9 |
| 9 | 5 | 12 | 8 |
| 10 | 3,8 | 12,13,15-19 | 11 |
| 11 | 1 | 14 | 10 |
| 12 | 9,10 | 19,21 | 13, 14 |
| 13 | 3,10 | None | 12, 14 |
| 14 | 3,11 | None | 12, 13 |
| 15 | 6,10 | None | 16, 17, 18 |
| 16 | 6,10 | None | 15, 17, 18 |
| 17 | 7,10 | None | 15, 16, 18 |
| 18 | 10 | None | 15, 16, 17 |
| 19 | 12 | None | 20, 21 |
| 20 | 4,8 | None | 19, 21 |
| 21 | 12 | None | 19, 20 |
| 22 | All prev | 23 | None |
| 23 | 22 | 24 | None |
| 24 | 23 | None | None |

### Agent Dispatch Summary

| Wave | Tasks | Recommended Agents |
|------|-------|-------------------|
| 1 | 1 | task(category="unspecified-high", load_skills=["git-master"]) |
| 2 | 2, 3, 4 | 3× parallel task(category="unspecified-high") |
| 3 | 5, 6, 7 | task(category="deep") for spike; 2× task(category="unspecified-high") |
| 4 | 8, 9 | task(category="visual-engineering", load_skills=["frontend-ui-ux"]) + task(category="ultrabrain") |
| 5 | 10, 11 | task(category="visual-engineering", load_skills=["frontend-ui-ux"]) + task(category="unspecified-low") |
| 6 | 12, 13, 14 | 3× parallel task(category="unspecified-high") |
| 7 | 15-18 | 4× parallel task(category="visual-engineering", load_skills=["frontend-ui-ux"]) |
| 8 | 19-21 | 3× parallel mixed categories |
| 9 | 22-24 | Sequential task(category="deep") |

---

## TODOs

> Implementation + Test = ONE Task. Never separate.
> EVERY task MUST have: Recommended Agent Profile + Parallelization info.

- [ ] 1. Project Scaffold + Build System + Test Infrastructure

  **What to do**:
  - Create .NET 8 solution `PulseTerm.sln` in `src/`
  - Create projects:
    - `src/PulseTerm.App/` — Avalonia application (net8.0, AvaloniaApplication output)
    - `src/PulseTerm.Core/` — Class library (net8.0)
    - `src/PulseTerm.Terminal/` — Class library (net8.0)
    - `tests/PulseTerm.Core.Tests/` — xUnit test project
    - `tests/PulseTerm.Terminal.Tests/` — xUnit test project
    - `tests/PulseTerm.App.Tests/` — xUnit test project with Avalonia.Headless.XUnit
  - Install NuGet packages:
    - PulseTerm.App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.ReactiveUI`, `Microsoft.Extensions.DependencyInjection`
    - PulseTerm.Core: `SSH.NET`, `System.Text.Json`, `ReactiveUI`, `Microsoft.Extensions.Logging.Abstractions`
    - PulseTerm.Terminal: `Avalonia`, `ReactiveUI`
    - Test projects: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `NSubstitute`, `FluentAssertions`
    - PulseTerm.App.Tests: additionally `Avalonia.Headless.XUnit`
  - Configure project references: App → Core, Terminal; Tests → corresponding src projects
  - Create `docker-compose.test.yml` with `linuxserver/openssh-server` for SSH integration tests
  - Create `.editorconfig` with C# coding standards
  - Create `.gitignore` for .NET projects
  - Add one smoke test per test project to verify infrastructure works
  - RED: Write smoke tests → FAIL (projects don't exist yet or empty)
  - GREEN: Create all projects, install packages → `dotnet test` → PASS
  - REFACTOR: Clean up, verify build warnings are zero

  **Must NOT do**:
  - Do NOT add packages beyond the listed set
  - Do NOT create any ViewModels, Services, or business logic — scaffold only
  - Do NOT create `BaseViewModel` or any abstract base classes

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Project scaffolding requires careful .NET solution setup, multiple projects, correct package versions
  - **Skills**: [`git-master`]
    - `git-master`: Initial commit with proper .gitignore setup
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: No UI work in this task
    - `playwright`: No browser testing needed

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 1 (solo — foundation for everything)
  - **Blocks**: Tasks 2, 3, 4 (all depend on project structure existing)
  - **Blocked By**: None (can start immediately)

  **References**:

  **Pattern References**:
  - No existing code — greenfield project

  **API/Type References**:
  - Avalonia project template: `dotnet new avalonia.app` for reference structure
  - ReactiveUI integration: `Avalonia.ReactiveUI` package wires up `IViewModelHost`

  **External References**:
  - Avalonia getting started: https://docs.avaloniaui.net/docs/getting-started/
  - Avalonia ReactiveUI: https://docs.avaloniaui.net/docs/concepts/reactiveui/
  - xUnit getting started: https://xunit.net/docs/getting-started/netcore/cmdline
  - Avalonia.Headless.XUnit: https://docs.avaloniaui.net/docs/concepts/headless/headless-xunit
  - SSH.NET NuGet: https://www.nuget.org/packages/SSH.NET
  - Docker linuxserver/openssh-server: https://hub.docker.com/r/linuxserver/openssh-server

  **WHY Each Reference Matters**:
  - Avalonia docs: Correct project structure, App.axaml setup, Program.cs entry point pattern
  - ReactiveUI integration: Must use `UseReactiveUI()` in AppBuilder, not manual wiring
  - Avalonia.Headless: Required for CI-compatible UI tests without display server

  **Acceptance Criteria**:

  - [ ] `dotnet build src/PulseTerm.sln --warnaserror` → Build succeeded, 0 warnings, 0 errors
  - [ ] `dotnet test tests/ --logger "console;verbosity=normal"` → 3+ smoke tests pass (one per test project)
  - [ ] Solution has 3 src projects and 3 test projects with correct references
  - [ ] All listed NuGet packages installed at latest stable versions
  - [ ] `.gitignore` excludes bin/, obj/, .vs/, *.user
  - [ ] `docker-compose.test.yml` exists with openssh-server service definition

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Solution builds and tests pass
    Tool: Bash
    Preconditions: .NET 8 SDK installed
    Steps:
      1. Run: dotnet build src/PulseTerm.sln --warnaserror
      2. Assert: exit code 0, output contains "Build succeeded", "0 Warning(s)", "0 Error(s)"
      3. Run: dotnet test tests/ --logger "console;verbosity=normal"
      4. Assert: exit code 0, output contains "Passed!" for each test project
      5. Run: dotnet publish src/PulseTerm.App -r osx-arm64 --self-contained -c Release --no-build || dotnet publish src/PulseTerm.App -r osx-arm64 --self-contained -c Release
      6. Assert: publish directory contains PulseTerm.App executable
    Expected Result: Clean build, all smoke tests pass, publishable binary produced
    Evidence: Build + test output captured

  Scenario: Docker compose validates
    Tool: Bash
    Preconditions: Docker installed
    Steps:
      1. Run: docker compose -f docker-compose.test.yml config
      2. Assert: exit code 0, output contains service definition for openssh-server
    Expected Result: Valid docker-compose configuration
    Evidence: docker compose config output captured
  ```

  **Commit**: YES
  - Message: `chore: scaffold PulseTerm solution with test infrastructure`
  - Files: `src/`, `tests/`, `docker-compose.test.yml`, `.editorconfig`, `.gitignore`
  - Pre-commit: `dotnet build src/PulseTerm.sln --warnaserror && dotnet test tests/`

---

- [ ] 2. SSH Connection Service + Wrapper Interfaces + Tests

  **What to do**:
  - Create `ISshClientWrapper` interface in `PulseTerm.Core/Ssh/`:
    - `ConnectAsync(CancellationToken)`, `Disconnect()`, `IsConnected`, `CreateShellStream(...)`, `AddForwardedPort(...)`, `Dispose()`
  - Create `SshClientWrapper` implementing `ISshClientWrapper` wrapping `SSH.NET.SshClient`
  - Create `ISftpClientWrapper` interface:
    - `ConnectAsync(CancellationToken)`, `ListDirectoryAsync(path)`, `UploadFileAsync(stream, remotePath, progress)`, `DownloadFileAsync(remotePath, stream, progress)`, `DeleteFileAsync(path)`, `GetStatusAsync(path)`, `Dispose()`
  - Create `SftpClientWrapper` implementing `ISftpClientWrapper`
  - Create `ConnectionInfo` model in `PulseTerm.Core/Models/`:
    - `Host`, `Port`, `Username`, `AuthMethod` (enum: Password, PrivateKey), `Password`, `PrivateKeyPath`, `PrivateKeyPassphrase`
  - Create `ISshConnectionService` interface:
    - `ConnectAsync(ConnectionInfo)` → returns `SshSession`
    - `DisconnectAsync(sessionId)`
    - `GetSession(sessionId)` → `SshSession`
    - `Sessions` → `IObservableList<SshSession>`
  - Create `SshConnectionService` implementing the interface using `ISshClientWrapper`
  - Create `SshSession` model: `Id`, `ConnectionInfo`, `Status` (enum: Connected, Connecting, Disconnected, Error), `ConnectedAt`, `SshClientWrapper`, `SftpClientWrapper`
  - Support auth methods: Password, PrivateKey (RSA, ED25519, ECDSA via `PrivateKeyFile`)
  - Handle connection errors: `SshAuthenticationException`, `SshConnectionException`, `SocketException`, timeouts
  - Create `Utf8StreamDecoder` in `PulseTerm.Core/Ssh/` — buffers incomplete multi-byte sequences between reads
  - RED: Write tests for all above → FAIL
  - GREEN: Implement → PASS
  - REFACTOR: Clean up

  **Must NOT do**:
  - Do NOT create `ISshProvider`, `ISshConnectionFactory`, or `AbstractSshSession`
  - Do NOT add SSH Agent, Jump host, or GSSAPI support
  - Do NOT handle reconnection logic yet (that's for terminal integration)

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Core SSH service with wrapper pattern requires careful interface design and thorough testing
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: No UI work
    - `playwright`: No browser testing

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 3, 4)
  - **Blocks**: Tasks 5, 6, 7 (all need SSH wrappers)
  - **Blocked By**: Task 1

  **References**:

  **Pattern References**:
  - SSH.NET SshClient API: `new SshClient(host, port, username, new PasswordAuthenticationMethod(username, password))`
  - SSH.NET PrivateKeyFile: `new PrivateKeyFile(path, passphrase)` → `new PrivateKeyAuthenticationMethod(username, keyFile)`
  - SSH.NET ShellStream: `client.CreateShellStream("xterm-256color", cols, rows, 0, 0, bufferSize, terminalModes)`
  - SSH.NET SFTP: `new SftpClient(connectionInfo)` shares `ConnectionInfo` with `SshClient`

  **External References**:
  - SSH.NET API docs: https://sshnet.github.io/SSH.NET/api/Renci.SshNet.html
  - SSH.NET GitHub: https://github.com/sshnet/SSH.NET
  - SSH.NET Issue #890 (mockability): Classes not mockable — justification for wrapper pattern
  - SSH.NET Issue #1762 (ShellStream disposal): Design Utf8StreamDecoder to handle stream resets

  **WHY Each Reference Matters**:
  - Wrapper pattern is REQUIRED because SSH.NET has non-virtual methods — without it, TDD is impossible
  - Utf8StreamDecoder addresses confirmed UTF-8 boundary splitting with 1024-byte default buffer
  - PrivateKeyFile supports RSA/ED25519/ECDSA automatically based on key file content

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=SshConnection"` → PASS (minimum 8 tests)
  - [ ] Tests cover: connect with password, connect with private key, auth failure → exception, connection refused → exception, disconnect, concurrent sessions, Utf8StreamDecoder partial UTF-8 handling
  - [ ] `ISshClientWrapper` and `ISftpClientWrapper` interfaces exist with all listed methods
  - [ ] `SshConnectionService` uses only `ISshClientWrapper` (no direct `new SshClient()`)
  - [ ] `Utf8StreamDecoder` correctly handles: complete sequences, split 2-byte chars, split 3-byte chars (CJK), split 4-byte chars (emoji), empty input

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: All SSH unit tests pass
    Tool: Bash
    Preconditions: Solution builds, NSubstitute available
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "Category=SshConnection" --logger "console;verbosity=detailed"
      2. Assert: exit code 0
      3. Assert: output shows 8+ tests passed, 0 failed
      4. Assert: output includes test names for password auth, key auth, auth failure, connection refused, disconnect, concurrent sessions, utf8 decoder
    Expected Result: All SSH connection tests pass with detailed output
    Evidence: Test output captured

  Scenario: No direct SshClient instantiation outside wrapper
    Tool: ast_grep_search
    Preconditions: Code written
    Steps:
      1. Search: pattern "new SshClient($$$)" in C# files under src/PulseTerm.Core/
      2. Assert: only found in SshClientWrapper.cs, nowhere else
      3. Search: pattern "new SftpClient($$$)" in C# files under src/PulseTerm.Core/
      4. Assert: only found in SftpClientWrapper.cs, nowhere else
    Expected Result: Wrapper pattern enforced — no leaky abstractions
    Evidence: ast_grep_search results

  Scenario: Utf8StreamDecoder handles CJK split correctly
    Tool: Bash
    Preconditions: Tests exist for Utf8StreamDecoder
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "FullyQualifiedName~Utf8StreamDecoder" --logger "console;verbosity=detailed"
      2. Assert: tests for split 3-byte CJK characters pass (e.g., "你" = 0xE4 0xBD 0xA0 split across two reads)
    Expected Result: Decoder buffers partial sequences correctly
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ssh): add SSH connection service with wrapper interfaces`
  - Files: `src/PulseTerm.Core/Ssh/`, `src/PulseTerm.Core/Models/`, `tests/PulseTerm.Core.Tests/Ssh/`
  - Pre-commit: `dotnet test tests/PulseTerm.Core.Tests --filter "Category=SshConnection"`

---

- [ ] 3. JSON Data Store + Session/Config Models + Tests

  **What to do**:
  - Create `JsonDataStore` in `PulseTerm.Core/Data/`:
    - `LoadAsync<T>(string filePath)` → deserializes JSON file to T
    - `SaveAsync<T>(string filePath, T data)` → serializes T to JSON file
    - File locking: `SemaphoreSlim(1,1)` + `FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)`
    - Retry: 3× with exponential backoff on `IOException`
    - Uses `System.Text.Json` with `JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`
  - Create models in `PulseTerm.Core/Models/`:
    - `ServerGroup` — `Id`, `Name`, `Icon`, `SortOrder`, `Sessions` (list)
    - `SessionProfile` — `Id`, `Name`, `Host`, `Port`, `Username`, `AuthMethod`, `Password`, `PrivateKeyPath`, `PrivateKeyPassphrase`, `GroupId`, `LastConnectedAt`, `Tags`
    - `AppSettings` — `Language` (string, default "en"), `Theme` (string, default "dark"), `TerminalFont` (default "JetBrains Mono"), `TerminalFontSize` (default 14), `ScrollbackLines` (default 10000), `DefaultPort` (default 22)
    - `AppState` — `RecentConnections` (list of session IDs, max 10), `WindowPosition`, `WindowSize`, `LastActiveTab`
    - `KnownHost` — `HostKey`, `Fingerprint`, `Algorithm`, `FirstSeenAt`, `LastSeenAt`
  - Create `ISessionRepository` interface:
    - `GetAllGroupsAsync()`, `GetSessionAsync(id)`, `SaveSessionAsync(SessionProfile)`, `DeleteSessionAsync(id)`, `SaveGroupAsync(ServerGroup)`, `DeleteGroupAsync(id)`
  - Create `SessionRepository` using `JsonDataStore` — stores to `~/.pulseterm/sessions.json`
  - Create `ISettingsService`:
    - `GetSettingsAsync()`, `SaveSettingsAsync(AppSettings)`, `GetStateAsync()`, `SaveStateAsync(AppState)`
  - Create `SettingsService` using `JsonDataStore` — stores to `~/.pulseterm/settings.json` and `~/.pulseterm/state.json`
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS
  - REFACTOR: Clean up

  **Must NOT do**:
  - Do NOT use `IRepository<T>` generic pattern or Unit of Work
  - Do NOT use `IOptions<T>` or hot-reload config patterns
  - Do NOT encrypt passwords yet (plaintext as decided)
  - Do NOT add migration logic for data format changes

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Data layer with file locking, retry logic, and model design requires careful implementation
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: No UI work

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 2, 4)
  - **Blocks**: Tasks 10, 13, 14 (UI shell and session tree need models)
  - **Blocked By**: Task 1

  **References**:

  **External References**:
  - System.Text.Json docs: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/overview
  - SemaphoreSlim pattern: https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim

  **WHY Each Reference Matters**:
  - System.Text.Json: Correct serialization options, handling of enums, nullable types
  - SemaphoreSlim: Required for file locking to prevent concurrent write corruption from multiple tabs

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=DataStore"` → PASS (minimum 10 tests)
  - [ ] Tests cover: save + load roundtrip, concurrent save (SemaphoreSlim prevents corruption), file not found → returns default, invalid JSON → graceful error, all model properties serialize/deserialize correctly
  - [ ] Data stored in `~/.pulseterm/` directory (or platform-appropriate equivalent)
  - [ ] `JsonDataStore` uses `FileShare.None` for exclusive write access

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Data store roundtrip works
    Tool: Bash
    Preconditions: Solution builds, tests written
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "Category=DataStore" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 10+ tests pass
      3. Assert: tests include "SaveAndLoad", "ConcurrentSave", "FileNotFound", "InvalidJson"
    Expected Result: All data store tests pass
    Evidence: Test output captured

  Scenario: Models serialize correctly to JSON
    Tool: Bash
    Preconditions: Tests exist for model serialization
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "FullyQualifiedName~Models" --logger "console;verbosity=detailed"
      2. Assert: SessionProfile, ServerGroup, AppSettings all roundtrip correctly
      3. Assert: camelCase property naming in JSON output
    Expected Result: All model serialization tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(core): add JSON data store and session models`
  - Files: `src/PulseTerm.Core/Data/`, `src/PulseTerm.Core/Models/`, `tests/PulseTerm.Core.Tests/Data/`, `tests/PulseTerm.Core.Tests/Models/`
  - Pre-commit: `dotnet test tests/PulseTerm.Core.Tests --filter "Category=DataStore"`

---

- [ ] 4. i18n Infrastructure (.resx Setup)

  **What to do**:
  - Create `PulseTerm.Core/Resources/` directory
  - Create `Strings.resx` (English — default) with PublicResXFileCodeGenerator
  - Create `Strings.zh-CN.resx` (Chinese)
  - Add initial string resources for core UI elements:
    - Sidebar: "Quick Connect", "Recent Connections", "Server Groups", "Settings", "Notifications"
    - Terminal: "New Tab", "Close Tab", "Search", "Copy", "Split", "Broadcast", "Sync Group"
    - File Browser: "File Name", "Size", "Permissions", "Modified", "Upload", "Download", "Refresh"
    - Tunnel: "Local Forward", "Remote Forward", "Local Port", "Remote Address", "New Tunnel", "Active Tunnels"
    - Quick Commands: "Search Commands", "System Monitor", "Network", "Docker", "Custom"
    - Status Bar: "Connected", "Connecting", "Disconnected", "Latency"
    - General: "Connect", "Disconnect", "Save", "Cancel", "Delete", "Edit", "OK", "Error", "Warning"
    - Settings: "Language", "Theme", "Font", "Font Size", "Scrollback Lines"
    - Auth: "Password", "Private Key", "Username", "Host", "Port", "Host Key Verification", "Trust This Host"
  - Add corresponding Chinese translations in `Strings.zh-CN.resx`
  - Configure `PulseTerm.Core.csproj` to generate strongly-typed resource accessor class
  - Create `ILocalizationService` with `GetString(key)` and `CurrentLanguage` property
  - Create `LocalizationService` that reads from generated `Strings` class based on `CultureInfo`
  - Register in DI
  - Write tests verifying: English strings load by default, Chinese strings load with zh-CN culture, missing key returns key name

  **Must NOT do**:
  - Do NOT implement runtime language hot-switch (language set at startup)
  - Do NOT add RTL language support
  - Do NOT create custom i18n framework — use standard .resx

  **Recommended Agent Profile**:
  - **Category**: `unspecified-low`
    - Reason: Straightforward .resx setup following standard .NET localization patterns
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: No UI rendering work, just resource files

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 2, 3)
  - **Blocks**: Task 20 (light theme uses localized strings)
  - **Blocked By**: Task 1

  **References**:

  **External References**:
  - .NET localization: https://learn.microsoft.com/en-us/dotnet/core/extensions/localization
  - Avalonia i18n with resx: https://docs.avaloniaui.net/docs/guides/development-guides/localizing

  **WHY Each Reference Matters**:
  - .NET localization docs: Correct .resx setup with PublicResXFileCodeGenerator and CultureInfo switching
  - Avalonia i18n: How to bind resx strings in AXAML via `{x:Static}` markup

  **Acceptance Criteria**:

  - [ ] `dotnet build src/PulseTerm.Core --warnaserror` → PASS
  - [ ] `Strings.resx` and `Strings.zh-CN.resx` exist with 40+ string entries each
  - [ ] Generated `Strings` class is accessible from code: `Strings.QuickConnect` returns "Quick Connect"
  - [ ] Tests pass: default culture returns English, zh-CN culture returns Chinese, missing key fallback works

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: i18n strings load correctly
    Tool: Bash
    Preconditions: Solution builds
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "Category=i18n" --logger "console;verbosity=detailed"
      2. Assert: exit code 0
      3. Assert: English default test passes
      4. Assert: Chinese culture test passes
    Expected Result: Localization infrastructure works for both languages
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(i18n): add localization infrastructure (zh-CN + en)`
  - Files: `src/PulseTerm.Core/Resources/`, `tests/PulseTerm.Core.Tests/Resources/`
  - Pre-commit: `dotnet build src/PulseTerm.Core --warnaserror`

---

- [x] 5. Terminal Spike — SSH.NET ↔ AvaloniaTerminal Bridge (CRITICAL PATH)

  **What to do**:
  - **THIS IS THE HIGHEST RISK TASK IN THE ENTIRE PROJECT.** If this spike fails, the terminal approach must pivot.
  - Create `ITerminalEmulator` interface in `PulseTerm.Terminal/`:
    - `Feed(byte[] data)` — feed raw bytes from SSH stream
    - `Resize(int cols, int rows)` — resize terminal
    - `UserInput` event → byte[] for sending to SSH
    - `GetBufferLine(int row)` → terminal line content
    - `CursorRow`, `CursorCol` properties
    - `ScrollbackLines` property
    - `Control` property → Avalonia Control for embedding in UI
  - Create `AvaloniaTerminalEmulator` implementing `ITerminalEmulator` wrapping AvaloniaTerminal/XtermSharp
  - Create `SshTerminalBridge` in `PulseTerm.Terminal/`:
    - Connects `ShellStream` ↔ `ITerminalEmulator`
    - Data flow: `ShellStream.ReadAsync → byte[] → Utf8StreamDecoder → ITerminalEmulator.Feed(bytes)`
    - Reverse: `ITerminalEmulator.UserInput → byte[] → ShellStream.WriteAsync`
    - Uses `Dispatcher.UIThread.InvokeAsync()` for UI thread marshaling
    - Handles `ShellStream` disposal (Issue #1762) with watchdog — monitors `CanWrite`, auto-recreates stream
    - Terminal modes: `new Dictionary<TerminalModes, uint> { { TerminalModes.ECHO, 53 } }`
  - Validate in spike tests:
    1. Feed raw VT100 escape sequences → verify buffer state (cursor position, colors)
    2. Feed "你好世界" (CJK) → verify double-width cells
    3. Feed 1MB+ of data → no crash, no ObjectDisposedException, memory < 50MB
    4. Resize 80×24 → 120×40 → verify ShellStream.ChangeWindowSize called correctly
    5. UserInput "ls\n" → verify bytes written to ShellStream
  - **SPIKE PASS CRITERIA**: ALL 5 validations must pass. If ANY fail, document failure and propose Plan B (xterm.js in CefGlue WebView).

  **Must NOT do**:
  - Do NOT build full UI integration — this is an isolated spike
  - Do NOT implement scrollback yet (Task 9)
  - Do NOT handle reconnection (Task 12)
  - Do NOT spend more than 3 days on this spike — if it's not working, document and pivot

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: This is the single highest technical risk — novel integration with unknown unknowns, requires deep investigation and problem-solving
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: This is a backend/integration spike, not UI design
    - `playwright`: No browser testing — this is terminal emulation

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 6, 7)
  - **Blocks**: Tasks 8, 9, 12 (theme, scrollback, terminal integration all depend on spike validation)
  - **Blocked By**: Task 2 (needs SSH wrappers)

  **References**:

  **Pattern References**:
  - Task 2's `ISshClientWrapper` — use this for SSH connection, NOT direct `SshClient`
  - Task 2's `Utf8StreamDecoder` — use for stream decoding

  **External References**:
  - AvaloniaTerminal source: https://github.com/IvanJosipovic/AvaloniaTerminal — `TerminalControlModel` is the bridge point
  - XtermSharp source: https://github.com/niclas-pricken/XtermSharp — `Terminal.Feed(byte[], int)` API
  - XtermSharp TODO.md: Documents missing scrollback, incomplete resize, no mouse support
  - SSH.NET ShellStream API: `CreateShellStream("xterm-256color", cols, rows, 0, 0, 4096, terminalModes)`
  - SSH.NET Issue #1762: ShellStream disposal on large output — design watchdog around this

  **WHY Each Reference Matters**:
  - AvaloniaTerminal `TerminalControlModel`: This is where byte[] data enters the terminal — `Terminal.Feed(data)` call site
  - XtermSharp TODO.md: Know exactly what's broken before starting (scrollback, resize, mouse)
  - Issue #1762: ShellStream dies on large output — the bridge MUST handle this or terminal sessions crash

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=TerminalBridge"` → PASS (minimum 5 tests)
  - [ ] Test: Feed `"\033[31mRED\033[0m"` → buffer cell at (0,0) has foreground=Red
  - [ ] Test: Feed `"你好"` (0xE4BDA0 0xE5A5BD) → cells 0-1 are first char (double-width), cells 2-3 are second char
  - [ ] Test: Feed 1MB of data → no crash, no `ObjectDisposedException`, memory measured via `GC.GetAllocatedBytesForCurrentThread` stays under 50MB
  - [ ] Test: Resize terminal 80×24 → 120×40 → `ShellStream.ChangeWindowSize` called with (120, 40, 0, 0)
  - [ ] Test: UserInput event fires → bytes written to ShellStream mock
  - [ ] `ITerminalEmulator` interface defined with all listed methods
  - [ ] Spike result documented: PASS (proceed) or FAIL (document issues, propose Plan B)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Terminal bridge spike tests all pass
    Tool: Bash
    Preconditions: Solution builds, SSH wrappers from Task 2 available
    Steps:
      1. Run: dotnet test tests/PulseTerm.Terminal.Tests --filter "Category=TerminalBridge" --logger "console;verbosity=detailed"
      2. Assert: exit code 0
      3. Assert: 5+ tests pass including VT100 color, CJK double-width, large output, resize, user input
      4. If ANY test fails: capture failure details, document in .sisyphus/evidence/spike-failure.md
    Expected Result: All 5 spike validations pass, green light to proceed
    Evidence: Test output captured, spike result documented

  Scenario: ITerminalEmulator interface is properly abstracted
    Tool: ast_grep_search
    Preconditions: Code written
    Steps:
      1. Search: pattern "interface ITerminalEmulator" in C# files
      2. Assert: found in PulseTerm.Terminal/
      3. Search: pattern "new AvaloniaTerminalEmulator($$$)" outside test files
      4. Assert: only created via DI or factory, not hardcoded in consumers
    Expected Result: Terminal emulator is behind an interface for swappability
    Evidence: ast_grep results
  ```

  **Commit**: YES
  - Message: `feat(terminal): validate SSH↔terminal bridge spike`
  - Files: `src/PulseTerm.Terminal/`, `tests/PulseTerm.Terminal.Tests/`
  - Pre-commit: `dotnet test tests/PulseTerm.Terminal.Tests --filter "Category=TerminalBridge"`

---

- [ ] 6. SFTP Service + Tests

  **What to do**:
  - Create `ISftpService` interface in `PulseTerm.Core/Sftp/`:
    - `ListDirectoryAsync(sessionId, path)` → `List<RemoteFileInfo>`
    - `UploadFileAsync(sessionId, localPath, remotePath, IProgress<TransferProgress>)` → Task
    - `DownloadFileAsync(sessionId, remotePath, localPath, IProgress<TransferProgress>)` → Task
    - `DeleteAsync(sessionId, remotePath)` → Task
    - `CreateDirectoryAsync(sessionId, remotePath)` → Task
    - `GetFileInfoAsync(sessionId, remotePath)` → `RemoteFileInfo`
  - Create models:
    - `RemoteFileInfo` — `Name`, `FullPath`, `Size`, `Permissions` (string like "drwxr-xr-x"), `IsDirectory`, `LastModified`, `Owner`, `Group`
    - `TransferProgress` — `FileName`, `BytesTransferred`, `TotalBytes`, `Percentage`, `SpeedBytesPerSecond`, `EstimatedTimeRemaining`
    - `TransferTask` — `Id`, `Type` (Upload/Download), `LocalPath`, `RemotePath`, `Status` (Queued/InProgress/Completed/Failed/Cancelled), `Progress`
  - Create `SftpService` implementing `ISftpService` using `ISftpClientWrapper` from Task 2
  - Configure SFTP performance: `BufferSize = 256 * 1024` for faster transfers
  - Create `ITransferManager` — manages queue of `TransferTask` with concurrent limit (default 3)
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS
  - REFACTOR: Clean up

  **Must NOT do**:
  - Do NOT implement drag-and-drop from OS file manager
  - Do NOT implement SCP protocol
  - Do NOT implement file resume (post-v1 enhancement)
  - Do NOT build UI — service layer only

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: SFTP service with transfer queue, progress tracking, and concurrent operations requires careful design
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 5, 7)
  - **Blocks**: Tasks 15, 16 (file browser UI and transfer panel UI)
  - **Blocked By**: Task 2 (needs ISftpClientWrapper)

  **References**:

  **Pattern References**:
  - Task 2's `ISftpClientWrapper` — use this for all SFTP operations

  **External References**:
  - SSH.NET SFTP docs: https://sshnet.github.io/SSH.NET/api/Renci.SshNet.SftpClient.html
  - SSH.NET SFTP pipelining: Up to 100 concurrent SSH_FXP_READ requests, 32KB chunks default

  **WHY Each Reference Matters**:
  - SFTP pipelining: Tuning `BufferSize` dramatically affects transfer speed
  - ISftpClientWrapper: Required for testing without real SSH server

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=Sftp"` → PASS (minimum 8 tests)
  - [ ] Tests: list directory returns RemoteFileInfo[], upload file verifies bytes written, download file verifies bytes read, delete file, create directory, permission denied → exception, progress callback fires with correct percentages
  - [ ] TransferManager respects concurrent limit (default 3 simultaneous transfers)
  - [ ] All SFTP operations go through ISftpClientWrapper (no direct SftpClient usage)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: SFTP service tests pass
    Tool: Bash
    Preconditions: Solution builds, SSH wrappers available
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "Category=Sftp" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 8+ tests pass
      3. Assert: tests include ListDirectory, Upload, Download, Delete, CreateDir, PermissionDenied, ProgressCallback, ConcurrentLimit
    Expected Result: All SFTP service tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(sftp): add SFTP service with file operations`
  - Files: `src/PulseTerm.Core/Sftp/`, `src/PulseTerm.Core/Models/RemoteFileInfo.cs`, `src/PulseTerm.Core/Models/TransferProgress.cs`, `tests/PulseTerm.Core.Tests/Sftp/`
  - Pre-commit: `dotnet test tests/PulseTerm.Core.Tests --filter "Category=Sftp"`

---

- [ ] 7. Port Forwarding Service + Tests

  **What to do**:
  - Create `ITunnelService` interface in `PulseTerm.Core/Tunnels/`:
    - `CreateLocalForwardAsync(sessionId, TunnelConfig)` → `TunnelInfo`
    - `CreateRemoteForwardAsync(sessionId, TunnelConfig)` → `TunnelInfo`
    - `StopTunnelAsync(tunnelId)` → Task
    - `GetActiveTunnels(sessionId)` → `IObservableList<TunnelInfo>`
  - Create models:
    - `TunnelConfig` — `Type` (LocalForward/RemoteForward), `Name`, `LocalHost`, `LocalPort`, `RemoteHost`, `RemotePort`
    - `TunnelInfo` — `Id`, `Config`, `Status` (Active/Stopped/Error), `SessionId`, `CreatedAt`, `BytesTransferred`
  - Create `TunnelService` implementing `ITunnelService` using `ISshClientWrapper.AddForwardedPort()`
  - Handle: ForwardedPort lifecycle tied to SSH session, store TunnelConfig separately for reconnect recreation
  - Handle: Individual tunnel failures isolated (other tunnels keep working)
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS
  - REFACTOR: Clean up

  **Must NOT do**:
  - Do NOT implement dynamic SOCKS forwarding
  - Do NOT implement Jump host chains
  - Do NOT build UI — service layer only

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Port forwarding with lifecycle management and error isolation needs careful implementation
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 5, 6)
  - **Blocks**: Task 17 (tunnel management UI)
  - **Blocked By**: Task 2 (needs ISshClientWrapper)

  **References**:

  **Pattern References**:
  - Task 2's `ISshClientWrapper` — `AddForwardedPort(ForwardedPortLocal/ForwardedPortRemote)`

  **External References**:
  - SSH.NET port forwarding: `new ForwardedPortLocal(boundHost, boundPort, host, port)` → `client.AddForwardedPort(port)` → `port.Start()`
  - SSH.NET lifecycle: ForwardedPort instances are tied to session — cannot be reused after reconnect

  **WHY Each Reference Matters**:
  - ForwardedPort lifecycle: On SSH disconnect, all tunnels die — must recreate from stored TunnelConfig on reconnect
  - Individual failure isolation: One tunnel failure shouldn't kill others

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=Tunnel"` → PASS (minimum 6 tests)
  - [ ] Tests: create local forward, create remote forward, stop tunnel, tunnel status tracking, individual failure isolation, reconnect recreation from stored config
  - [ ] TunnelConfig stored independently from ForwardedPort for reconnect support

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Tunnel service tests pass
    Tool: Bash
    Preconditions: Solution builds
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "Category=Tunnel" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 6+ tests pass
    Expected Result: All tunnel service tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(tunnel): add port forwarding service`
  - Files: `src/PulseTerm.Core/Tunnels/`, `tests/PulseTerm.Core.Tests/Tunnels/`
  - Pre-commit: `dotnet test tests/PulseTerm.Core.Tests --filter "Category=Tunnel"`

---

- [ ] 8. Theme System + Design Tokens from .pen File

  **What to do**:
  - Create theme ResourceDictionaries in `PulseTerm.App/Themes/`:
    - `DesignTokens.axaml` — shared token keys
    - `DarkTheme.axaml` — dark theme values from PulseTerm-zh.pen design
    - `LightTheme.axaml` — light theme values (invert/lighten dark tokens)
  - Design tokens from .pen file (exact hex values):
    - `$bg-page`: #0D1117 (dark) → map to `PulseBgPage`
    - `$bg-sidebar`: #161B22 → `PulseBgSidebar`
    - `$bg-terminal`: #0D1117 → `PulseBgTerminal`
    - `$bg-surface`: #1C2128 → `PulseBgSurface`
    - `$bg-active`: #1F6FEB33 → `PulseBgActive`
    - `$bg-hover`: #FFFFFF0D → `PulseBgHover`
    - `$bg-input`: #0D1117 → `PulseBgInput`
    - `$accent`: #00D4AA → `PulseAccent`
    - `$accent-dim`: #00D4AA33 → `PulseAccentDim`
    - `$accent-text`: #00D4AA → `PulseAccentText`
    - `$text-primary`: #E6EDF3 → `PulseTextPrimary`
    - `$text-secondary`: #8B949E → `PulseTextSecondary`
    - `$text-tertiary`: #6E7681 → `PulseTextTertiary`
    - `$text-muted`: #484F58 → `PulseTextMuted`
    - `$border-primary`: #30363D → `PulseBorderPrimary`
    - `$border-secondary`: #21262D → `PulseBorderSecondary`
    - `$status-connected`: #00D4AA → `PulseStatusConnected`
    - `$status-connecting`: #F0C000 → `PulseStatusConnecting`
    - `$status-disconnected`: #F85149 → `PulseStatusDisconnected`
    - `$warning`: #F0C000 → `PulseWarning`
    - `$error`: #F85149 → `PulseError`
    - `$info`: #58A6FF → `PulseInfo`
    - `$tab-inactive-bg`: #161B22 → `PulseTabInactiveBg`
  - Configure FluentTheme with ThemeDictionaries in `App.axaml`:
    - Dark variant uses DarkTheme.axaml tokens
    - Light variant uses LightTheme.axaml tokens
  - Create `IThemeService` in `PulseTerm.Core/Services/`:
    - `CurrentTheme` (observable, "dark" or "light")
    - `SetTheme(string themeName)` — switches theme at runtime
  - Font configuration: JetBrains Mono for terminal, Inter for UI
  - Include JetBrains Mono as embedded resource (fallback if not system-installed)

  **Must NOT do**:
  - Do NOT create custom theme editor or per-session themes
  - Do NOT add more than Dark + Light themes
  - Do NOT deviate from .pen file design token values for dark theme

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Theme system requires precise color matching to design tokens, AXAML styling expertise
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: Design token mapping, color system implementation, font configuration
  - **Skills Evaluated but Omitted**:
    - `playwright`: No browser — Avalonia desktop app

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 4 (with Task 9)
  - **Blocks**: Task 10 (core UI shell needs theme)
  - **Blocked By**: Task 5 (spike must pass to confirm terminal approach)

  **References**:

  **Pattern References**:
  - `PulseTerm-zh.pen` design file — ALL color values extracted above come from this file's design tokens

  **External References**:
  - Avalonia theming: https://docs.avaloniaui.net/docs/basics/user-interface/styling/themes/fluent
  - Avalonia ThemeDictionaries: https://docs.avaloniaui.net/docs/guides/styles-and-resources/how-to-use-theme-dictionaries
  - JetBrains Mono: https://www.jetbrains.com/lp/mono/ (OFL license, free to embed)

  **WHY Each Reference Matters**:
  - ThemeDictionaries: The mechanism for switching between Dark/Light at runtime — must use `{DynamicResource}` for all tokens
  - .pen file tokens: The EXACT colors that must be reproduced — dark theme must be pixel-accurate

  **Acceptance Criteria**:

  - [ ] `dotnet build src/PulseTerm.App --warnaserror` → PASS
  - [ ] DarkTheme.axaml contains ALL 23 design tokens listed above with exact hex values
  - [ ] LightTheme.axaml contains all 23 tokens with appropriate light variants
  - [ ] `App.axaml` configures FluentTheme with ThemeDictionaries for Dark and Light
  - [ ] JetBrains Mono font embedded or referenced correctly
  - [ ] `IThemeService.SetTheme("light")` switches active theme

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Theme resources load without errors
    Tool: Bash
    Preconditions: Solution builds
    Steps:
      1. Run: dotnet build src/PulseTerm.App --warnaserror
      2. Assert: exit code 0, no XAML parse errors
      3. Run: grep -c "PulseAccent\|PulseBgPage\|PulseTextPrimary" src/PulseTerm.App/Themes/DarkTheme.axaml
      4. Assert: count matches expected number of tokens (23+)
    Expected Result: All theme tokens defined, app builds cleanly
    Evidence: Build output captured

  Scenario: Dark theme colors match design spec
    Tool: Bash (grep verification)
    Preconditions: DarkTheme.axaml exists
    Steps:
      1. Verify: DarkTheme.axaml contains "#0D1117" for PulseBgPage
      2. Verify: DarkTheme.axaml contains "#00D4AA" for PulseAccent
      3. Verify: DarkTheme.axaml contains "#E6EDF3" for PulseTextPrimary
      4. Verify: DarkTheme.axaml contains "#F85149" for PulseStatusDisconnected
    Expected Result: All design tokens match .pen file values exactly
    Evidence: grep output captured
  ```

  **Commit**: YES
  - Message: `feat(theme): add dark/light theme system with design tokens`
  - Files: `src/PulseTerm.App/Themes/`, `src/PulseTerm.App/App.axaml`, `src/PulseTerm.Core/Services/IThemeService.cs`
  - Pre-commit: `dotnet build src/PulseTerm.App --warnaserror`

---

- [ ] 9. Custom Scrollback Buffer Implementation

  **What to do**:
  - Create `ScrollbackBuffer` in `PulseTerm.Terminal/`:
    - Wraps XtermSharp's `Terminal` — intercepts lines that scroll off the top of the visible buffer
    - Configurable max lines (default 10000 from AppSettings.ScrollbackLines)
    - `GetLine(int absoluteRow)` → `TerminalLine` (content + attributes)
    - `TotalLines` → visible rows + scrollback rows
    - `ScrollTo(int absoluteRow)` — scroll viewport
    - `ScrollUp(int lines)`, `ScrollDown(int lines)`
    - `Search(string query)` → `List<SearchMatch>` with row/col positions
    - Circular buffer implementation for memory efficiency
  - Update `ITerminalEmulator` to expose scrollback:
    - `ScrollbackBuffer` property
    - `TotalLines` property
    - `ViewportRow` property (current scroll position)
  - Update `AvaloniaTerminalEmulator` to integrate `ScrollbackBuffer`
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS
  - REFACTOR: Clean up

  **Must NOT do**:
  - Do NOT implement infinite scrollback — cap at configurable max (default 10000)
  - Do NOT implement scrollback search highlighting in terminal view yet (just the search API)
  - Do NOT modify XtermSharp source — wrap, don't fork

  **Recommended Agent Profile**:
  - **Category**: `ultrabrain`
    - Reason: Circular buffer with terminal line interception, coordinate translation between scrollback and visible buffer — genuinely complex data structure work
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: Pure data structure, no UI

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 4 (with Task 8)
  - **Blocks**: Task 12 (terminal tab integration needs scrollback)
  - **Blocked By**: Task 5 (needs spike-validated terminal emulator)

  **References**:

  **Pattern References**:
  - Task 5's `ITerminalEmulator` — extend this interface with scrollback properties
  - Task 5's `AvaloniaTerminalEmulator` — integrate scrollback into this implementation

  **External References**:
  - XtermSharp TODO.md: "Buffer management and scrollback support" listed as missing — confirms we must build this
  - Circular buffer pattern: Standard ring buffer with head/tail pointers, O(1) append, O(1) access by index

  **WHY Each Reference Matters**:
  - XtermSharp explicitly lacks scrollback — this is not optional, it's a fundamental terminal feature
  - Circular buffer: Memory-efficient for 10000+ lines without list resizing

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=Scrollback"` → PASS (minimum 8 tests)
  - [ ] Tests: append lines, circular buffer wraps at max, GetLine returns correct content, ScrollTo moves viewport, ScrollUp/ScrollDown by N lines, Search finds text across scrollback + visible, TotalLines count correct, configurable max lines
  - [ ] Memory: 10000 lines of 120-char terminal output uses < 20MB
  - [ ] Circular buffer correctly discards oldest lines when full

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Scrollback buffer tests pass
    Tool: Bash
    Preconditions: Terminal spike (Task 5) completed
    Steps:
      1. Run: dotnet test tests/PulseTerm.Terminal.Tests --filter "Category=Scrollback" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 8+ tests pass
      3. Assert: includes circular wrap test, search test, viewport scroll test
    Expected Result: All scrollback tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(terminal): add custom scrollback buffer`
  - Files: `src/PulseTerm.Terminal/ScrollbackBuffer.cs`, `tests/PulseTerm.Terminal.Tests/ScrollbackBufferTests.cs`
  - Pre-commit: `dotnet test tests/PulseTerm.Terminal.Tests --filter "Category=Scrollback"`

---

- [ ] 10. Core UI Shell — MainWindow + Sidebar + Tab Bar

  **What to do**:
  - Create `MainWindow.axaml` in `PulseTerm.App/Views/`:
    - 3-column layout: Sidebar (260px fixed) | Terminal Area (flex) | Optional right panel
    - Bottom status bar (24px)
    - Top tab bar above terminal area
    - Use `DockPanel` or `Grid` with column definitions matching .pen layout (1440×900 reference)
  - Create `MainWindowViewModel` with ReactiveUI:
    - `Sidebar` → `SidebarViewModel`
    - `Tabs` → `IObservableList<TabViewModel>`
    - `ActiveTab` → `TabViewModel` (reactive property)
    - `StatusBar` → `StatusBarViewModel`
    - Commands: `AddTab`, `CloseTab`, `SwitchTab`
  - Create `SidebarView.axaml` + `SidebarViewModel`:
    - Logo bar: "PulseTerm v1.0" with app icon
    - Placeholder TreeView for session tree (populated in Task 13)
    - Quick connect input field: `用户名@主机名:端口` format
    - Recent connections list (placeholder, populated in Task 13)
    - User info footer with notification + settings buttons
    - Sidebar width: 260px, background: `{DynamicResource PulseBgSidebar}`
  - Create `TabBarView.axaml` + `TabBarViewModel`:
    - Tab items with: title, connection status dot (green/yellow/red), close button
    - Active tab highlight with accent color
    - "+" button to add new tab
    - Tab overflow with horizontal scroll
  - Apply dark theme tokens from Task 8 to all views
  - Use `{x:Static}` bindings for localized strings from Task 4
  - Register all ViewModels and Views in DI container
  - RED: Write ViewModel tests (tab add/remove/switch, sidebar layout) → FAIL
  - GREEN: Implement Views + ViewModels → PASS
  - REFACTOR: Polish layout, verify theme tokens applied

  **Must NOT do**:
  - Do NOT implement session tree population (Task 13)
  - Do NOT implement terminal content area (Task 12)
  - Do NOT implement status bar content (Task 19)
  - Do NOT create `BaseViewModel` — use `ReactiveObject` directly
  - Do NOT hard-code colors — ALL colors via `{DynamicResource PulseXxx}` tokens

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Core UI layout requiring pixel-accurate implementation from design spec
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: Layout design, design token application, responsive UI patterns

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 5 (with Task 11)
  - **Blocks**: Tasks 12, 13, 15-19 (all UI panels need the shell)
  - **Blocked By**: Tasks 3, 8 (needs models + theme system)

  **References**:

  **Pattern References**:
  - `PulseTerm-zh.pen` design file — layout dimensions: sidebar 260px, terminal area 644px, status bar 24px, tab bar height ~36px
  - Task 8's theme tokens — ALL `{DynamicResource PulseXxx}` keys defined there
  - Task 3's models — `SessionProfile`, `ServerGroup`, `AppSettings` used by ViewModels
  - Task 4's i18n — `{x:Static res:Strings.QuickConnect}` for localized text

  **External References**:
  - Avalonia layouts: https://docs.avaloniaui.net/docs/basics/user-interface/building-layouts/
  - Avalonia DockPanel: https://docs.avaloniaui.net/docs/reference/controls/dockpanel
  - ReactiveUI ViewModels: https://docs.avaloniaui.net/docs/concepts/reactiveui/
  - Avalonia TreeView: https://docs.avaloniaui.net/docs/reference/controls/treeview
  - Design: sidebar icon = Lucide icons, fonts = Inter for UI text

  **WHY Each Reference Matters**:
  - .pen layout: Exact pixel dimensions for faithful reproduction
  - ReactiveUI docs: Correct `[Reactive]` property pattern, `ReactiveCommand` for tab operations

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=UI"` → PASS (minimum 6 tests)
  - [ ] Tests: MainWindowViewModel creates with sidebar/tabs/statusbar, AddTab adds to collection, CloseTab removes, SwitchTab updates ActiveTab, sidebar ViewModel initializes
  - [ ] `dotnet build src/PulseTerm.App --warnaserror` → PASS (no XAML errors)
  - [ ] MainWindow layout: 3-column grid, sidebar 260px, status bar docked bottom 24px
  - [ ] ALL colors use `{DynamicResource PulseXxx}` — zero hardcoded hex values in AXAML
  - [ ] ALL user-visible text uses `{x:Static}` i18n bindings

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: UI shell ViewModel tests pass
    Tool: Bash
    Preconditions: Solution builds with theme + models
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=UI" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 6+ tests pass
    Expected Result: All UI ViewModel tests pass
    Evidence: Test output captured

  Scenario: No hardcoded colors in AXAML
    Tool: Grep
    Preconditions: AXAML files exist
    Steps:
      1. Search: pattern "#[0-9A-Fa-f]{6}" in *.axaml files under src/PulseTerm.App/Views/
      2. Assert: zero matches (all colors should be DynamicResource)
    Expected Result: All colors use theme tokens, not hardcoded hex
    Evidence: grep results
  ```

  **Commit**: YES
  - Message: `feat(ui): add main window shell with sidebar and tab bar`
  - Files: `src/PulseTerm.App/Views/`, `src/PulseTerm.App/ViewModels/`, `tests/PulseTerm.App.Tests/`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=UI"`

---

- [ ] 11. Host Key TOFU (Trust-On-First-Use) Service + Tests

  **What to do**:
  - Create `IHostKeyService` in `PulseTerm.Core/Ssh/`:
    - `VerifyHostKeyAsync(host, port, keyType, fingerprint)` → `HostKeyVerification` (enum: Trusted, Unknown, Changed)
    - `TrustHostKeyAsync(host, port, keyType, fingerprint)` → stores in known_hosts
    - `GetKnownHosts()` → `List<KnownHost>`
    - `RemoveKnownHost(host, port)` → Task
  - Create `HostKeyService` using `JsonDataStore` — stores to `~/.pulseterm/known_hosts.json`
  - `KnownHost` model (from Task 3): `Host`, `Port`, `KeyType`, `Fingerprint`, `Algorithm`, `FirstSeenAt`, `LastSeenAt`
  - Integrate into `SshConnectionService`:
    - On connect: `SshClient.HostKeyReceived` event → call `IHostKeyService.VerifyHostKeyAsync`
    - If Unknown → raise `HostKeyUnknownEvent` (UI will prompt user)
    - If Changed → raise `HostKeyChangedEvent` (WARNING: potential MITM)
    - If Trusted → proceed silently
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT build UI for host key prompts (Task 14 handles this)
  - Do NOT implement strict host key checking mode
  - Do NOT implement full known_hosts file format — use JSON

  **Recommended Agent Profile**:
  - **Category**: `unspecified-low`
    - Reason: Straightforward TOFU pattern with JSON persistence
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 5 (with Task 10)
  - **Blocks**: Task 14 (settings/connection profiles use host key verification)
  - **Blocked By**: Task 1

  **References**:

  **Pattern References**:
  - Task 3's `JsonDataStore` — persistence for `~/.pulseterm/known_hosts.json`
  - Task 3's `KnownHost` model
  - Task 2's `SshConnectionService` — integrate host key check

  **External References**:
  - SSH.NET HostKeyReceived: `client.HostKeyReceived += (sender, e) => { e.CanTrust = ...; }`

  **WHY Each Reference Matters**:
  - `HostKeyReceived`: The SSH.NET hook point — `e.CanTrust` must be set synchronously, use `TaskCompletionSource` for async UI prompt

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=HostKey"` → PASS (minimum 5 tests)
  - [ ] Tests: first connection → Unknown, trust + reconnect → Trusted, key changed → Changed, remove host, list known hosts
  - [ ] Known hosts stored in `~/.pulseterm/known_hosts.json`

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Host key service tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.Core.Tests --filter "Category=HostKey" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 5+ tests pass
    Expected Result: All host key TOFU tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ssh): add host key TOFU verification`
  - Files: `src/PulseTerm.Core/Ssh/HostKeyService.cs`, `src/PulseTerm.Core/Ssh/IHostKeyService.cs`, `tests/PulseTerm.Core.Tests/Ssh/HostKeyServiceTests.cs`
  - Pre-commit: `dotnet test tests/PulseTerm.Core.Tests --filter "Category=HostKey"`

---

- [ ] 12. Terminal Tab Integration (Terminal Control + Toolbar)

  **What to do**:
  - Create `TerminalTabView.axaml` in `PulseTerm.App/Views/`:
    - Terminal toolbar at top: shows `root@hostname:~` with uptime/latency info
    - Toolbar action buttons: Search, Copy, Split, Tunnel, Quick Commands, Sync Group, Broadcast
    - Terminal control area: embeds `ITerminalEmulator.Control`
    - Scrollbar for scrollback navigation
  - Create `TerminalTabViewModel` with ReactiveUI:
    - `TerminalEmulator` → `ITerminalEmulator`
    - `SshBridge` → `SshTerminalBridge`
    - `Session` → `SshSession`
    - `Title` (reactive) — `username@host` format
    - `ConnectionStatus` (reactive)
    - `Latency` (reactive) — via SSH keepalive
    - Commands: `Search`, `Copy`, `Split`, `ToggleBroadcast`, `OpenTunnel`, `OpenQuickCommands`
    - Reconnection: detect disconnect → overlay → auto-retry (max 3, exponential backoff)
    - Keyboard: terminal captures ALL keys, Ctrl+Shift+C/V for copy/paste
  - Create `TerminalToolbarView.axaml` — horizontal button bar matching .pen design
  - Integrate `ScrollbackBuffer` from Task 9
  - Wire data flow: `ShellStream → Utf8StreamDecoder → ITerminalEmulator.Feed → Dispatcher.UIThread.InvokeAsync`
  - RED: Write ViewModel tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement split terminal panes (button placeholder only)
  - Do NOT implement broadcast mode logic (button placeholder only)
  - Do NOT implement sync group logic (button placeholder only)

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Core integration connecting SSH, terminal emulator, scrollback, and UI
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: Terminal toolbar layout, scrollbar integration

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 6 (with Tasks 13, 14)
  - **Blocks**: Tasks 19, 21
  - **Blocked By**: Tasks 9, 10

  **References**:

  **Pattern References**:
  - Task 5's `ITerminalEmulator` + `SshTerminalBridge`
  - Task 9's `ScrollbackBuffer`
  - Task 2's `SshConnectionService` + `Utf8StreamDecoder`
  - Task 10's `MainWindowViewModel` — tab hosting
  - `PulseTerm-zh.pen`: Terminal toolbar layout, action buttons

  **External References**:
  - SSH.NET keepalive: `client.KeepAliveInterval = TimeSpan.FromSeconds(30)`
  - Avalonia Dispatcher: `Dispatcher.UIThread.InvokeAsync()` — REQUIRED for all terminal updates (Issue #1934)

  **WHY Each Reference Matters**:
  - Dispatcher.UIThread: ALL terminal data updates MUST go through this
  - SshTerminalBridge: Data pipeline — incorrect wiring = no terminal output

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=Terminal"` → PASS (minimum 8 tests)
  - [ ] Tests: ViewModel creates, connection lifecycle, title updates, status tracking, reconnection, copy command, toolbar commands exist
  - [ ] Scrollbar navigates scrollback history
  - [ ] Theme tokens applied to toolbar

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Terminal ViewModel tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=Terminal" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 8+ tests pass
    Expected Result: All terminal integration tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): integrate terminal control with toolbar`
  - Files: `src/PulseTerm.App/Views/TerminalTab*`, `src/PulseTerm.App/ViewModels/TerminalTab*`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=Terminal"`

---

- [ ] 13. Session Tree ViewModel + Quick Connect

  **What to do**:
  - Create `SessionTreeViewModel` — loads groups + sessions from `ISessionRepository`
  - Tree structure: Group nodes → Session nodes with status indicators
  - Context menu: Connect, Edit, Delete, Move to Group
  - Double-click session → connect (create terminal tab)
  - Create `QuickConnectViewModel`:
    - Parses `username@hostname:port` format, default port 22
    - Connect button → temporary SessionProfile → terminal tab
    - Input validation
  - Create `RecentConnectionsViewModel`:
    - Last 10 connections from `AppState.RecentConnections`
    - Click → reconnect, Clear button
  - Wire into `SidebarViewModel` from Task 10
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement import/export sessions
  - Do NOT implement session tags or advanced filtering

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: TreeView with hierarchical templates, context menus
  - **Skills**: [`frontend-ui-ux`]

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 6 (with Tasks 12, 14)
  - **Blocks**: None
  - **Blocked By**: Tasks 3, 10

  **References**:

  **Pattern References**:
  - Task 3's `ISessionRepository`, `SessionProfile`, `ServerGroup`
  - Task 10's `SidebarViewModel`
  - Task 2's `SshConnectionService`
  - `PulseTerm-zh.pen`: Sidebar session tree with groups, status dots, quick connect

  **External References**:
  - Avalonia TreeView: https://docs.avaloniaui.net/docs/reference/controls/treeview
  - Avalonia HierarchicalDataTemplate

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=SessionTree"` → PASS (minimum 8 tests)
  - [ ] Tests: load groups, add/delete session, move between groups, quick connect parsing, recent connections
  - [ ] Status dots use theme tokens (PulseStatusConnected/Connecting/Disconnected)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Session tree and quick connect tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=SessionTree" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 8+ tests pass
    Expected Result: All session tree tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add session tree and quick connect`
  - Files: `src/PulseTerm.App/Views/SessionTree*`, `src/PulseTerm.App/Views/QuickConnect*`, `src/PulseTerm.App/ViewModels/SessionTree*`, `src/PulseTerm.App/ViewModels/QuickConnect*`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=SessionTree"`

---

- [ ] 14. Settings Service + Connection Profiles

  **What to do**:
  - Create `SettingsViewModel` — Language, Theme, Font, FontSize, ScrollbackLines, DefaultPort
  - Create `SettingsView.axaml` — modal dialog
  - Create `ConnectionProfileViewModel` — form for session profiles:
    - Host, Port, Username, Auth method (Password/PrivateKey), Password/Key fields
    - Group assignment, Test Connection button, Save/Cancel
  - Create `HostKeyPromptViewModel` — dialog for first connection:
    - Shows host, key type, fingerprint (SHA256)
    - Trust / Reject buttons, warning mode for changed keys
  - Wire host key events from `SshConnectionService` → `HostKeyPromptView`
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement runtime language hot-switch (restart required)
  - Do NOT encrypt passwords
  - Do NOT implement import/export settings

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Form-heavy UI with modals, conditional fields
  - **Skills**: [`frontend-ui-ux`]

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 6 (with Tasks 12, 13)
  - **Blocks**: None
  - **Blocked By**: Tasks 3, 11

  **References**:

  **Pattern References**:
  - Task 3's `ISettingsService`, `AppSettings`
  - Task 8's `IThemeService`
  - Task 11's `IHostKeyService`
  - Task 2's `SshConnectionService` — test connection

  **External References**:
  - Avalonia file picker: `StorageProvider.OpenFilePickerAsync()`
  - Avalonia dialog: https://docs.avaloniaui.net/docs/basics/user-interface/windowing/dialogs

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=Settings"` → PASS (minimum 6 tests)
  - [ ] Tests: settings load/save, profile validation, test connection, host key prompt
  - [ ] Auth method toggle switches between Password/PrivateKey fields

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Settings and profile tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=Settings" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 6+ tests pass
    Expected Result: All settings tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(core): add settings and connection profiles`
  - Files: `src/PulseTerm.App/Views/Settings*`, `src/PulseTerm.App/Views/ConnectionProfile*`, `src/PulseTerm.App/Views/HostKeyPrompt*`, `src/PulseTerm.App/ViewModels/`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=Settings"`

---

- [ ] 15. SFTP File Browser Panel

  **What to do**:
  - Create `FileBrowserView.axaml` in `PulseTerm.App/Views/`:
    - Bottom panel (220px height, collapsible) below terminal area
    - Path breadcrumb showing current remote path (e.g., `/var/www/html`)
    - DataGrid/ListView with columns: File Name, Size, Permissions, Modified Time
    - File/folder icons (folder icon, file icon differentiated)
    - Toolbar: Upload button, Download button, Refresh button, New Folder, Delete
    - Double-click folder → navigate into
    - Double-click file → download
    - Sort by column header click
  - Create `FileBrowserViewModel` with ReactiveUI:
    - `CurrentPath` (reactive) — current remote directory
    - `Files` → `IObservableList<RemoteFileInfoViewModel>`
    - `SelectedFiles` → multi-select support
    - Commands: `NavigateTo(path)`, `GoUp`, `Upload`, `Download`, `Delete`, `CreateFolder`, `Refresh`
    - Uses `ISftpService` from Task 6
    - Loading state: shows spinner while listing directory
    - Error handling: permission denied, connection lost → show error message
  - Create `RemoteFileInfoViewModel` — wraps `RemoteFileInfo` with display formatting:
    - Size: human-readable (1.2 KB, 3.4 MB)
    - Permissions: `drwxr-xr-x` format
    - Modified: relative time ("2 hours ago") or absolute
  - Wire to active terminal tab's SSH session (SFTP client shares connection)
  - RED: Write ViewModel tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement drag-and-drop from OS file manager
  - Do NOT implement file editing/preview
  - Do NOT implement file search within remote filesystem

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: DataGrid/ListView with columns, sorting, file icons, breadcrumb navigation
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: File browser layout, DataGrid styling, breadcrumb pattern

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 7 (with Tasks 16, 17, 18)
  - **Blocks**: None
  - **Blocked By**: Tasks 6, 10 (needs SFTP service + UI shell)

  **References**:

  **Pattern References**:
  - Task 6's `ISftpService` — all SFTP operations
  - Task 6's `RemoteFileInfo` model — data source
  - Task 10's UI shell — panel hosting
  - `PulseTerm-zh.pen`: File browser panel at bottom showing `/var/www/html`, columns: 文件名/大小/权限/修改时间

  **External References**:
  - Avalonia DataGrid: https://docs.avaloniaui.net/docs/reference/controls/datagrid/

  **WHY Each Reference Matters**:
  - DataGrid: Primary control for file listing with sortable columns
  - .pen design: Column layout and file browser positioning reference

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=FileBrowser"` → PASS (minimum 6 tests)
  - [ ] Tests: list directory populates files, navigate into folder, go up, refresh, size formatting (bytes→human readable), permissions display
  - [ ] Panel is collapsible (toggle visibility)
  - [ ] Columns match .pen design: File Name, Size, Permissions, Modified Time

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: File browser ViewModel tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=FileBrowser" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 6+ tests pass
    Expected Result: All file browser tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add SFTP file browser panel`
  - Files: `src/PulseTerm.App/Views/FileBrowser*`, `src/PulseTerm.App/ViewModels/FileBrowser*`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=FileBrowser"`

---

- [ ] 16. File Transfer Panel with Progress

  **What to do**:
  - Create `FileTransferView.axaml` — floating/dockable panel (280px width from .pen):
    - List of active + completed transfers
    - Each transfer item shows: file name, direction (↑ upload / ↓ download), progress bar, percentage, speed (KB/s or MB/s), estimated time remaining
    - Completed transfers show checkmark + total time
    - Failed transfers show error icon + retry button
    - Cancel button on active transfers
    - Clear completed button
  - Create `FileTransferViewModel` with ReactiveUI:
    - `Transfers` → `IObservableList<TransferItemViewModel>`
    - Subscribes to `ITransferManager` from Task 6 for real-time updates
    - Commands: `CancelTransfer`, `RetryTransfer`, `ClearCompleted`
    - Progress updates via `IProgress<TransferProgress>` from Task 6
  - Create `TransferItemViewModel`:
    - `FileName`, `Direction`, `Progress` (0-100), `Speed` (formatted), `TimeRemaining`, `Status`
    - Reactive properties update in real-time during transfer
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement transfer queue priority/reordering
  - Do NOT implement bandwidth throttling
  - Do NOT implement resume interrupted transfers

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Progress bars, real-time updates, formatted display values
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: Progress bar design, transfer item layout, real-time data display

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 7 (with Tasks 15, 17, 18)
  - **Blocks**: None
  - **Blocked By**: Tasks 6, 10 (needs transfer manager + UI shell)

  **References**:

  **Pattern References**:
  - Task 6's `ITransferManager`, `TransferTask`, `TransferProgress`
  - `PulseTerm-zh.pen`: File transfer panel (280px floating) showing upload/download progress bars, speeds, completion status

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=FileTransfer"` → PASS (minimum 5 tests)
  - [ ] Tests: transfer added to list, progress updates, cancel transfer, clear completed, speed formatting
  - [ ] Progress bar and speed display update reactively

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: File transfer ViewModel tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=FileTransfer" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 5+ tests pass
    Expected Result: All file transfer tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add file transfer progress panel`
  - Files: `src/PulseTerm.App/Views/FileTransfer*`, `src/PulseTerm.App/ViewModels/FileTransfer*`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=FileTransfer"`

---

- [ ] 17. Tunnel Management Panel

  **What to do**:
  - Create `TunnelPanelView.axaml` — floating/dockable panel (320px width from .pen):
    - List of active tunnels per session
    - Each tunnel shows: name, type (Local/Remote), local port → remote address:port, status dot, bytes transferred
    - New Tunnel form: Type dropdown (Local Forward/Remote Forward), Local Host, Local Port, Remote Host, Remote Port, Name
    - Start/Stop toggle per tunnel
    - Delete tunnel button
  - Create `TunnelPanelViewModel` with ReactiveUI:
    - `Tunnels` → `IObservableList<TunnelItemViewModel>`
    - Uses `ITunnelService` from Task 7
    - `NewTunnelConfig` → form binding
    - Commands: `CreateTunnel`, `StopTunnel`, `StartTunnel`, `DeleteTunnel`
    - Validation: port range (1-65535), required fields
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement dynamic SOCKS forwarding
  - Do NOT implement tunnel auto-start on connection
  - Do NOT implement tunnel templates/presets

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Form panel with list management, status indicators
  - **Skills**: [`frontend-ui-ux`]

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 7 (with Tasks 15, 16, 18)
  - **Blocks**: None
  - **Blocked By**: Tasks 7, 10 (needs tunnel service + UI shell)

  **References**:

  **Pattern References**:
  - Task 7's `ITunnelService`, `TunnelConfig`, `TunnelInfo`
  - `PulseTerm-zh.pen`: Tunnel panel (320px) showing MySQL/Redis port forwards, new tunnel form with type/port fields

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=TunnelUI"` → PASS (minimum 5 tests)
  - [ ] Tests: create tunnel validates form, tunnel added to list, stop/start toggle, delete tunnel, port range validation
  - [ ] Tunnel list shows type, ports, status correctly

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Tunnel panel ViewModel tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=TunnelUI" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 5+ tests pass
    Expected Result: All tunnel UI tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add tunnel management panel`
  - Files: `src/PulseTerm.App/Views/Tunnel*`, `src/PulseTerm.App/ViewModels/Tunnel*`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=TunnelUI"`

---

- [ ] 18. Quick Commands Panel

  **What to do**:
  - Create `QuickCommandsView.axaml` — floating/dockable panel (300px width from .pen):
    - Search box at top (filters commands as you type)
    - Categorized command list: 系统监控 (System Monitor), 网络 (Network), Docker, 自定义 (Custom)
    - Each command shows: name, description, the actual command text
    - Click command → sends to active terminal
    - Add custom command button → small form (name, category, command text)
    - Edit/Delete existing commands
  - Create `QuickCommandsViewModel` with ReactiveUI:
    - `Commands` → `IObservableList<QuickCommandViewModel>` grouped by category
    - `SearchQuery` (reactive) → filters list
    - `SelectedCommand` → sends to active terminal via `ITerminalEmulator.UserInput` event
    - Commands: `ExecuteCommand`, `AddCommand`, `EditCommand`, `DeleteCommand`
    - Built-in defaults: `htop`, `top`, `df -h`, `free -m`, `netstat -tlnp`, `docker ps`, `docker stats`, `systemctl status`, `journalctl -f`
    - Custom commands persisted via `JsonDataStore` to `~/.pulseterm/quick-commands.json`
  - Create `QuickCommand` model: `Id`, `Name`, `Category`, `CommandText`, `Description`, `IsBuiltIn`
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement command templates with variables
  - Do NOT implement command scheduling
  - Do NOT implement command history sharing between sessions

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Searchable categorized list, grouping, inline form
  - **Skills**: [`frontend-ui-ux`]

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 7 (with Tasks 15, 16, 17)
  - **Blocks**: None
  - **Blocked By**: Task 10 (needs UI shell)

  **References**:

  **Pattern References**:
  - Task 3's `JsonDataStore` — persist custom commands
  - Task 12's `TerminalTabViewModel` — send command to active terminal
  - `PulseTerm-zh.pen`: Quick commands panel (300px) showing categorized list with search

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=QuickCommands"` → PASS (minimum 5 tests)
  - [ ] Tests: search filters commands, execute sends to terminal, add custom command persists, delete command, built-in defaults exist
  - [ ] Built-in commands include: htop, top, df -h, free -m, netstat -tlnp, docker ps, docker stats

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Quick commands ViewModel tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=QuickCommands" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 5+ tests pass
    Expected Result: All quick commands tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add quick commands panel`
  - Files: `src/PulseTerm.App/Views/QuickCommands*`, `src/PulseTerm.App/ViewModels/QuickCommands*`, `src/PulseTerm.Core/Models/QuickCommand.cs`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=QuickCommands"`

---

- [ ] 19. Status Bar

  **What to do**:
  - Create `StatusBarView.axaml` — bottom bar (24px height from .pen):
    - Left section: SSH connection info (user@host), connection status dot, latency (e.g., "12ms")
    - Center section: terminal type (xterm-256color), window size (120×36)
    - Right section: encoding (UTF-8), uptime
  - Create `StatusBarViewModel` with ReactiveUI:
    - Subscribes to active terminal tab's `SshSession` for connection info
    - `ConnectionInfo` (reactive) — "root@web-prod-01"
    - `Status` (reactive) — Connected/Disconnected with color
    - `Latency` (reactive) — from SSH keepalive response time
    - `TerminalType` — "xterm-256color"
    - `WindowSize` (reactive) — "120×36" from terminal emulator dimensions
    - `Encoding` — "UTF-8"
    - `Uptime` (reactive) — time since connection established
    - Updates when active tab changes
  - Wire into `MainWindowViewModel` from Task 10
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement remote CPU/Memory/Network stats (descoped per Metis review)
  - Do NOT implement customizable status bar sections

  **Recommended Agent Profile**:
  - **Category**: `unspecified-low`
    - Reason: Simple reactive data display bar — no complex logic
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: Status bar layout matching .pen pixel spec

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 8 (with Tasks 20, 21)
  - **Blocks**: None
  - **Blocked By**: Task 12 (needs terminal tab for session data)

  **References**:

  **Pattern References**:
  - Task 12's `TerminalTabViewModel` — source for connection info
  - Task 10's `MainWindowViewModel` — hosting container
  - `PulseTerm-zh.pen`: Status bar (24px) showing SSH info, latency, terminal type, window size, encoding

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=StatusBar"` → PASS (minimum 4 tests)
  - [ ] Tests: status updates on tab switch, latency display, window size updates, uptime increments
  - [ ] Status bar shows connection info, latency, terminal type, window size, encoding
  - [ ] No CPU/Memory/Network stats (explicitly descoped)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Status bar ViewModel tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=StatusBar" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 4+ tests pass
    Expected Result: All status bar tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add status bar`
  - Files: `src/PulseTerm.App/Views/StatusBar*`, `src/PulseTerm.App/ViewModels/StatusBar*`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=StatusBar"`

---

- [ ] 20. Light Theme

  **What to do**:
  - Complete `LightTheme.axaml` from Task 8 with full light theme color values:
    - Invert/lighten all dark theme tokens appropriately
    - `PulseBgPage`: #FFFFFF (white background)
    - `PulseBgSidebar`: #F6F8FA (light gray)
    - `PulseBgTerminal`: #FFFFFF (white)
    - `PulseTextPrimary`: #24292F (dark text)
    - `PulseAccent`: #00A88A (slightly adjusted for light bg contrast)
    - All 23 tokens must have light variants
  - Verify theme switching works: `IThemeService.SetTheme("light")` → all UI updates
  - Test all views look correct in light theme:
    - Sidebar, tab bar, terminal toolbar, file browser, status bar
    - Connection status dots still visible against light background
    - Text contrast meets WCAG AA (4.5:1 ratio minimum)
  - RED: Write test for theme switching → FAIL
  - GREEN: Implement light theme colors → PASS

  **Must NOT do**:
  - Do NOT create a third theme
  - Do NOT add a theme editor

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: Color design for light theme variant, contrast verification
  - **Skills**: [`frontend-ui-ux`]
    - `frontend-ui-ux`: Color theory for light theme, contrast checking

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 8 (with Tasks 19, 21)
  - **Blocks**: None
  - **Blocked By**: Tasks 4, 8 (needs i18n + theme infrastructure)

  **References**:

  **Pattern References**:
  - Task 8's `DarkTheme.axaml` — reference for token keys, invert/lighten each
  - Task 8's `App.axaml` — ThemeDictionaries already wired

  **Acceptance Criteria**:

  - [ ] `dotnet build src/PulseTerm.App --warnaserror` → PASS
  - [ ] LightTheme.axaml contains all 23 design tokens with light-appropriate values
  - [ ] Theme switching from dark to light updates all visible UI elements
  - [ ] Status dots still distinguishable against light background

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Light theme builds and all tokens defined
    Tool: Bash
    Steps:
      1. Run: dotnet build src/PulseTerm.App --warnaserror
      2. Assert: exit code 0
      3. Count PulseXxx keys in LightTheme.axaml
      4. Assert: 23+ token definitions present
    Expected Result: Light theme complete with all tokens
    Evidence: Build output + grep count
  ```

  **Commit**: YES
  - Message: `feat(theme): add light theme variant`
  - Files: `src/PulseTerm.App/Themes/LightTheme.axaml`
  - Pre-commit: `dotnet build src/PulseTerm.App --warnaserror`

---

- [ ] 21. Keyboard Shortcuts + Terminal Copy/Paste

  **What to do**:
  - Implement keyboard shortcut system:
    - Terminal area: captures ALL keyboard input, forwards to SSH as raw bytes
    - In terminal: `Ctrl+Shift+C` = copy selected text, `Ctrl+Shift+V` = paste from clipboard
    - Global: `Ctrl+T` = new tab, `Ctrl+W` = close tab, `Ctrl+Tab` = next tab, `Ctrl+Shift+Tab` = prev tab
    - Global: `Ctrl+,` = open settings
    - macOS: Replace `Ctrl` with `Cmd` for global shortcuts (Cmd+T, Cmd+W, etc.)
    - macOS terminal: `Cmd+C` = copy (not SIGINT), use terminal's raw key handling for Ctrl+C = SIGINT
  - Implement terminal text selection:
    - Mouse click+drag selects text in terminal buffer
    - Double-click selects word
    - Triple-click selects line
    - Selection visible with highlight color from theme tokens
  - Implement clipboard integration:
    - Copy: selected terminal text → system clipboard
    - Paste: system clipboard → terminal input (as raw bytes)
    - Use `Avalonia.Input.Clipboard` API
  - Wire keyboard events in `TerminalTabView` — prevent Avalonia from intercepting terminal keys
  - RED: Write tests → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement customizable keybindings
  - Do NOT implement keyboard shortcuts configuration UI
  - Do NOT intercept Ctrl+C in terminal (it must send SIGINT to remote process)

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: Keyboard event handling across terminal and app contexts requires careful platform-specific logic
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 8 (with Tasks 19, 20)
  - **Blocks**: None
  - **Blocked By**: Task 12 (needs terminal tab integration)

  **References**:

  **Pattern References**:
  - Task 12's `TerminalTabView` — keyboard event handling location
  - Task 5's `ITerminalEmulator.UserInput` — where key bytes are sent

  **External References**:
  - Avalonia KeyBindings: https://docs.avaloniaui.net/docs/basics/user-interface/adding-interactivity
  - Avalonia Clipboard: `TopLevel.GetTopLevel(this)?.Clipboard`
  - Avalonia Issue #18482: macOS keyboard freeze — avoid HotKey on MenuItem

  **WHY Each Reference Matters**:
  - macOS keyboard freeze: Must use command bindings, NOT `HotKey` on `MenuItem` to avoid freezing
  - Clipboard API: Cross-platform clipboard access differs from WPF

  **Acceptance Criteria**:

  - [ ] `dotnet test --filter "Category=Keyboard"` → PASS (minimum 6 tests)
  - [ ] Tests: Ctrl+Shift+C copies selected text, Ctrl+Shift+V pastes, Ctrl+T creates new tab, Ctrl+W closes tab, tab switching, Ctrl+C in terminal sends byte 0x03 (not copy)
  - [ ] Terminal captures raw keyboard input (no Avalonia interception)
  - [ ] Text selection with mouse works (click-drag, double-click word, triple-click line)

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Keyboard shortcut tests pass
    Tool: Bash
    Steps:
      1. Run: dotnet test tests/PulseTerm.App.Tests --filter "Category=Keyboard" --logger "console;verbosity=detailed"
      2. Assert: exit code 0, 6+ tests pass
    Expected Result: All keyboard shortcut tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `feat(ui): add keyboard shortcuts and terminal copy/paste`
  - Files: `src/PulseTerm.App/Views/TerminalTabView.axaml.cs`, `src/PulseTerm.App/Services/KeyboardService.cs`
  - Pre-commit: `dotnet test tests/PulseTerm.App.Tests --filter "Category=Keyboard"`

---

- [ ] 22. Cross-Platform Packaging + Velopack Auto-Update

  **What to do**:
  - Configure cross-platform publishing in `PulseTerm.App.csproj`:
    - `dotnet publish -r win-x64 --self-contained -c Release` → Windows executable
    - `dotnet publish -r osx-arm64 --self-contained -c Release` → macOS binary
    - `dotnet publish -r linux-x64 --self-contained -c Release` → Linux binary
    - Enable trimming: `<PublishTrimmed>true</PublishTrimmed>` (test carefully)
    - Single file: `<PublishSingleFile>true</PublishSingleFile>`
    - App icon for each platform
  - Integrate Velopack for auto-update:
    - Install `Velopack` NuGet package
    - Configure update source URL (GitHub Releases or custom server)
    - Add `VelopackApp.Build().Run()` to Program.cs entry point
    - Create `IUpdateService` with check/download/apply methods
    - Create `UpdateService` using Velopack API
    - Show update notification in sidebar footer or dialog
  - macOS: `.app` bundle (Info.plist, icon.icns), code signing documented but not configured
  - Linux: `.desktop` file, AppImage or tar.gz distribution
  - Build scripts: `scripts/build-win.sh`, `scripts/build-mac.sh`, `scripts/build-linux.sh`
  - RED: Write tests for UpdateService → FAIL
  - GREEN: Implement → PASS

  **Must NOT do**:
  - Do NOT implement silent auto-updates, delta updates, or rollback
  - Do NOT set up actual code signing

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Cross-platform packaging with platform-specific configs, Velopack integration
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 9 (sequential)
  - **Blocks**: Task 23
  - **Blocked By**: All previous tasks (1-21)

  **References**:

  **External References**:
  - Velopack docs: https://docs.velopack.io/
  - dotnet publish: https://learn.microsoft.com/en-us/dotnet/core/deploying/
  - Avalonia packaging: https://docs.avaloniaui.net/docs/deployment/

  **Acceptance Criteria**:

  - [ ] `dotnet publish` succeeds for win-x64, osx-arm64, linux-x64
  - [ ] Velopack `VelopackApp.Build().Run()` in Program.cs
  - [ ] Build scripts exist for all 3 platforms
  - [ ] macOS Info.plist configured

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Cross-platform publish succeeds
    Tool: Bash
    Steps:
      1. Run: dotnet publish src/PulseTerm.App -r osx-arm64 --self-contained -c Release
      2. Assert: exit code 0, binary exists
    Expected Result: Publishable binary produced
    Evidence: Publish output captured
  ```

  **Commit**: YES
  - Message: `feat(deploy): add cross-platform packaging with Velopack`
  - Pre-commit: `dotnet publish src/PulseTerm.App -r osx-arm64 --self-contained -c Release`

---

- [ ] 23. Integration Testing + Cross-Platform QA

  **What to do**:
  - SSH integration tests via Docker:
    - Connect with password + private key → bash prompt
    - Run `echo "Hello World"` → output matches
    - Run `echo "你好世界"` → CJK correct
    - SFTP list, upload, download → SHA256 match
    - Local port forward → curl succeeds
    - Large output (1MB base64) → no crash
    - Disconnect + reconnect → works
  - Full test suite: `dotnet test tests/` → ALL pass
  - Headless UI tests: MainWindow renders, theme switching, i18n
  - Cross-platform publish verification (all 3 targets)

  **Must NOT do**:
  - Do NOT test against real production SSH servers

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Integration testing with Docker, end-to-end verification
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 9 (after Task 22)
  - **Blocks**: Task 24
  - **Blocked By**: Task 22

  **Acceptance Criteria**:

  - [ ] Docker SSH integration tests all pass
  - [ ] `dotnet test tests/` → ALL pass (100+ tests total)
  - [ ] Cross-platform publish succeeds for all 3 targets

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Full integration test suite
    Tool: Bash
    Steps:
      1. Run: docker compose -f docker-compose.test.yml up -d
      2. Wait: 10 seconds
      3. Run: dotnet test tests/ --logger "console;verbosity=detailed"
      4. Assert: exit code 0, all pass
      5. Run: docker compose -f docker-compose.test.yml down
    Expected Result: All tests pass
    Evidence: Test output captured
  ```

  **Commit**: YES
  - Message: `test: cross-platform integration testing`
  - Pre-commit: `dotnet test tests/`

---

- [ ] 24. Polish + Edge Cases + Final QA

  **What to do**:
  - Visual polish: verify all panels match .pen design, font rendering, icons
  - Edge cases:
    - Empty state (no saved sessions) → "Add your first connection" prompt
    - Corrupt JSON → reset to defaults + warning
    - SSH timeout → clear error + retry
    - Invalid UTF-8 → replacement character (U+FFFD)
    - Rapid connect/disconnect → no race conditions
    - Window state persistence (position/size save/restore)
  - Performance:
    - 5 simultaneous tabs → no UI lag
    - Terminal rendering: 60fps target
    - Memory: < 100MB base, < 50MB per tab
  - Cleanup: remove TODO/FIXME/HACK, verify version strings, final test run

  **Must NOT do**:
  - Do NOT add new features or refactor architecture

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: Thorough edge case hunting and polish across all modules
  - **Skills**: [`frontend-ui-ux`]

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 9 (final)
  - **Blocks**: None
  - **Blocked By**: Task 23

  **Acceptance Criteria**:

  - [ ] `dotnet build src/PulseTerm.sln --warnaserror` → 0 warnings, 0 errors
  - [ ] `dotnet test tests/` → ALL pass (100+ tests)
  - [ ] No TODO/FIXME/HACK in source code
  - [ ] App launches cleanly with no saved data
  - [ ] Memory under 100MB on startup

  **Agent-Executed QA Scenarios**:

  ```
  Scenario: Final build and full test suite
    Tool: Bash
    Steps:
      1. Run: dotnet build src/PulseTerm.sln --warnaserror
      2. Assert: 0 warnings, 0 errors
      3. Run: dotnet test tests/
      4. Assert: ALL pass, 100+ total
    Expected Result: Clean build, all tests pass
    Evidence: Output captured

  Scenario: No leftover markers in source
    Tool: Grep
    Steps:
      1. Search: "TODO|FIXME|HACK" in *.cs under src/
      2. Assert: zero matches
    Expected Result: All temporary markers resolved
    Evidence: grep results
  ```

  **Commit**: YES
  - Message: `fix: polish, edge cases, and final QA`
  - Pre-commit: `dotnet build src/PulseTerm.sln --warnaserror && dotnet test tests/`

---

| After Task(s) | Message | Verification |
| 1 | `chore: scaffold PulseTerm solution with test infrastructure` | `dotnet build --warnaserror && dotnet test` |
| 2 | `feat(ssh): add SSH connection service with wrapper interfaces` | `dotnet test --filter "Category=SshConnection"` |
| 3 | `feat(core): add JSON data store and session models` | `dotnet test --filter "Category=DataStore"` |
| 4 | `feat(i18n): add localization infrastructure (zh-CN + en)` | `dotnet build --warnaserror` |
| 5 | `feat(terminal): validate SSH↔terminal bridge spike` | `dotnet test --filter "Category=TerminalBridge"` |
| 6 | `feat(sftp): add SFTP service with file operations` | `dotnet test --filter "Category=Sftp"` |
| 7 | `feat(tunnel): add port forwarding service` | `dotnet test --filter "Category=Tunnel"` |
| 8 | `feat(theme): add dark/light theme system with design tokens` | `dotnet build --warnaserror` |
| 9 | `feat(terminal): add custom scrollback buffer` | `dotnet test --filter "Category=Scrollback"` |
| 10 | `feat(ui): add main window shell with sidebar and tab bar` | `dotnet test --filter "Category=UI"` |
| 11 | `feat(ssh): add host key TOFU verification` | `dotnet test --filter "Category=HostKey"` |
| 12 | `feat(ui): integrate terminal control with toolbar` | `dotnet test --filter "Category=Terminal"` |
| 13 | `feat(ui): add session tree and quick connect` | `dotnet test --filter "Category=SessionTree"` |
| 14 | `feat(core): add settings and connection profiles` | `dotnet test --filter "Category=Settings"` |
| 15 | `feat(ui): add SFTP file browser panel` | `dotnet test --filter "Category=FileBrowser"` |
| 16 | `feat(ui): add file transfer progress panel` | `dotnet test --filter "Category=FileTransfer"` |
| 17 | `feat(ui): add tunnel management panel` | `dotnet test --filter "Category=TunnelUI"` |
| 18 | `feat(ui): add quick commands panel` | `dotnet test --filter "Category=QuickCommands"` |
| 19 | `feat(ui): add status bar` | `dotnet test --filter "Category=StatusBar"` |
| 20 | `feat(theme): add light theme variant` | `dotnet build --warnaserror` |
| 21 | `feat(ui): add keyboard shortcuts and terminal copy/paste` | `dotnet test --filter "Category=Keyboard"` |
| 22 | `feat(deploy): add cross-platform packaging with Velopack` | `dotnet publish` for all 3 platforms |
| 23 | `test: cross-platform integration testing` | Full test suite pass |
| 24 | `fix: polish, edge cases, and final QA` | Full test suite + Playwright QA |

---

## Success Criteria

### Verification Commands
```bash
# Build without warnings
dotnet build src/PulseTerm.sln --warnaserror
# Expected: Build succeeded. 0 Warning(s). 0 Error(s).

# All tests pass
dotnet test tests/ --logger "console;verbosity=normal"
# Expected: All tests passed.

# Cross-platform publish
dotnet publish src/PulseTerm.App -r win-x64 --self-contained -c Release
dotnet publish src/PulseTerm.App -r osx-arm64 --self-contained -c Release
dotnet publish src/PulseTerm.App -r linux-x64 --self-contained -c Release
# Expected: All produce runnable binaries

# App launches (macOS)
./publish/osx-arm64/PulseTerm.App
# Expected: Main window opens with sidebar, tab bar, terminal area
```

### Final Checklist
- [ ] All 7 UI modules from design implemented (sidebar, terminal, file browser, file transfer, tunnels, quick commands, status bar)
- [ ] SSH connection works with password and private key (RSA/ED25519/ECDSA)
- [ ] Terminal emulates xterm-256color with scrollback
- [ ] CJK characters render correctly (double-width)
- [ ] SFTP file browser lists, uploads, downloads files
- [ ] Local + remote port forwarding works
- [ ] Host key TOFU stores and verifies fingerprints
- [ ] Dark theme matches design tokens exactly
- [ ] Light theme available
- [ ] zh-CN + en localization working
- [ ] Velopack auto-update configured
- [ ] All "Must NOT Have" items are absent
- [ ] All tests pass on all 3 platforms
