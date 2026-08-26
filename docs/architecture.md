# VelaShell Architecture Blueprint

## Recommended Baseline

```text
VelaShell (App)          -> Desktop entry point and composition root
VelaShell.Presentation   -> ViewModels, navigation, orchestration
VelaShell.Controls       -> Custom controls, styles, tokens, behaviors
VelaShell.Terminal       -> Terminal engine, parser, renderer, interaction model
VelaShell.Core           -> Stable domain models and contracts
VelaShell.Infrastructure -> SSH, storage, platform integration, background work
```

> The desktop entry project is named `VelaShell` (assembly `VelaShell`, `OutputType=WinExe`).
> `VelaShell.App` is a legacy alias that still appears in some older docs. Each project
> also carries its own `README.md` describing its internal structure and dependencies.

## Dependency Direction

```text
App(VelaShell) -> Presentation
App(VelaShell) -> Controls
App(VelaShell) -> Infrastructure
App(VelaShell) -> Terminal
App(VelaShell) -> Core

Presentation -> Core
Presentation -> Terminal

Controls -> Core (optional, only for shared UI contracts)

Infrastructure -> Core
Infrastructure -> Terminal (only if an adapter truly belongs here)

Terminal -> Core
```

## Why Split Out `VelaShell.Controls`

- The design file contains many reusable surfaces instead of one-off screens.
- Theme tokens, shared panel shells, session tree items, terminal tab strip, transfer rows, and tunnel cards should evolve independently from the application bootstrap.
- Runtime theme changes become simpler when token dictionaries and custom control styles live in a dedicated assembly.

## Terminal Design Notes

The terminal area should be treated as a subsystem, not a single control.

Suggested internal layers:

```text
SSH Stream Adapter
  -> ANSI Parser
    -> Buffer / Scrollback Model
      -> Semantic Highlight / Link Detection
        -> Render Snapshot
          -> Avalonia Drawing Surface
```

This split makes it easier to support:

- incremental streaming (character-by-character / line-by-line)
- ANSI escape sequences
- URL / error / warning highlighting
- selection without hijacking `Ctrl+C`
- multiline paste input
- common Linux progress bar redraw behavior

The renderer also supports an optional **line-number / timestamp gutter** on the left
edge (`Terminal/Rendering/GutterLayout` + `GutterFoldModel`): two independently toggled
side columns with fold markers and blank-gap handling, wired to keyboard shortcuts.

## SSH Transport

