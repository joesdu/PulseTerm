# 03 · Plugin Model: Package Format, Manifest, Lifecycle, and Contribution Points

> **Implementation note (2026-08)**: The manifest fields currently implemented are documented in dev-guide §3 (`id/version/entry/apiLevel/hostMode/minHostVersion/activationEvents/contributes.commands/idlePolicy`). The `onStartup` and `onCommand:<id>` activation events are implemented (lazy activation of placeholder commands). Other event types (`onSessionConnect/onFileOpen/onSchedule`, etc.) and the .vpx package format/NLS/signatures remain targets described in this document. Idle reclamation is implemented (`idlePolicy: "recyclable"`, isolated mode only).

## 1. .vpx Package Format

`.vpx` (VelaShell Plugin Package) is a zip container:

```text
my-plugin-1.2.0.vpx
├── plugin.json                  # manifest (required, UTF-8)
├── plugin.nls.zh-Hans.json      # text localization (optional, one for each of five languages)
├── plugin.nls.ja.json / ...
├── bin/
│   ├── MyPlugin.dll             # entry assembly (pointed to by manifest.entry)
│   ├── MyPlugin.deps.json       # dependency list (basis for ALC resolution)
│   └── <third-party dependency>.dll # all dependencies bundled with the plugin (self-contained, excluding shared contracts referenced by the SDK)
├── assets/                      # icons and static assets
│   └── icon.png                 # 128×128, shown in the plugin management page and marketplace
├── README.md                    # displayed on the details page
├── CHANGELOG.md                 # optional
└── SIGNATURE                    # signature file (see 10-packaging)
```

Constraints:

- Absolute paths and `..` path segments are forbidden inside the zip (the unpacker performs mandatory validation to prevent zip-slip).
- `VelaShell.PluginProtocol.dll` / `VelaShell.PluginSdk.dll` are **not included in the package**. They are provided uniformly by PluginHost (shared assemblies), ensuring that contract types remain identical. The packaging CLI removes and validates them.
- After unpacking, the directory is used read-only. The only directory writable during plugin execution is its `data/<pluginId>/` directory.

## 2. Manifest Specification (plugin.json)

```jsonc
{
  "$schema": "https://velashell.dev/schemas/plugin-v1.json",
  "id": "acme.image-viewer",          // Required. <publisher>.<name>, lowercase, [a-z0-9.-]
  "version": "1.2.0",                 // Required. semver
  "displayName": "%displayName%",     // %key% uses indirect lookup through NLS
  "description": "%description%",
  "publisher": "acme",
  "icon": "assets/icon.png",
  "license": "MIT",
  "homepage": "https://github.com/acme/image-viewer",

  "engines": {
    "velaShell": ">=0.2.0",           // Host version range (semver range)
    "apiLevel": 1                     // See §5, negotiated during the handshake
  },
  "platforms": ["win-x64", "win-arm64", "osx-arm64", "linux-x64"],  // Omitted = all platforms

  "entry": "bin/MyPlugin.dll",        // Entry assembly; entry type discovered through the [VelaPlugin] attribute
  "hostMode": "isolated",             // isolated (default, dedicated process) | shared (reserved for future use, ignored in v1)

  "activationEvents": [               // See §4
    "onCommand:acme.image-viewer.open",
    "onFileOpen:remote:**/*.{png,jpg,jpeg,gif,webp}"
  ],

  "permissions": [                    // See 06-permission-system
    "remote.files.read",
    { "id": "fs.local.read", "reason": "%perm.localRead.reason%" }  // Optional request reason, shown in the authorization dialog
  ],

  "contributes": { ... }              // See §6
}
```

Validation rules (enforced during installation; `vela-plugin validate` performs the same checks):

- Validate against the JSON Schema (the schema is published and hosted with the SDK). Warn on unknown top-level fields, and reject unknown `contributes` child keys (to prevent typos from failing silently).
- `id` must be globally unique (checked against the installed set), and `entry` must exist in the package.
- An unknown permission ID in `permissions` causes installation to be rejected. The case of an old host and a new plugin is handled by the `engines` range.
- For plugins declaring dangerous permissions such as `remote.*`, `fs.*`, or `terminal.*`, the details page and installation confirmation page must display each permission individually (with a prominent Chrome Store-style warning).

