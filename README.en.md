# VelaShell

> A modern, cross-platform SSH terminal client built for sysadmins and developers.

[简体中文](README.md) · **English**

VelaShell is a desktop terminal application built with .NET 11 and Avalonia, running on Windows, Linux and macOS. It ships its own VT terminal engine, SSH/SFTP/FTP connectivity, local shell tabs, jump hosts (ProxyJump) and network proxies (HTTP / SOCKS5 / follow-system), two-step authentication with host fingerprint verification, port-forwarding tunnels, grouped session management, the in-house VelaDock split/dock workspace, remote resource monitoring and traceroute, a command palette and a twelve-page settings centre; it can also be launched externally by bastion hosts and SSO portals using Xshell's calling convention. On top of that sits a **dual-mode plugin system** (in-process / isolated process) and a first-party **AI assistant plugin**. Everything is persisted, encrypted, into an embedded SonnetDB database. The goal is a **keyboard-first, information-dense, snappy** experience for heavy remote work.

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
| **UI languages** | English / 简体中文 / 繁體中文 / 日本語 / 한국어 (identical key sets across all five, enforced by `LocalizedKeyUsageTests` / `UnusedLocalizedKeyTests` — both missing translations and orphaned keys turn the suite red) |
| **License** | Dual: [AGPL-3.0](LICENSE) / [Commercial](LICENSE-COMMERCIAL.md) · © 2026 VelaShell authors and contributors |

---

## ✨ Features

### Terminal and connectivity

- **In-house VT terminal engine**  
  A full DEC ANSI / VT / Xterm state machine: 256 colours, true colour, DEC line-drawing glyphs, primary/alternate screens, scroll regions, application cursor keys, mouse protocols, CJK double-width characters and on-the-fly encoding switching. Ten terminal profiles are built in (vt52/100/102/220/320/340/420/520/xterm/xterm-256color), defaulting to xterm-256color. The terminal is a custom-drawn Avalonia control — glyphs, selection and scrolling are all rendered by us. Selection supports linear and block modes, `Shift+click` to extend, and **disjoint multi-span selection** via `Ctrl+Shift+drag` (copy line 1 and line 3 in one go).

