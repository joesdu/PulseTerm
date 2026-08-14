# VelaShell

> A modern, cross-platform SSH terminal client built for sysadmins and developers.

[简体中文](README.md) · **English**

VelaShell is a desktop terminal application built with .NET 11 and Avalonia, running on Windows, Linux and macOS. It ships its own VT terminal engine, SSH/SFTP/FTP connectivity, local shell tabs, jump hosts (ProxyJump), two-step authentication with host fingerprint verification, port-forwarding tunnels, grouped session management, the in-house VelaDock split/dock workspace, remote resource monitoring and traceroute, a command palette and an eleven-page settings centre — plus a **dual-mode plugin system** (in-process / isolated process) and a first-party **AI assistant plugin**. Everything is persisted, encrypted, into an embedded SonnetDB database. The goal is a **keyboard-first, information-dense, snappy** experience for heavy remote work.

---

## 🪶 About the name

**Pronunciation**: `/ˈveɪlə ʃɛl/` — say it as **"VAY-la shell"**, stress on the first syllable.

**Meaning**: **Vela** + **Shell**.

- **Vela** — Latin for "sails". Vela is a southern constellation which, together with Carina (the keel) and Puppis (the stern), was split out of Argo Navis — the ship Jason and the Argonauts sailed in search of the Golden Fleece. It carries the sense of **setting sail for distant shores**.
- **Shell** — the command-line shell, and the heart of this app: a terminal attached to a remote host.

Together, VelaShell means **"a terminal as your sail, riding the signal winds to remote hosts"**. The icon distills that idea: a dark `>_` prompt on a teal gradient rounded square.

### At a glance

| Item | Detail |
|------|--------|
| **Name** | VelaShell |
| **Pronunciation** | `/ˈveɪlə ʃɛl/` (VAY-la shell) |
| **Category** | Cross-platform SSH / SFTP / FTP terminal client |
| **Current version** | `v0.0.1-dev` (active development; single source of truth in `Directory.Build.props`, overridden at release time from the Release tag via `-p:Version`) |
| **Platforms** | Windows 10 / 11 · Linux · macOS (x64 / arm64) |
| **Runtime** | .NET 11 + Avalonia 12.1, published self-contained (no runtime install required) |
| **UI languages** | English / 简体中文 / 繁體中文 / 日本語 / 한국어 (1234 keys, all five at parity) |
| **License** | Dual: [AGPL-3.0](LICENSE) / [Commercial](LICENSE-COMMERCIAL.md) · © 2026 VelaShell authors and contributors |

---

## ✨ Features

### Terminal and connectivity

- **In-house VT terminal engine**  
  A full DEC ANSI / VT / Xterm state machine: 256 colours, true colour, DEC line-drawing glyphs, primary/alternate screens, scroll regions, application cursor keys, mouse protocols, CJK double-width characters and on-the-fly encoding switching. Ten terminal profiles are built in (vt52/100/102/220/320/340/420/520/xterm/xterm-256color), defaulting to xterm-256color. The terminal is a custom-drawn Avalonia control — glyphs, selection and scrolling are all rendered by us.

- **SSH, SFTP and local shells**  
  Shell sessions, SFTP transfers and port forwarding are powered by [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh) (a fully managed, async-first .NET SSH library). Password and private-key auth are supported; when credentials are missing the app walks a two-step authentication flow (username → auth method) and lets you retry in place after a failure. **Local terminal tabs** auto-detect pwsh / PowerShell / CMD / WSL / Git Bash — implemented on ConPTY, so they are **Windows-only for now**.

- **Jump hosts (ProxyJump)**  
  A session can reference another saved profile as its jump host, chained up to 5 hops with cycle detection. Chains are built hop by hop through Tmds.Ssh's native `SshProxy`, and fingerprints are verified per logical host at every hop.

- **ZMODEM (rz / sz)**  
  Transfer files straight from the terminal: the ZMODEM lead-in sequence is detected in the output stream, the channel is handed to our own ZMODEM protocol engine, and the terminal is restored afterwards. Both directions, transport agnostic (SSH or local ConPTY). Set `VELASHELL_ZMODEM_TRACE=1` for frame-level tracing.