## 3. Entry-Point Convention (SDK Side)

```csharp
[VelaPlugin]                                  // Discovered by PluginHost through reflection; exactly one per package
public sealed class ImageViewerPlugin : IVelaPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken ct);
    public Task DeactivateAsync(CancellationToken ct);   // Time-limited (5s by default); process is terminated on timeout
}
```

`IPluginContext` is the sole entry point through which a plugin obtains all capabilities:

```csharp
public interface IPluginContext
{
    string PluginId { get; }
    string DataDirectory { get; }            // Absolute path to data/<pluginId>/
    ActivationReason Activation { get; }     // The activation event that started the plugin (including parameters)
    IRemoteFs RemoteFs { get; }              // Capability proxies, see 07
    ITerminal Terminal { get; }
    ISessions Sessions { get; }
    ILocalFs LocalFs { get; }
    IUi Ui { get; }
    IStorage Storage { get; }
    ISecrets Secrets { get; }
    IPluginSettings Settings { get; }
    IEvents Events { get; }
    IAudio Audio { get; }
    IAi Ai { get; }
    ILogger Log { get; }
    CancellationToken Shutdown { get; }      // Triggered when the host requests shutdown
}
```

## 4. Activation Events

| Event | Trigger | Parameters |
| --- | --- | --- |
| `onStartup` | After the main application finishes starting (deferred batch, does not block startup) | — |
| `onCommand:<commandId>` | The user executes the command (command palette/menu/shortcut) | commandId, command arguments |
| `onView:<viewId>` | The user first expands a sidebar view contributed by the plugin | viewId |
| `onSessionConnect` | Any session connects successfully | sessionId, redacted host fingerprint information |
| `onSessionConnect:<hostPattern>` | A session matching the hostname/tag connects | Same as above |
| `onFileOpen:<selector>` | The user's "Open With" action in the SFTP/local panel matches a glob; the `remote:`/`local:` prefixes distinguish the source | File path, source, sessionId |
| `onTransferComplete` | An SFTP/ZMODEM transfer completes | Transfer summary |
| `onSchedule:<cron>` | A cron expression matches (automation, see 11) | Scheduled time |
| `onUri:<scheme>` | A `velashell://<pluginId>/...` deep link is opened | uri |

Rule: activation events determine only **when to start the process**. Permissions after startup are still controlled independently by the permission system (being woken by `onSessionConnect` does not mean the plugin can read session data). `onStartup` is prominently marked on the plugin details page (resident plugin; users should be informed).

## 5. Versioning and Compatibility Promise

- **apiLevel (integer)**: The compatibility generation of `PluginProtocol`. For the same apiLevel, the host promises to only add, never modify or remove, methods, DTO fields, permission IDs, and contribution-point schemas. Breaking changes increment apiLevel, and the host supports two adjacent levels (N and N-1) for at least six months.
- **SDK semver**: Functional evolution within an apiLevel (new capabilities and contribution points) is released as minor versions, which plugins can adopt as needed.
- During handshake negotiation (see 05), the host and plugin each report an apiLevel. If their intersection is empty, activation is rejected and the plugin management page clearly indicates that the host or plugin must be upgraded.

## 6. Contributions (contributes)

Contributions are **purely declarative**. After installation, placeholders (menu items, commands, and view containers) can be registered and displayed in the UI without starting the plugin process. User interaction then triggers activation. All schemas are defined in `PluginProtocol`; rendering details are described in [08-ui-extensions.md](08-ui-extensions.md).