- **SSH, SFTP and local shells**  
  Shell sessions, SFTP transfers and port forwarding are powered by [Tmds.Ssh](https://github.com/tmds/Tmds.Ssh) (a fully managed, async-first .NET SSH library). Password and private-key auth are supported; when credentials are missing the app walks a two-step authentication flow (username → auth method) and lets you retry in place after a failure. **Local terminal tabs** auto-detect pwsh / PowerShell / CMD / WSL / Git Bash — implemented on ConPTY, so they are **Windows-only for now**.

- **Jump hosts (ProxyJump)**  
  A session can reference another saved profile as its jump host, chained up to 5 hops with cycle detection. Chains are built hop by hop through Tmds.Ssh's native `SshProxy`, and fingerprints are verified per logical host at every hop.

- **Network proxy**  
  A global proxy setting (Settings → Proxy): direct / follow system / HTTP CONNECT / SOCKS5, with proxy authentication and an option to let SOCKS5 resolve DNS proxy-side (so target hostnames never leak). It applies to **all outbound traffic** — SSH, FTP, and HTTP requests such as cloud sync and update checks.

- **Xshell-compatible launch (external invocation)**  
  VelaShell can be launched by third-party security clients using Xshell's (and SecureCRT's / PuTTY's) calling convention: the user clicks "open in terminal" on a bastion host or SSO portal, and the one-time credential is handed straight to VelaShell — the user never sees the password. Includes URL protocol registration and single-instance forwarding; threat model and credential handling in [`docs-en/xshell-compatible-login.md`](docs-en/xshell-compatible-login.md).

- **ZMODEM (rz / sz), XMODEM (rx / sx), YMODEM (rb / sb)**  
  Transfer files straight from the terminal. All three engines are in-house, bidirectional and transport agnostic (SSH or local ConPTY). ZMODEM takes over **automatically** once its lead-in sequence is spotted in the output stream, and the terminal is restored afterwards. XMODEM and YMODEM have no lead-in on the wire, so they are started **manually** from the command palette (Ctrl+P → "File Transfer") — run `sb`/`rb` on the remote first, then invoke the matching entry. YMODEM supports batches and the YMODEM-G streaming variant. Set `VELASHELL_TRANSFER_TRACE=1` (the old `VELASHELL_ZMODEM_TRACE=1` still works) for frame-level tracing.

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
  Plugins ship as `.vpx` packages and can be installed, enabled, disabled and uninstalled from a dedicated plugin manager window; uninstalling also purges the plugin's private data (its SonnetDB namespace and data directory). The SDK ships test doubles (`VelaShell.PluginSdk.Testing`) so plugins can be tested headlessly. Third-party developers get a debugger in one command (`vela-plugin dev init` → F5); see the [dev guide](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md), [CLI manual](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/cli.md), [SDK reference](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/sdk-reference.md) and [packaging and publishing](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/publishing.md); marketplace: <http://market.easilynet.top>. Full blueprint in [`docs-en/plugins/`](docs-en/plugins/) (15 design documents + a [status overview](docs-en/plugins/STATUS.md)).

- **AI assistant plugin (first-party)**  
  Multi-provider streaming chat across three wire protocols — OpenAI Responses, OpenAI Chat Completions-compatible and Anthropic Messages — covering OpenAI, Grok, Ollama and relay endpoints, with your own base URL and API key (keys go into the host's encrypted secret store). **Agent mode** runs a Microsoft.Extensions.AI `FunctionInvokingChatClient` tool loop bridged to sessions / terminal / remoteExec / remoteFs, with per-command approval for dangerous operations, and can attach custom **MCP servers** (stdio / HTTP) for extra tools. It also ships **web search and fetch** tools (a public SearXNG instance by default, swap in your own), so the model can look things up before answering. You can **interject** while the agent is running: a new message joins the queue and is picked up without waiting for the current turn to finish. Conversations are persisted to the plugin's private time-series store: browse history, resume a conversation, delete one or clear all; `↑`/`↓` recalls previous prompts and `@` opens a remote file picker for the selected session, attaching file contents to the message. The composer itself is an editor with **Markdown highlighting**, where `@` references render as themed short-name chips (full path on hover), and message bubbles are rendered as Markdown.

### Data, appearance and updates

- **Embedded SonnetDB storage**  
  Everything persistent (profiles, groups, settings, known_hosts, snippets, connection history, audit log, recordings, plugin data) lives in a local embedded [SonnetDB](https://github.com/IoTSharp/SonnetDB) multi-model database: document collections for business data, the time-series engine for recent connections, audit entries and recording chunks. Connection passwords and key passphrases are written with **AES-256-GCM** encryption.

- **GitHub Gist cloud sync**  
  Settings, connection profiles (including groups and tunnels) and snippets sync to a private Gist under your own account for seamless multi-device roaming. Every sync is a revisitable revision and any revision can be restored. Optional passphrase-based end-to-end encryption (PBKDF2 + AES-256-GCM); with encryption off, credentials are never uploaded.

- **Settings centre**  
  Twelve pages: General, Appearance, Terminal, Proxy, Key management, Shortcuts, File transfer, Security audit, Snippets, Cloud sync, About, and Support & donate. Key management enumerates `~/.ssh` keys (type + SHA256 fingerprint), generates RSA key pairs, and imports/copies public keys. The Shortcuts page is generated from `ShortcutCatalog` as the single source of truth, same as [`docs-en/keyboard-shortcuts.md`](docs-en/keyboard-shortcuts.md).

- **Dark / light / system themes**  
  Fully tokenised design with no hard-coded colours and runtime switching; unless customised, the terminal palette follows the theme (dark = Dracula, light = Solarized Light). Scrollbars follow the Windows 11 two-state model — a thin resting line that expands into a track with arrows on hover.

- **Bundled terminal font**  
  Cascadia Mono ships with the app in four styles (regular / bold / italic / bold-italic) as the default terminal font for identical glyphs on all three platforms; CJK falls back to system fonts.

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

# Build the whole solution (including the plugin host)
dotnet build

# Or just the desktop entry project
dotnet build src/VelaShell/VelaShell.csproj
```

> A clean clone builds with only the **AI plugin** this repo produces itself. To run Redis / S3 / Telnet alongside it locally, drop their plugin directories into `artifacts/plugins/` (or point elsewhere with `-p:VelaPluginsStageDir=<dir>`) — they are mirrored into `plugins/<plugin-dir>/` under the app's output directory on every build, so F5 picks them up. `dotnet publish` fails only when both the in-repo plugins and the staging directory are empty — a release package must not ship "a plugin system that looks present but has no plugins".
>
> Since 2026-08-22 the **release pipeline no longer preinstalls** Redis / S3 / Telnet (not everyone needs Redis, S3 or Telnet); users install them on demand from the [plugin marketplace](https://market.easilynet.top). Staging is purely a local affair: once staged, your own `dotnet publish` output will include those plugins, which only affects your own build.
>
> **Building while the app is running fails on locked files** — close the app first.

### Run

```bash
# Development (hot reload)
dotnet watch run --project src/VelaShell/VelaShell.csproj

# Publish a standalone Windows executable
dotnet publish src/VelaShell/VelaShell.csproj -c Release -r win-x64 --self-contained true
```

### Start the test SSH server

```bash
docker compose -f docker-compose.test.yml up -d
# username: testuser, password: testpass
# port: 2222
```

### Where data lives

| Content | Location |
|---------|----------|
| SonnetDB data directory (profiles/groups/settings/known_hosts/history/audit/recordings/plugin data) | `~/.velashell/sonnetdb` |
| Credential encryption key (AES-256) | `~/.velashell/secret.key` |
| Manually installed plugins (`.vpx`) | `~/.velashell/plugins` (first-party plugins remain in the application's `plugins/` directory) |
| Host self-registration (lets `vela-plugin` locate the install and check versions) | `~/.velashell/host.json` |
| Plugin development mounts and shadow copies | `~/.velashell/plugins.dev.txt`, `~/.velashell/dev-shadow/` |
| SSH key pairs (Key management page) | `~/.ssh` |

> Legacy JSON configuration (`sessions.json` / `settings.json` …) is imported into SonnetDB on first run and renamed to `*.migrated.bak`.
> When upgrading from the former data root, VelaShell verifies and migrates everything under `%LocalAppData%/VelaShell` into `~/.velashell`, then removes the former directory. Conflicting files already present in `~/.velashell` are preserved under `.migration-backup/localappdata/`.

---

## 📦 Build & release

```bash
# Produce every platform package in one run (output in publish/)
pwsh scripts/publish-all.ps1
```

Artifacts cover Windows x64/arm64 (portable zip) plus macOS and Linux x64/arm64 (tar.gz), all self-contained — unpack anywhere and run, no .NET required. Each package carries the isolated-plugin host process `VelaShell.PluginHost` plus a `plugins/` directory holding only the in-house AI plugin (Redis / S3 / Telnet have not been preinstalled since 2026-08-22 — users install them from the marketplace on demand). The macOS `.dmg` drag-install image is produced only on CI's macOS runner (`hdiutil`/`iconutil`/`codesign` are macOS-only tools); **the updater always consumes the tar.gz**, while the dmg exists purely for manual installation.

> Microsoft Store (MSIX) installs are updated by the Store, so in-app update actions are hidden there. The Store build lives under the read-only `WindowsApps` directory and its data folder is redirected to a package-private location, so **its settings, sessions and keys are separate from the portable build's**.

**In-app updates**: Settings → About → Check for updates. The app reads the `latest.json` manifest from GitHub Releases, downloads the matching archive into a staging directory inside the app folder, verifies SHA-256, unpacks it, and then lets an external swap process — which only starts after the app exits — replace the files and relaunch. By then nothing in the app directory is locked, so no undeletable leftovers remain. That "external process" *is* the freshly unpacked new version (releases are self-contained and **not** single-file, so they run straight from disk), which is why no separate updater has to be shipped. Updates happen wherever the app is installed, with no location requirement; the `~/.velashell` data directory is completely isolated from the update flow, so upgrades and rollbacks never touch user data. A failed swap rolls back to the previous version automatically, and if the flow is interrupted, "Repair update state" on the About page resets it. The update channel (stable / preview) is switchable in Settings.

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
├── tests/                          # 7 MSTest projects: unit, integration, UI and smoke tests
│   └── fixtures/                   # Fixture plugins for the plugin-runtime tests (not sample code; see its README)
├── docs/                           # Architecture, UI specs, settings audit, plugin blueprint, interaction notes
├── scripts/publish-all.ps1         # One-shot cross-platform publish script
├── docker-compose.test.yml         # Local SSH test server
├── global.json                     # SDK version pin
├── Directory.Build.props           # Repo-wide version and shared MSBuild properties
├── src/Directory.Packages.props    # Central NuGet version management
└── VelaShell.slnx                  # Visual Studio solution
```

> Every source and test project has its own `README.md` describing its architecture, directory responsibilities and dependencies. The entry project is named `VelaShell` (`VelaShell.App` in older docs is a stale alias).

### 🧩 Three repositories, one job each

Everything plugin-related now lives outside this repository. Three repositories, one job each:

| Repository | Owns | How it reaches this repo |
| --- | --- | --- |
| **joesdu/VelaShell** (this one) | The app + the host-side plugin runtime + the in-house AI plugin | — |
| **[joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain)** | Plugin SDK, `dotnet new` templates, the `vela-plugin` CLI | `VelaShell.PluginSdk` / `.Testing` **NuGet packages** |
| **[joesdu/velashell-plugins](https://github.com/joesdu/velashell-plugins)** | The Redis / S3 / Telnet plugins (plus the HelloWorld sample) | `velashell-plugins-<version>.zip` **release asset** |

> The split happened in two steps: on 2026-08-21 the SDK, toolchain and plugins all moved into the
> toolchain repository; on 2026-08-22 the plugins moved out again into their own repository, leaving
> the toolchain repo to own only the SDK and tooling. Older docs claiming "the plugins live in the
> toolchain repo" are obsolete.

**The AI plugin is the exception**: it lives here in
[`plugins/VelaShell.Plugin.Ai/`](plugins/VelaShell.Plugin.Ai) and is a first-party plugin built
and released together with the app — it is the one most tightly coupled to the host (it borrows
the host's AvaloniaEdit for its input box, must load in-process, and must compile against the
exact Avalonia version the host loads). The reasoning is in
[`plugins/README.md`](plugins/README.md).

The SDK contract is pinned in `src/Directory.Packages.props` and `tests/Directory.Packages.props`
(literal versions). Plugin binaries are not pinned here — they never enter a release package, so
there is no version to lock.

To run Redis / S3 / Telnet alongside the app on your own machine, drop their plugin directories
into the **staging directory** `artifacts/plugins/`: unpack the
[joesdu/velashell-plugins](https://github.com/joesdu/velashell-plugins) release asset
`velashell-plugins-<version>.zip` (its layout is exactly the installer's `plugins/` level), or
point straight at that repo's build output. Pass `-p:VelaPluginsStageDir=<dir>` to stage elsewhere.

> ⚠️ Do not stage `velashell-ai` — this repo already produces it, and two plugins with the same id
> make `PluginManager` mark the later one Invalid, which shows up as "the plugin mysteriously
> doesn't work".

To change the SDK contract, publish a (pre-release) package from the toolchain repository first,
then bump the `VelaShell.PluginSdk` version in `src/Directory.Packages.props`,
`tests/Directory.Packages.props` and
`plugins/VelaShell.Plugin.Ai/VelaShell.Plugin.Ai.csproj` together — this repository always
consumes the SDK as a NuGet package, never as a project reference.

**To write a plugin, read the toolchain repository's
[`docs-en/dev-guide.md`](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md)**;
what remains under `docs-en/plugins/` here is the host-side architecture blueprint.

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
| `VelaShell.Core.Tests` | Domain models, SFTP and the transfer queue, tunnels, sync encryption, ZMODEM / XMODEM / YMODEM (interop regressions against hand-built lrzsz and ymodem.txt wire bytes) |
| `VelaShell.Terminal.Tests` | VT parsing, emulation, encodings, character widths, gutter folding, plus ZMODEM auto-takeover and XMODEM / YMODEM manual-takeover routing |
| `VelaShell.Presentation.Tests` | View-model workflows and commands |
| `VelaShell.Infrastructure.Tests` | SonnetDB persistence, credential encryption, ConPTY, SSH key management, plugin management and cross-process RPC |
| `VelaShell.Controls.Tests` | Custom control behaviour |
| `VelaShell.Plugin.Ai.Tests` | AI plugin: toolbox approval gate, capability bridging, settings/secret storage, chat history, `@` reference syntax and headless panel interaction |
| `VelaShell.Tests` | Window-level view models, authentication flow, plugin panels and theme tokens, integration and smoke tests |

> Integration tests **bail out early** when their environment is missing: `SshIntegrationTests` and `TransferRealChannelIntegrationTests` (category `DockerIntegration`) need Docker plus the SSH server from `docker-compose.test.yml`, and the ZMODEM ones additionally need `lrzsz` to be installable inside the container; `CrossPlatformPublishTests` needs `VELASHELL_PUBLISH_TESTS=1`.
>
> ⚠️ **An early bail-out counts as "passed" in MSTest.** When the prerequisites are absent these tests go quietly green without executing a single line — the test result alone cannot tell you the difference. The gate itself has to be honest too: probing the TCP port is not enough, because Docker's port proxy **always** accepts the connection even when the sshd behind it cannot complete a handshake, so the fixture now caches one real SSH handshake and decides from that. To confirm they actually ran, look for `[SKIP]` lines in `TestContext`.
>
> ⚠️ In headless UI tests, always use the **value-returning** overload — `Dispatch(async () => { …; return true; })`. `HeadlessUnitTestSession` has no `Func<Task>` overload, so a void-returning lambda yields a `Task<Task>` that is never awaited: the body stops at the first `await`, the test "passes", and every assertion failure is lost.

---

## 📚 Documentation

English translations of the design documents are linked below; the Chinese originals live under `docs/`.

- [`docs-en/architecture.md`](docs-en/architecture.md) — layering, dependency direction and the SonnetDB persistence strategy
- [`docs-en/architecture-design.md`](docs-en/architecture-design.md) — engineering refactor blueprint
- [`docs-en/plugins/`](docs-en/plugins/) — 15-part plugin blueprint + [dev guide](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md) + [CLI manual](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/cli.md) + [SDK reference](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/sdk-reference.md) + [packaging and publishing](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/publishing.md) + [status overview](docs-en/plugins/STATUS.md)
- [`docs-en/dock-replacement-plan.md`](docs-en/dock-replacement-plan.md) — replacing Dock.Avalonia with VelaDock
- [`docs-en/design-specs.md`](docs-en/design-specs.md) — UI visual specs (extracted frame by frame from Pencil)
- [`DESIGN.md`](DESIGN.md) — design system: colour/type/spacing tokens and component rules
- [`docs-en/interaction-and-ui-specs.md`](docs-en/interaction-and-ui-specs.md) — interaction logic and design tokens
- [`docs-en/settings-audit.md`](docs-en/settings-audit.md) — settings audit ledger and remediation log
- [`docs-en/xshell-compatible-login.md`](docs-en/xshell-compatible-login.md) — Xshell-compatible external launch for jump servers, and its security model
- [`docs-en/tunnel-feature-planning.md`](docs-en/tunnel-feature-planning.md) — port-forwarding tunnel design
- [`docs-en/route-tracing-design.md`](docs-en/route-tracing-design.md) — traceroute and geographic visualisation
- [`docs-en/performance-and-memory-optimization-2026-07.md`](docs-en/performance-and-memory-optimization-2026-07.md) — performance and memory optimisation log
- [`docs-en/terminal-input-ordering-analysis.md`](docs-en/terminal-input-ordering-analysis.md) — serialising terminal input
- [`docs-en/sftp-dual-pane-winscp-gap-analysis.md`](docs-en/sftp-dual-pane-winscp-gap-analysis.md) — dual-pane SFTP vs WinSCP, item by item
- [`docs-en/ftp-client-feasibility-research.md`](docs-en/ftp-client-feasibility-research.md) — trade-offs behind FTP / FTPS support
- [`docs-en/telnet-and-serial-feasibility-research.md`](docs-en/telnet-and-serial-feasibility-research.md) — feasibility and work list for Telnet / serial sessions
- [`docs-en/keyboard-shortcuts.md`](docs-en/keyboard-shortcuts.md) — every keyboard shortcut and mouse gesture (generated from `ShortcutCatalog`, not hand-copied)
- [`plan.md`](plan.md) — progress log, known issues and the backlog (the source of truth for day-to-day work)

---

## 🛠️ Tech stack

- **.NET 11** — target runtime (`net11.0`, preview features and `runtime-async` enabled)
- **Avalonia 12.1** — cross-platform XAML UI framework
- **ReactiveUI** — reactive MVVM
- **VelaDock (in-house)** — draggable split/dock layout with zero third-party dependencies
- **Tmds.Ssh** — SSH / SFTP / port forwarding / ProxyJump (fully managed, async-first)
- **FluentFTP** — FTP / FTPS client
- **ZMODEM / XMODEM / YMODEM (in-house)** — in-terminal rz/sz, rx/sx and rb/sb; engines under `VelaShell.Core/ZModem/` and `VelaShell.Core/XYModem/`, shared contracts under `VelaShell.Core/FileTransfer/`
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

**Working today**: terminal engine, SSH/SFTP, FTP/FTPS, ZMODEM / XMODEM / YMODEM, local shells, jump hosts, session management and import, authentication, tunnels, persistence, settings centre, cloud sync, session recording, resource monitor / process manager / traceroute, plus the **plugin system framework** (dual hosting modes, the full capability surface, UI extensions, heartbeat self-healing and idle recycling, per-plugin storage with uninstall cleanup, `.vpx` install/uninstall, SDK test doubles and developer docs) and the first-party **AI assistant plugin**.

**Provided by plugins**: Telnet, serial (COM / USB-to-serial), Redis and S3 — none of them preinstalled; install on demand from the [plugin marketplace](https://market.easilynet.top) (sources in [joesdu/velashell-plugins](https://github.com/joesdu/velashell-plugins)).

**Not yet available**: certificate authentication; the container-management plugin has not been started. Some settings are persisted but not yet wired to runtime behaviour.

The full completion matrix and backlog live in [`plan.md`](plan.md) §10–§12 and [`docs-en/plugins/STATUS.md`](docs-en/plugins/STATUS.md).

---

## 🤝 Contributing

Issues and pull requests are welcome. **Read [`CONTRIBUTING.en.md`](CONTRIBUTING.en.md) before you start** — it covers the setup (the SDK is a preview build, and only Debug builds locally), branch and commit conventions, the two hard rules for the test suite, and the localization and documentation sync requirements.

For layering conventions and dependency direction see [`docs-en/architecture.md`](docs-en/architecture.md); if you are writing a plugin, start with the [plugin development guide](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md).

Found a security vulnerability? **Do not open a public issue** — follow the private process in [`SECURITY.md`](SECURITY.md).

---

## 📄 License

VelaShell is **dual-licensed**:

- **[AGPL-3.0](LICENSE) (default)**: free to use, modify and distribute, but derivative works — including anything offered as a network service — **must release their complete source under the same license**, keeping copyright and donation notices intact. Stripping the project's identity and selling it closed-source is infringement, and will be pursued (DMCA takedowns / litigation).
- **[Commercial license](LICENSE-COMMERCIAL.md) (paid, on request)**: if you need closed-source integration or distribution, or corporate policy rules out AGPL, contact the author to purchase one (📧 <dygood@outlook.com>, subject line "Commercial License").

**Authenticity notice**: VelaShell itself is **free forever** for individuals and companies alike, and the only official distribution channel is this repository's GitHub Releases; any "paid VelaShell" from any other channel is pirated. The "VelaShell" name and logo are not covered by the open-source license — derivative versions must not use them to promote or sell.

By contributing you agree that your contribution is licensed under AGPL-3.0 and that the copyright holder may sublicense it under the commercial license (see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3).

---

> VelaShell — born for the command line.