- **FTP / FTPS**  
  Built on [FluentFTP](https://github.com/robinrodricks/FluentFTP) (MIT) with a connection pool for concurrent transfers (a single FTP control connection can only run one command at a time), reusing exactly the same dual-pane file browser and transfer stack as SFTP. Rationale in [`docs-en/ftp-client-feasibility-research.md`](docs-en/ftp-client-feasibility-research.md).

- **Dedicated SFTP tabs and remote file editing**  
  A profile can be SSH or SFTP; SFTP tabs live as their own documents in the dock workspace with local/remote dual-pane browsing, drag-and-drop transfers, resumable transfers and a transfer queue. Remote files open in the built-in editor (AvaloniaEdit, syntax highlighting by extension, plus five hand-written definitions for Shell/YAML/INI/Log/Dockerfile re-skinned for dark themes) and upload on save; you can also hand a file to an external editor and have changes uploaded when it hits disk.

- **Host key trust**  
  First connect records the fingerprint (TOFU) by default, or you can switch to manual confirmation (trust always / trust once / cancel). A changed fingerprint aborts the connection immediately, defeating man-in-the-middle attacks; both SSH and SFTP channels are checked. Trusted hosts can be reviewed and removed in Settings (with address masking so screenshots don't leak them).

- **Port-forwarding tunnels**  
  Local (`-L`), remote (`-R`) and dynamic SOCKS5 (`-D`) forwarding, managed in one place.

### Workspace and operations tooling

- **VelaDock — in-house draggable split view**  
  A dock framework written from scratch with zero third-party dependencies (it replaced Dock.Avalonia): tabs, five-zone edge splitting, merging across groups and tab reordering, so multiple terminals can run side by side.

- **Session management and import**  
  The explorer keeps connection profiles in groups (create/edit/delete/double-click to connect); the sidebar's "recent connections" shows name-group plus relative time, survives restarts, and reconnects on double-click. Existing sessions can be imported from **WinSCP** and **Xshell**.

- **Resource monitor**  
  Live charts for the remote host: CPU (overall, per core, time breakdown, clock, context switches), memory (including cache/buffers/swap), disks (devices, mount points, filesystems, capacity), network connections and the process list.

- **Process manager / traceroute / connection diagnostics**  
  Inspect and kill remote processes; visualise `traceroute` with geographic data (design in [`docs-en/route-tracing-design.md`](docs-en/route-tracing-design.md)); step-by-step diagnostics when a connection fails.

- **Quick commands and command palette**  
  Send saved command snippets to the current session in one click; `Ctrl+P` / `Ctrl+K` opens the command palette with fuzzy subsequence search over recent sessions, all saved sessions and global commands.

- **Session recording and replay**  
  Optionally record terminal output (stored in SonnetDB's time-series engine, pruned by the log retention setting). The replay centre offers timeline scrubbing, 1x/2x/…/16x speeds and idle-gap skipping, and exports to asciinema-compatible asciicast v2 (`.cast`).

- **Line-number / timestamp gutters**  
  Optional gutters next to terminal output, independently toggled (also via shortcuts), with fold markers and blank-gap handling.

### Plugin system

- **Dual-mode plugin hosting**  
  A plugin can be loaded **in-process** (isolated in a collectible `AssemblyLoadContext`, its UI docked directly into the workspace) or run in a **separate process**, `VelaShell.PluginHost` (custom named-pipe RPC, so a crash never takes down the app, with heartbeats, self-healing restarts and idle recycling). The mode is declared in the plugin manifest; both share the same SDK contract.

- **Capability APIs**  
  Plugins reach host functionality through `IPluginContext`: `Sessions` (enumerate/observe sessions), `Terminal` (read output, write input), `RemoteFs` (remote file read/write and directory listing), `RemoteExec` (run remote commands), `Storage` and `TimeSeries` (per-plugin private document and time-series storage), `Secrets` (host-encrypted secrets), `Commands` (register commands and entry points), `Events` (session/locale/theme events), `Ui` (panels as docked documents or standalone windows), `Clipboard` and `Log`. Dangerous capabilities are granted one by one through a permission dialog.

- **Packaging and management**  
  Plugins ship as `.vpx` packages and can be installed, enabled, disabled and uninstalled from a dedicated plugin manager window; uninstalling also purges the plugin's private data (its SonnetDB namespace and data directory). The SDK ships test doubles (`VelaShell.PluginSdk.Testing`) so plugins can be tested headlessly. Full blueprint in [`docs-en/plugins/`](docs-en/plugins/) (15 design documents + a [dev guide](docs-en/plugins/dev-guide.md) + a [status overview](docs-en/plugins/STATUS.md)).

- **AI assistant plugin (first-party)**  
  Multi-provider streaming chat across three wire protocols — OpenAI Responses, OpenAI Chat Completions-compatible and Anthropic Messages — covering OpenAI, Grok, Ollama and relay endpoints, with your own base URL and API key (keys go into the host's encrypted secret store). **Agent mode** runs a Microsoft.Extensions.AI `FunctionInvokingChatClient` tool loop bridged to sessions / terminal / remoteExec / remoteFs, with per-command approval for dangerous operations, and can attach custom **MCP servers** (stdio / HTTP) for extra tools. Conversations are persisted to the plugin's private time-series store: browse history, resume a conversation, delete one or clear all; `↑`/`↓` recalls previous prompts and `@` opens a remote file picker for the selected session, attaching file contents to the message. The composer itself is an editor with **Markdown highlighting**, where `@` references render as themed short-name chips (full path on hover), and message bubbles are rendered as Markdown.

### Data, appearance and updates

- **Embedded SonnetDB storage**  
  Everything persistent (profiles, groups, settings, known_hosts, snippets, connection history, audit log, recordings, plugin data) lives in a local embedded [SonnetDB](https://github.com/IoTSharp/SonnetDB) multi-model database: document collections for business data, the time-series engine for recent connections, audit entries and recording chunks. Connection passwords and key passphrases are written with **AES-256-GCM** encryption.

- **GitHub Gist cloud sync**  
  Settings, connection profiles (including groups and tunnels) and snippets sync to a private Gist under your own account for seamless multi-device roaming. Every sync is a revisitable revision and any revision can be restored. Optional passphrase-based end-to-end encryption (PBKDF2 + AES-256-GCM); with encryption off, credentials are never uploaded.

- **Settings centre**  
  Eleven pages: General, Appearance, Terminal, Key management, Shortcuts, File transfer, Security audit, Snippets, Cloud sync, About, and Support & donate. Key management enumerates `~/.ssh` keys (type + SHA256 fingerprint), generates RSA key pairs, and imports/copies public keys.

- **Dark / light / system themes**  
  Fully tokenised design with no hard-coded colours and runtime switching; unless customised, the terminal palette follows the theme (dark = Dracula, light = Solarized Light). Scrollbars follow the Windows 11 two-state model — a thin resting line that expands into a track with arrows on hover.

- **Bundled terminal font**  
  Cascadia Mono ships with the app in four weights as the default terminal font for identical glyphs on all three platforms; CJK falls back to system fonts.

- **Live status bar**  
  Connection state, latency, uptime, terminal size, encoding and CPU / memory / network throughput at a glance.

- **Desktop integration**  
  Single instance (launching again raises the existing window), minimise to tray, launch at login, and a hardware-acceleration switch (turning it off saves roughly 170 MB of resident memory).

---

## 🖥️ Platform support

| Platform | Architecture | Status |
|----------|--------------|--------|
| Windows 10 / 11 | x64 / arm64 | ✅ Fully supported (portable zip, in-app updates; also on Microsoft Store as MSIX) |
| Linux | x64 / arm64 | ✅ Fully supported (portable tar.gz) |
| macOS | x64 / arm64 | ✅ Fully supported (tar.gz + drag-install `.dmg`, unsigned/not notarised) |

Releases are **self-contained**, so no .NET runtime is required on the target machine. [`scripts/publish-all.ps1`](scripts/publish-all.ps1) produces every platform package in one go — see [Build & release](#-build--release).

---

## 🚀 Getting started

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) **11.0.0 or newer** (pinned by `global.json` with `rollForward: latestFeature`; currently built with `11.0.100-preview.x`)
- (Optional) Docker, to run the local SSH test server

> ⚠️ The repo targets **net11.0** with `EnablePreviewFeatures` and `runtime-async=on` (see `Directory.Build.props`), so building requires a .NET 11 preview SDK. If you need an LTS baseline, roll `<TargetFramework>` in `Directory.Build.props` and `global.json` back to net10 together.

### Clone and build

```bash
git clone https://github.com/joesdu/VelaShell
cd VelaShell

# Build the whole solution (including plugins and the plugin host)
dotnet build

# Or just the desktop entry project
dotnet build src/VelaShell/VelaShell.csproj
```

> Plugin projects mirror their output into `plugins/<plugin-dir>/` under the app's output directory after each build, so F5 always picks up the latest plugin. **Building while the app is running fails on locked files** — close the app first.

### Run

```bash
# Development (hot reload)
dotnet watch run --project src/VelaShell/VelaShell.csproj

# Publish a standalone Windows executable
dotnet publish src/VelaShell/VelaShell.csproj -c Release -r win-x64 --self-contained true
```

### Start the test SSH server

```bash
docker-compose -f docker-compose.test.yml up
# username: testuser, password: testpass
# port: 2222
```

### Where data lives

| Content | Location |
|---------|----------|
| SonnetDB data directory (profiles/groups/settings/known_hosts/history/audit/recordings/plugin data) | `%LocalAppData%/VelaShell/sonnetdb` |
| Credential encryption key (AES-256) | `%LocalAppData%/VelaShell/secret.key` |
| User-installed plugins (`.vpx`) | `%LocalAppData%/VelaShell/plugins` |
| SSH key pairs (Key management page) | `~/.ssh` |

> Legacy JSON configuration (`sessions.json` / `settings.json` …) is imported into SonnetDB on first run and renamed to `*.migrated.bak`.

---

## 📦 Build & release

```bash
# Produce every platform package in one run (output in publish/)
pwsh scripts/publish-all.ps1
```

Artifacts cover Windows x64/arm64 (portable zip) plus macOS and Linux x64/arm64 (tar.gz), all self-contained — unpack anywhere and run, no .NET required. Each package carries the isolated-plugin host process `VelaShell.PluginHost` and the bundled `plugins/` directory alongside the main app. The macOS `.dmg` drag-install image is produced only on CI's macOS runner (`hdiutil`/`iconutil`/`codesign` are macOS-only tools); **the updater always consumes the tar.gz**, while the dmg exists purely for manual installation.

> Microsoft Store (MSIX) installs are updated by the Store, so in-app update actions are hidden there. The Store build lives under the read-only `WindowsApps` directory and its data folder is redirected to a package-private location, so **its settings, sessions and keys are separate from the portable build's**.

**In-app updates**: Settings → About → Check for updates. The app reads the `latest.json` manifest from GitHub Releases, downloads the matching archive into a staging directory inside the app folder, verifies SHA-256, unpacks it, and then lets an external swap process — which only starts after the app exits — replace the files and relaunch. By then nothing in the app directory is locked, so no undeletable leftovers remain. That "external process" *is* the freshly unpacked new version (releases are self-contained and **not** single-file, so they run straight from disk), which is why no separate updater has to be shipped. Updates happen wherever the app is installed, with no location requirement; the `%LocalAppData%/VelaShell` data directory is completely isolated from the update flow, so upgrades and rollbacks never touch user data. A failed swap rolls back to the previous version automatically, and if the flow is interrupted, "Repair update state" on the About page resets it. The update channel (stable / preview) is switchable in Settings.

**CI/CD**: [`.github/workflows/release.yml`](.github/workflows/release.yml) triggers when a GitHub Release is published and builds on all three native runners in parallel (the version comes from the Release tag via `-p:Version`, so releasing needs no code changes), then attaches `SHA256SUMS.txt` and the `latest.json` update manifest to the Release. The same pipeline also produces an **MSIX** for Microsoft Store submission (deliberately unsigned — the Store signs it with its own certificate after certification).

> The earlier WiX MSI and Velopack installers were removed in `241c2a2`: installing into Program Files makes the app directory read-only, degrading in-app updates to "please download manually", which conflicts with the portable self-update model.

---

## 🏗️ Project layout

```text
VelaShell/
├── src/
│   ├── VelaShell/                  # Desktop entry point, DI composition root, XAML views, VelaDock, global styles
│   ├── VelaShell.Terminal/         # In-house VT engine and the Avalonia rendering control
│   ├── VelaShell.Presentation/     # Cross-cutting view models, workflows and the Presentation DI module
│   ├── VelaShell.Controls/         # Reusable control library and theme tokens
│   ├── VelaShell.Core/             # Domain models, service contracts, persistence abstractions, localisation (UI-free)
│   ├── VelaShell.Infrastructure/   # SSH/SFTP/FTP/tunnels, SonnetDB persistence, AES-256 credential encryption,
│   │                               # Gist sync, plugin management and capability implementations
│   └── VelaShell.PluginHost/       # Host process for isolated plugins (named-pipe RPC, SDK contract only)
├── plugin-sdk/
│   ├── VelaShell.PluginSdk/        # Plugin contracts and capability interfaces (the one assembly host and plugin share)
│   └── VelaShell.PluginSdk.Testing/# Test doubles (fake sessions / fake remote FS / fake context)
├── plugins/
│   ├── VelaShell.Plugin.Ai/        # First-party AI assistant plugin (multi-provider + agent + MCP)
│   └── VelaShell.Plugin.HelloWorld/# Sample plugin running in isolated mode
├── tests/                          # 7 MSTest projects: unit, integration, UI and smoke tests
├── docs/                           # Architecture, UI specs, settings audit, plugin blueprint, interaction notes
├── scripts/publish-all.ps1         # One-shot cross-platform publish script
├── docker-compose.test.yml         # Local SSH test server
├── global.json                     # SDK version pin
├── Directory.Build.props           # Repo-wide version and shared MSBuild properties
├── src/Directory.Packages.props    # Central NuGet version management
└── VelaShell.slnx                  # Visual Studio solution
```

> Every source and test project has its own `README.md` describing its architecture, directory responsibilities and dependencies. The entry project is named `VelaShell` (`VelaShell.App` in older docs is a stale alias).

---

## 🧩 Architecture highlights

- **Strict layering**: dependencies flow `App(VelaShell) → Presentation / Controls / Infrastructure → Core`. Core depends on no UI framework, so it is independently testable and reusable.
- **Interfaces first**: services are injected through interfaces, which keeps mocking and unit testing straightforward.
- **Single composition root**: all DI registration is centralised in [`src/VelaShell/App.axaml.cs`](src/VelaShell/App.axaml.cs), with each layer contributing via `*ServiceCollectionExtensions`.
- **Custom rendering**: the terminal draws glyphs, selection and scrolling directly in a custom Avalonia control instead of depending on abandoned third-party terminal controls.
- **In-house docking**: VelaDock separates its model layer (plain INPC, unit-testable) from its controls; dragging, splitting and tab reordering are all ours, with zero third-party dock dependencies.
- **Plugin isolation**: each in-process plugin gets a collectible `AssemblyLoadContext` and resolves its dependencies from its own `deps.json`; only the SDK contract and `Avalonia*` framework assemblies fall back to the host, which keeps types identical across the boundary. Plugins that need stronger isolation run in a separate process over custom named-pipe RPC.
- **Tokenised design**: colours, fonts and spacing all live in resource dictionaries, enabling theming and rebranding.
- **One persistence engine**: a single embedded SonnetDB instance serves both document (configuration/business data) and time-series (history/audit/recordings/plugin data) models — interfaces in Core, implementation in Infrastructure, flushed on exit; legacy JSON configuration migrates automatically on first run.
- **Secure defaults**: credentials encrypted at rest (AES-256-GCM plus a local key file), TOFU host fingerprint verification, "remember password" disableable per connection, and per-capability consent for plugins.

---

## 🧪 Tests

The repo carries an MSTest suite covering domain models, the VT engine, view models, the plugin system and integration scenarios (7 test projects, including real two-process plugin e2e tests and headless UI tests).

```bash
# Run everything
dotnet test

# Only the terminal engine
dotnet test tests/VelaShell.Terminal.Tests/

# Verbose output
dotnet test --logger "console;verbosity=detailed"
```

| Test project | Scope |
|--------------|-------|
| `VelaShell.Core.Tests` | Domain models, SFTP and the transfer queue, tunnels, sync encryption, ZMODEM (including lrzsz interop) |
| `VelaShell.Terminal.Tests` | VT parsing, emulation, encodings, character widths, gutter folding and ZMODEM routing |
| `VelaShell.Presentation.Tests` | View-model workflows and commands |
| `VelaShell.Infrastructure.Tests` | SonnetDB persistence, credential encryption, ConPTY, SSH key management, plugin management and cross-process RPC |
| `VelaShell.Controls.Tests` | Custom control behaviour |
| `VelaShell.Plugin.Ai.Tests` | AI plugin: toolbox approval gate, capability bridging, settings/secret storage, chat history, `@` reference syntax and headless panel interaction |
| `VelaShell.Tests` | Window-level view models, authentication flow, plugin panels and theme tokens, integration and smoke tests |

> Integration tests bail out early when their environment is missing: `SshIntegrationTests` needs Docker plus the SSH server, `CrossPlatformPublishTests` needs `VELASHELL_PUBLISH_TESTS=1`.
>
> ⚠️ In headless UI tests, always use the **value-returning** overload — `Dispatch(async () => { …; return true; })`. `HeadlessUnitTestSession` has no `Func<Task>` overload, so a void-returning lambda yields a `Task<Task>` that is never awaited: the body stops at the first `await`, the test "passes", and every assertion failure is lost.

---

## 📚 Documentation

English translations of the design documents are linked below; the Chinese originals live under `docs/`.

- [`docs-en/architecture.md`](docs-en/architecture.md) — layering, dependency direction and the SonnetDB persistence strategy
- [`docs-en/architecture-design.md`](docs-en/architecture-design.md) — engineering refactor blueprint
- [`docs-en/plugins/`](docs-en/plugins/) — 15-part plugin blueprint + [dev guide](docs-en/plugins/dev-guide.md) + [status overview](docs-en/plugins/STATUS.md)
- [`docs-en/dock-replacement-plan.md`](docs-en/dock-replacement-plan.md) — replacing Dock.Avalonia with VelaDock
- [`docs-en/design-specs.md`](docs-en/design-specs.md) — UI visual specs (extracted frame by frame from Pencil)
- [`DESIGN.md`](DESIGN.md) — design system: colour/type/spacing tokens and component rules
- [`docs-en/interaction-and-ui-specs.md`](docs-en/interaction-and-ui-specs.md) — interaction logic and design tokens
- [`docs-en/settings-audit.md`](docs-en/settings-audit.md) — settings audit ledger and remediation log
- [`docs-en/tunnel-feature-planning.md`](docs-en/tunnel-feature-planning.md) — port-forwarding tunnel design
- [`docs-en/route-tracing-design.md`](docs-en/route-tracing-design.md) — traceroute and geographic visualisation
- [`docs-en/performance-and-memory-optimization-2026-07.md`](docs-en/performance-and-memory-optimization-2026-07.md) — performance and memory optimisation log
- [`docs-en/terminal-input-ordering-analysis.md`](docs-en/terminal-input-ordering-analysis.md) — serialising terminal input
- [`docs-en/sftp-dual-pane-winscp-gap-analysis.md`](docs-en/sftp-dual-pane-winscp-gap-analysis.md) — dual-pane SFTP vs WinSCP, item by item
- [`docs-en/ftp-client-feasibility-research.md`](docs-en/ftp-client-feasibility-research.md) — trade-offs behind FTP / FTPS support
- [`docs-en/telnet-and-serial-feasibility-research.md`](docs-en/telnet-and-serial-feasibility-research.md) — feasibility and work list for Telnet / serial sessions
- [`plan.md`](plan.md) — progress log, known issues and the backlog (the source of truth for day-to-day work)

---

## 🛠️ Tech stack

- **.NET 11** — target runtime (`net11.0`, preview features and `runtime-async` enabled)
- **Avalonia 12.1** — cross-platform XAML UI framework
- **ReactiveUI** — reactive MVVM
- **VelaDock (in-house)** — draggable split/dock layout with zero third-party dependencies
- **Tmds.Ssh** — SSH / SFTP / port forwarding / ProxyJump (fully managed, async-first)
- **FluentFTP** — FTP / FTPS client
- **ZMODEM (in-house)** — in-terminal rz/sz, protocol engine under `VelaShell.Core/ZModem/`
- **AvaloniaEdit** — remote file editor and the AI composer (syntax highlighting, inline reference chips)
- **SonnetDB** — embedded multi-model database (document + time series), the only persistence engine
- **Plugin runtime (in-house)** — collectible ALCs, a separate host process, named-pipe RPC and `.vpx` packaging
- **Microsoft.Extensions.AI / ModelContextProtocol** — unified model abstraction, agent tool loop and MCP client for the AI plugin
- **LiveMarkdown.Avalonia** — incremental Markdown rendering for AI chat (with Mermaid / LaTeX / SVG extensions)
- **In-house portable self-update** — `latest.json` manifest from GitHub Releases + SHA-256 verification + an external process that swaps and relaunches after exit (auto rollback on failure), location-independent and never touching the user data directory (`src/VelaShell/Services/Update/`)
- **MSTest** — unit testing framework
- **Central package management** — NuGet versions unified in `Directory.Packages.props`

---

## 🚧 Project status

The project is under active development.

**Working today**: terminal engine, SSH/SFTP, FTP/FTPS, ZMODEM, local shells, jump hosts, session management and import, authentication, tunnels, persistence, settings centre, cloud sync, session recording, resource monitor / process manager / traceroute, plus the **plugin system framework** (dual hosting modes, the full capability surface, UI extensions, heartbeat self-healing and idle recycling, per-plugin storage with uninstall cleanup, `.vpx` install/uninstall, SDK test doubles and developer docs) and the first-party **AI assistant plugin**.

**Not yet available**: Telnet / serial protocols and certificate authentication (feasibility and work list in [`docs-en/telnet-and-serial-feasibility-research.md`](docs-en/telnet-and-serial-feasibility-research.md)); the container-management plugin has not been started. Some settings are persisted but not yet wired to runtime behaviour.

The full completion matrix and backlog live in [`plan.md`](plan.md) §10–§12 and [`docs-en/plugins/STATUS.md`](docs-en/plugins/STATUS.md).

---

## 🤝 Contributing

Issues and pull requests are welcome. Before contributing, read [`docs-en/architecture.md`](docs-en/architecture.md) for the layering conventions and dependency direction; if you are writing a plugin, start with [`docs-en/plugins/dev-guide.md`](docs-en/plugins/dev-guide.md).

---

## 📄 License

VelaShell is **dual-licensed**:

- **[AGPL-3.0](LICENSE) (default)**: free to use, modify and distribute, but derivative works — including anything offered as a network service — **must release their complete source under the same license**, keeping copyright and donation notices intact. Stripping the project's identity and selling it closed-source is infringement, and will be pursued (DMCA takedowns / litigation).
- **[Commercial license](LICENSE-COMMERCIAL.md) (paid, on request)**: if you need closed-source integration or distribution, or corporate policy rules out AGPL, contact the author to purchase one (📧 <dygood@outlook.com>, subject line "Commercial License").

**Authenticity notice**: VelaShell itself is **free forever** for individuals and companies alike, and the only official distribution channel is this repository's GitHub Releases; any "paid VelaShell" from any other channel is pirated. The "VelaShell" name and logo are not covered by the open-source license — derivative versions must not use them to promote or sell.

By contributing you agree that your contribution is licensed under AGPL-3.0 and that the copyright holder may sublicense it under the commercial license (see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3).

---

> VelaShell — born for the command line.