```jsonc
"contributes": {
  "commands": [
    { "id": "acme.image-viewer.open", "title": "%cmd.open%", "icon": "assets/open.svg",
      "category": "Image Viewer" }
  ],
  "menus": {
    "sftp/item/context": [                  // Mount point: SFTP panel file context menu
      { "command": "acme.image-viewer.open", "when": "isFile && ext =~ png|jpg" }
    ],
    "commandPalette": [ ... ],
    "terminal/context": [ ... ],
    "session/context": [ ... ]
  },
  "views": [
    { "id": "acme.player.panel", "name": "%view.player%", "location": "sidebar",
      "icon": "assets/note.svg" }
  ],
  "documents": [                            // Document page types that can host VelaUI/image surfaces (mounted in VelaDock)
    { "type": "acme.image-viewer.preview", "title": "%doc.preview%" }
  ],
  "statusBar": [
    { "id": "acme.player.status", "alignment": "right", "priority": 90 }
  ],
  "settings": [                             // Generates a settings page (rendered by the host); values read/written through the Settings capability
    { "key": "acme.player.volume", "type": "number", "default": 80,
      "minimum": 0, "maximum": 100, "title": "%setting.volume%" }
  ],
  "keybindings": [
    { "command": "acme.player.playPause", "key": "ctrl+alt+p", "when": "..." }
  ],
  "automation": {                           // See 11
    "triggers": [ { "id": "acme.watchdog.onHighLoad", "title": "%trig.highLoad%" } ],
    "actions":  [ { "id": "acme.watchdog.restartService", "title": "%act.restart%" } ]
  }
}
```

`when` clauses use context expressions similar to VSCode (restricted grammar: identifiers, comparisons, `&& || !`, and regular-expression matching with `=~`), evaluated by the host. Context keys such as `isFile`, `ext`, and `sessionConnected` are frozen in `PluginProtocol` with each apiLevel.

## 7. Plugin Lifecycle (Complete State Machine)

```text
                    ┌────────────────────────────────────────────────┐
                    ▼                                                │
 [Discovered] → validate (manifest/signature/engines/platform) ─fail→ [Incompatible/Invalid](#page-note-principle)
      │pass
      ▼
 [Installed] ←──────────────(Deactivate complete, process exits)──────────── [Deactivating]
      │ activation event matches (and not disabled)                           ▲
      ▼                                                                      │ idle reclamation/disable/shutdown/uninstall/upgrade
 [Starting]: start PluginHost → handshake → Activate(ct)                     │
      │success                    │failure/timeout (10s by default)          │
      ▼                           ▼                                          │
 [Active] ────────────────► [Crashed]: process exits/heartbeat lost/protocol error │
      │                        │ backoff restart: 1s→5s→30s, ≤3 times in window │
      └────────────────────────┤ threshold exceeded                           │
                               ▼                                             │
                           [Faulted]: no more automatic restarts; red marker + log entry in management page; user can restart manually
 [Disabled]: disabled by user; contribution-point placeholders removed; activation events ignored
 [Removed]: uninstalled; stop process → remove contribution points → delete authorization records → ask whether to retain the data/ directory
```

Additional semantics:

- **Disabling/uninstalling takes effect immediately** (G4): first withdraw contribution points (the UI disappears immediately), then enter Deactivating. Force-kill the process if it has not exited after 5 seconds.
- **Upgrade** = install the new version into `installed/<id>/<newVer>/` → Deactivate the old process → atomically switch the current-version pointer → reactivate as needed. Automatically roll back the pointer on failure.
- **Idle reclamation**: A plugin can declare `"idlePolicy": "recyclable"` in its manifest. When there has been no RPC traffic and no active UI surface for N consecutive minutes (15 by default), the host calls Deactivate to reclaim the process. The next activation event starts it again. Resident plugins, such as automation daemons, remain resident when they do not declare this policy.

## 8. Development Plan (This Workstream)

| Task | Description | Dependency | Estimate |
| --- | --- | --- | --- |
| M-1 | Finalize the `plugin.json` JSON Schema and implement the validator (including zip-slip and NLS parsing) | A-4 | 3d |
| M-2 | Activation event router: connect event sources (commands/views/sessions/file open) + matching engine (glob, host pattern) | M-1 | 3d |
| M-3 | Implement the lifecycle state machine (pure logic layer, first with fake process handles and unit-test coverage for every transition) | M-1 | 3d |
| M-4 | Contribution registry + `when` expression evaluator (freeze grammar + unit tests) | M-1 | 4d |
| M-5 | Install/uninstall/upgrade transactions (directory layout, version pointer, rollback) | M-3 | 3d |
| M-6 | Five-language NLS pipeline (`%key%` parsing, hot updates on language switch) | M-4 | 2d |

Acceptance: use a hand-written minimal manifest (with no real process) to drive the state machine and contribution registry through every transition path. All 20+ invalid examples for malformed manifests must produce readable rejection reasons.