SSH/SFTP is provided by **[Tmds.Ssh](https://github.com/tmds/Tmds.Ssh)**, a fully managed,
async-first .NET SSH library (it replaced SSH.NET). The library's types never leave
`Infrastructure/Ssh/`: `TmdsSshClientWrapper` / `TmdsSftpClientWrapper` / `ShellStreamWrapper`
adapt `SshClient` / `SftpClient` / `RemoteProcess` to the neutral `Core.Ssh` interfaces, and
`TmdsSshInterop` translates library exceptions into the `VelaSsh*Exception` family declared in
`Core`. ProxyJump uses the library's native `SshProxy` chain, assembled by
`InfrastructureServiceCollectionExtensions.BuildProxyChain` from the saved jump-host profiles
(≤5 hops, cycle-checked, host key verified per logical hop).

## In-terminal file transfer (ZMODEM / XMODEM / YMODEM)

`rz`/`sz`, `rb`/`sb` and `rx`/`sx` transfers are implemented in-house and split across three
projects, with the protocol-neutral contracts shared by all three engines:

```text
Core/FileTransfer/ -> shared contracts: IByteDuplex, IFileTransferSink / Source / Observer,
                      FileTransferSession / Item, CRC-16/XMODEM, the ZFILE / YMODEM block-0
                      file-info codec, and TransferTrace
Core/ZModem/       -> ZMODEM engine (frames, ZDLE escaping, CRC-16/32, ZModemSender /
                      ZModemReceiver) over the IByteDuplex abstraction
Core/XYModem/      -> XMODEM / XMODEM-1K / YMODEM / YMODEM-G engine (fixed-size blocks,
                      per-block ACK/NAK, XYModemSender / XYModemReceiver)
Terminal/FileTransfer/ -> ZModemDetector (spots the ZRQINIT / ZRINIT boot sequence in the
                      output stream) + TerminalTransferRouter (sits between the bridge read
                      loop and the emulator; hands bytes to the chosen engine for the
                      duration of a session, then resets) + ShellStreamByteDuplex
App/Services/FileTransfer/ -> file source (upload), folder sink (download), progress observer
```

ZMODEM starts **automatically**: its lead-in sequence is recognisable in the output stream.
XMODEM and YMODEM can only be started **manually** (command palette → "File Transfer"), because
they have no lead-in on the wire — `sb`/`sx` wait silently for the receiver's `C`, and `rb`/`rx`
emit a bare `C` that is indistinguishable from ordinary terminal output, so any auto-detection
would misfire. Run the remote command first, then invoke the palette entry.

Because the engines only need an `IByteDuplex`, the same path serves SSH, local ConPTY, and
future serial / Telnet sessions. Set `VELASHELL_TRANSFER_TRACE=1` (the historical
`VELASHELL_ZMODEM_TRACE=1` still works) to dump protocol frames when diagnosing interop problems.

## Docking (VelaDock)

Split/tabbed layout is provided by an in-house, dependency-free docking framework
(**VelaDock**, `src/VelaShell/Docking/`) that replaced `Dock.Avalonia`. See
`docs/dock-replacement-plan.md`.

```text
Docking/Model/     -> pure INPC model (unit-testable): DockWorkspace / DockGroup / DockSplit / DockDocument
Docking/Controls/  -> DockWorkspaceControl (renders the split tree), DockGroupControl (tab strip),
                      DockTabItem, DockDragController + DockDropOverlay (five-zone drag-drop split)
```

Documents are live SSH/local sessions; each `TerminalTabView` is cached per document and
reused across tab switches. Floating windows are intentionally not implemented (product decision).

## Window Shell

The main window is a **self-drawn borderless window** (`WindowDecorations="None"`), not
native chrome — Avalonia 12.x's `ExtendClientArea` / decoration-role input redirection is
broken on Win32 (title-bar buttons and drag stop working), so it was abandoned. Instead
`Views/TitleBarView` draws a 36px bar with logo+name (left), the global action icon group
(right; Broadcast opens the multi-terminal input bar, while SyncGroup remains disabled) and its own minimize/maximize/close
caption buttons. Native *feel* is restored programmatically: `BeginMoveDrag` (native move
loop, Win11 edge-snap works), double-click to maximize, and **Win11 Snap Layouts via a
`MainWindow` WndProc hook on `HTMAXBUTTON`**. All dialogs use the same borderless mode.
The text menu bar (会话/编辑/…) was removed in favor of the command palette (`Ctrl+P`/`Ctrl+K`).

## Theme Design Notes

- Map `.pen` variables to semantic Avalonia resources.
- Support three user modes: `Dark`, `Light`, `System`.
- Keep accent color as a separate override layer.
- Use `DynamicResource` so changes are reflected without restart.

## Persistence Strategy

All persistence goes through **SonnetDB** (https://github.com/IoTSharp/SonnetDB), used as an
**embedded** multi-model database (`SonnetDB.Core`, opened via `Tsdb.Open` under
`~/.velashell/sonnetdb`). Legacy JSON files (`sessions.json`, `settings.json`,
`state.json`, `known_hosts.json`, `quick-commands.json`) are imported once on first run.

- **Document collections** (JSON documents) hold business/config data:
  `session_groups`, `session_profiles` (indexed by `$.groupId`), `app_config`
  (settings/state/sync singleton docs — `sync` holds the Gist cloud-sync config with
  the PAT encrypted via `ISecretProtector`), `known_hosts`, `ui_config`,
  `quick_commands` (a schema-v2 singleton containing groups plus custom commands),
  `tunnels` (one doc per profile id holding its tunnel configs),
  `recordings` (session-recording metadata).
- **Time-series measurements** hold time-oriented data:
  `conn_history` (recent connections, powers the sidebar list),
  `audit_log` (security auditing) and `session_recording_chunks`
  (session recording output chunks: tag `recording_id`, fields `offset_ms` +
  Base64 `data`; point time = recording start + offset, replayed on a timeline
  by the recording player).
- SQL dialect note: `ORDER BY time` requires the `time` column to be present in
  the SELECT list; plain `DELETE FROM measurement` may be unsupported — the
  recording store falls back to a drop-and-rewrite compaction during retention
  cleanup to reclaim orphaned chunk bytes.
- Sensitive fields (passwords, key passphrases, sync tokens) are encrypted at
  rest with AES-256-GCM via `ISecretProtector` (local key file).
- Device-local shell layout is stored in `app_config/state`: sidebar section
  collapse state and remembered heights are restored on startup and are not
  included in Gist sync.
- Keep persistence interfaces in `Core` (`ISessionRepository`, `ISettingsService`,
  `IRecentConnectionService`, `IAuditLogService`, `IAppDataStore`,
  `ISessionRecordingStore`, `IQuickCommandRepository`, `ISecretProtector`), implementations in
  `Infrastructure/Persistence` (`SonnetDb*`), all sharing one `SonnetDbEngine`
  singleton that is disposed on app exit (WAL flush).

Quick commands are loaded through `IQuickCommandRepository`, which owns v1 SonnetDB and
legacy `quick-commands.json` migration, backup creation, schema validation, and Gist-compatible
v1/v2 snapshots. The UI and sync service never manipulate the SonnetDB document directly.

## Migration Rule

Do not move everything at once.

Preferred order:

1. Add new assemblies.
2. Wire solution references.
3. Migrate ViewModels to `Presentation`.
4. Migrate themes and reusable controls to `Controls`.
5. Migrate storage and SSH implementations to `Infrastructure`.
6. Reduce `App` to startup and shell hosting only.
