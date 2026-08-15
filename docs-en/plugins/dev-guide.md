# VelaShell Plugin Development Guide (v1)

> Status: **Implemented and available with the repository**. This guide describes the currently implemented plugin system (dual host modes + full Avalonia UI); the documents numbered 01–15 in this directory are the long-term design blueprint for the complete plugin platform
> (permission system, plugin store, and more). Unimplemented parts follow the blueprint and will be delivered in phases as needed.

## 1. Architecture in One Minute

- **Dual host modes** (selected by manifest `hostMode`):
  - `inProcess` (default): the plugin runs in its own **collectible AssemblyLoadContext** inside the host process, with zero IPC overhead; panels can be docked into the main window's tab area;
  - `isolated`: the plugin runs in a separate VelaShell.PluginHost process (named-pipe RPC), so crashes and hangs do not affect the host; panels are separate card windows (use `inProcess` for true docked panels).
  The **plugin source code is identical** in both modes.
- **Dependency isolation**: the plugin resolves its bundled dependencies (any NuGet packages) from its plugin directory according to its `deps.json`, without interfering with the host; only two categories of assemblies are forcibly shared with the loader to guarantee type identity:
  `VelaShell.PluginSdk` and `Avalonia*` (therefore the Avalonia version must match the host, see §5.9).
- **Narrow waist contract**: plugins reference `VelaShell.PluginSdk` (BCL dependencies only); UI code uses full Avalonia directly (compile-only reference). **Never** copy the SDK or Avalonia assemblies into the plugin directory.
- **Fault isolation**: in-process mode uses full-path guards (a single plugin is marked Failed, but infinite loops and runaway memory cannot be prevented); isolated mode uses the process boundary (even a hard failure only loses that plugin).
- **Zero startup overhead**: discovery only reads `plugin.json` and does not touch assemblies, while discovery and activation run on a background thread after the main window is displayed; when there are no plugins, the startup path adds only two directory-existence checks.

Related source:

| Location | Contents |
| --- | --- |
| `plugin-sdk/VelaShell.PluginSdk/` | Contract: entry interfaces, capability interfaces, DTOs, and manifest models |
| `plugin-sdk/VelaShell.PluginSdk.Testing/` | Test doubles: `TestPluginContext` and in-memory implementations of each capability |
| `src/VelaShell.Infrastructure/Plugins/` | Host runtime: discovery/loading/activation/deactivation and capability bridging |
| `src/VelaShell.Presentation/Plugins/` | Bridge from command capabilities to the command registry |
| `src/VelaShell.PluginHost/` | Isolated plugin host process: RPC proxy context, built-in Avalonia, and dock embedding |
| `plugins/` | First-party plugins in the repository, including the HelloWorld example |

## 2. Quick Start

### 2.1 In-Repository Plugins (First-Party)

```text
plugins/VelaShell.Plugin.Demo/
├── VelaShell.Plugin.Demo.csproj
├── plugin.json
└── DemoPlugin.cs
```

csproj (`plugins/Directory.Build.props` centrally enables `EnableDynamicLoading` and `plugin.json` output; `VelaPluginId` drives copying to the application output directory after the build):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <VelaPluginId>velashell.demo</VelaPluginId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\plugin-sdk\VelaShell.PluginSdk\VelaShell.PluginSdk.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

plugin.json:

```jsonc
{
  "id": "velashell.demo",              // <publisher>.<name>, lowercase [a-z0-9.-]
  "version": "0.1.0",                  // semver
  "displayName": "Demo",
  "description": "Example",
  "entry": "VelaShell.Plugin.Demo.dll", // Relative to the plugin directory; absolute paths and ".." are forbidden
  "apiLevel": 1
}
```

Entry class:

```csharp
using VelaShell.PluginSdk;

[VelaPlugin]
public sealed class DemoPlugin : IVelaPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken ct)
    {
        context.Log.Info("Demo activated.");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;
}
```

**Press F5 directly**: the main program has a build-order-only reference to `plugins/*/*.csproj` (`ReferenceOutputAssembly=false`), so repository plugins are always rebuilt before startup and their output (including `plugin.json`) is mirrored to `src/VelaShell/bin/<configuration>/net11.0/plugins/<id>/`.
You do not need to build the plugin project manually before changing its manifest or code.
Finally, add the project to the `/plugins/` folder in `VelaShell.slnx` (for IDE visibility only).

**Whether it ships in the installer** is controlled by `<VelaPluginShip>` (default `true`):

```xml
<!-- Example only, excluded from release packages: mirrored into bin during local builds, so F5 can load it; dotnet publish excludes it -->
<VelaPluginShip>false</VelaPluginShip>
```

Plugins with `true` are registered in `ResolvedFileToPublish` by `AddVelaPluginsToPublish` in `src/VelaShell/VelaShell.csproj` during `dotnet publish`, and are placed in `plugins/<directory name>/` in the installer package (and marked `ExcludeFromSingleFile` to guarantee they are real files on disk, so the ALC can load them according to `deps.json`). The official example plugin `velashell.hello-world` sets this to `false`.

> **Directory name = id with dots replaced by hyphens** (`velashell.ai` → `velashell-ai`). macOS `codesign`
> treats directories containing dots inside an `.app` as nested bundles. Using the id unchanged as the directory name causes signing to fail directly
> (`bundle format unrecognized, invalid, or unsuitable`). The directory name **does not participate in any logic**.
> The host enumerates subdirectories and reads the id from `plugin.json`, so this is only a packaging-side naming convention.

### 2.2 Plugins Outside the Repository (Third-Party)

Create a project with the same structure anywhere (referencing the SDK project or a future NuGet package). After building, there are two installation methods:

**Method 1: `.vpx` package (recommended, installable/uninstallable with one click from the plugin management page)**. Package the
**entry DLL + deps.json + bundled dependencies + plugin.json** into a zip archive and rename it to `.vpx`:

```text
my-plugin.vpx (zip)
├── plugin.json          # Root directory
├── MyPlugin.dll
├── MyPlugin.deps.json
└── <bundled dependency>.dll
```

Sidebar plugin icon → Plugin Management page → select the file with "Install .vpx…" to install it (extracts into the user directory, validates the manifest, protects against zip slip, replaces the old version for the same id, and activates according to the activation policy). Uninstallation is also a one-click operation on the management page
(deletes the directory and clears database data).

**Method 2: Place the directory directly**. Put the files above in:

```text
%LocalAppData%\VelaShell\plugins\<plugin id>\        (Windows)
~/.local/share/VelaShell/plugins/<plugin id>/        (Linux/macOS, based on the host data root)
```

Restart VelaShell to load it. Again: do not put `VelaShell.PluginSdk.dll` or `Avalonia*.dll` there (the loader forcibly shares its own copies; adding them only increases the package size).

> Built-in application plugins (`<application directory>/plugins`) are read-only, so the management page does not offer uninstallation; user-installed plugins
> (`.vpx` files or directories placed in the user directory) can be uninstalled.

## 3. Manifest (`plugin.json`) Reference

| Field | Required | Description |
| --- | --- | --- |
| `id` | ✓ | Globally unique, `[a-z0-9.-]`, must start and end with a letter or digit, ≤64 characters. All command ids use it as a prefix |
| `version` | ✓ | semver (`1.2.0` / `1.2.0-beta.1`) |
| `displayName` | ✓ | Display name |
| `entry` | ✓ | Relative path to the entry assembly, must end in `.dll`; absolute paths and `..` segments are rejected |
| `description` | | One-sentence description |
| `publisher` | | Publisher |
| `apiLevel` | | Defaults to 1; a generation higher than the host supports is marked Incompatible and not loaded |
| `minHostVersion` | | Minimum required host version; if unmet, the plugin is Incompatible |
| `activationEvents` | | Omitted or containing `"onStartup"` = activate at startup; containing only `"onCommand:<command id>"` = **lazy activation** (load/start the process only when the placeholder command is invoked; the corresponding placeholder must be declared in `contributes.commands`) |
| `contributes.commands` | | Declarative command placeholders `[{id,title,category}]`: available in the command palette during discovery; ids must be prefixed by the plugin id; on activation, the plugin should `Register` a real handler with the same id to replace the placeholder |
| `idlePolicy` | | `"keepAlive"` (default) / `"recyclable"`: in isolated mode, a process is reclaimed after continuous idleness (no RPC and no open panels, 15 minutes by default), while placeholder commands remain available for a later trigger |
| `homepage` / `license` | | Metadata |

JSON comments and trailing commas are allowed. Plugins that fail validation produce a readable reason in the log
(`[PluginManager] Rejected plugin at ...`).

## 4. Lifecycle

```text
Discovered ──activate (background batch after startup)──▶ Active ──host exits──▶ Deactivated
    │                                   │
    ├─ .disabled marker → Disabled      └─ loading/activation error, activation timeout (10s) → Failed (unload ALC)
    ├─ invalid manifest / id conflict → Invalid
    └─ apiLevel / minHostVersion mismatch → Incompatible
```

Contract essentials:

- `ActivateAsync` **must return quickly** (10-second limit): start long-running work in your own background task and use the `context.Shutdown` token to respond to shutdown.
- `DeactivateAsync` has an approximately 2-second limit (application exit path) and is abandoned on timeout. Commands and event subscriptions registered through the SDK are cleaned up automatically by the host; only your own resources need to be finalized.
- Entry type: exactly one public, non-abstract class with `[VelaPlugin]` that implements `IVelaPlugin`, with a public parameterless constructor.
- After deactivation or failure, the ALC is unloaded with `Unload()`: do not put your own types into host static fields or long-lived events, or the assembly cannot be reclaimed.

## 5. Capability API Reference (`IPluginContext`)

### 5.1 Log: Logging

```csharp
context.Log.Info("hello");
context.Log.Error("failed", ex);
```

Writes to the host Trace pipeline, automatically adds the `[Plugin:<id>]` prefix, and is thread-safe.

### 5.2 Storage: Private Key-Value Storage

```csharp
int n = await context.Storage.GetAsync<int>("count", ct);
await context.Storage.SetAsync("count", n + 1, ct);
```

Data is stored in the host's **SonnetDB** (`plugin_data` collection, primary key `<plugin id>|kv|<key>`):

- **Strong isolation by plugin**: the capability instance carries only its own id prefix, so a plugin cannot read another plugin's data (the plugin id character set excludes the separator `|`, so the namespace cannot escape); in isolated processes, RPC routes to the same implementation and the plugin process never connects directly to the database;
- **Automatic cleanup on uninstall**: after the plugin directory is removed from `plugins/`, the host clears its database namespace and data directory as a whole on the next startup (`.disabled` means disabled, not uninstalled, so data is retained);
- A single value should generally be ≤256KB; write large data directly to files under `context.DataDirectory` (also swept on uninstall). A headless host without a database automatically falls back to JSON files in the data directory.

### 5.3 Sessions: Session Enumeration (Redacted)

```csharp
IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(ct);
SessionInfo? one = await context.Sessions.GetAsync(sessionId, ct);
```

`SessionInfo` contains only connection metadata (host/port/username/status/time), **never credentials**.
`SessionId` is the first parameter of other remote capabilities. v1 plugins cannot initiate connections.

### 5.4 RemoteFs: Remote Files (SFTP)

```csharp
var entries = await context.RemoteFs.ListDirectoryAsync(sid, "/var/www", ct);
RemoteFileEntry? stat = await context.RemoteFs.StatAsync(sid, "/etc/nginx/nginx.conf", ct); // missing → null
byte[] conf = await context.RemoteFs.ReadAllBytesAsync(sid, "/etc/nginx/nginx.conf", ct: ct);
await context.RemoteFs.DownloadFileAsync(sid, "/var/log/app.log", localPath, progress, ct);
```

- Reuses the user's established session channel and does not authenticate again; an invalid session throws `PluginSessionNotFoundException`.
- `StatAsync` returns `null` for a missing path (consistent with host semantics; do not use exceptions to test for existence).
- `ReadAllBytesAsync` has a 16MB limit by default; use `OpenReadAsync` (**read-only sequential stream**, process while reading without a temporary file) or `DownloadFileAsync` for large files. In isolated mode, `OpenReadAsync` fetches chunks over RPC and does not support Seek.
- The host throttles progress callbacks (≥100ms), so you can update state directly.

### 5.5 RemoteExec: One-Off Remote Commands

```csharp
ExecResult r = await context.RemoteExec.RunAsync(sid, "docker ps --format json",
    new ExecOptions { Timeout = TimeSpan.FromSeconds(10) }, ct);
```

Uses a separate exec channel: it does not enter the user's terminal or pollute shell history and environment. The default timeout is 30s, with a maximum of 10min. It is suitable for probe commands, not interactive or long-running processes.

### 5.6 Commands: Command Palette

```csharp
context.Commands.Register(new(
    $"{context.PluginId}.refresh", "Demo: Refresh", "Demo",
    async ct => { /* Runs on a background thread; exceptions are logged automatically */ }));
```

- The id must be prefixed with `<pluginId>.` (enforced by the host to prevent plugins from impersonating one another).
- After registration, the command appears in the command palette (Ctrl+P / Ctrl+K); the plugin is responsible for title localization (it can retrieve translations through `context.Host.Locale` and re-register on `LocaleChanged`).
- The command body runs on a **background thread**. Do not touch the UI; slow operations will not freeze the interface.
- All commands are automatically unregistered when the plugin is deactivated; the handle returned by `Register` can be used for early unregistration.

### 5.7 Events: Host Events

```csharp
context.Events.SessionConnected += s => context.Log.Info($"{s.Host} connected");
context.Events.ThemeChanged     += theme => ...;
context.Events.LocaleChanged    += locale => ...;
```

Handlers are invoked off the UI thread and **must return quickly without throwing** (exceptions are caught and logged); hand off expensive work to your own background task. Subscriptions are removed automatically on deactivation.

### 5.8 Host: Host Information

`context.Host.AppVersion / ApiLevel / Locale / Theme` (the latter two are live).

### 5.9 Ui: Panels (Full Avalonia)

Plugins use **full Avalonia** to design their own UI: choose compile-time AXAML or pure code, bring your own styles, resources, and localization, and add any third-party component packages (distributed in the plugin directory and isolated from other plugins by the ALC).

**The only hard constraint: the Avalonia version must match the host.** Avalonia-related packages must be compile-only:

```xml
<!-- csproj: version = host version (currently 12.1.1); Avalonia DLLs are never copied into the plugin directory,
     and the loader shares the same set at runtime (in-process = the host's; isolated process = bundled with PluginHost). -->
<PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
```

Third-party packages based on Avalonia, such as control libraries, should be referenced normally (their DLLs must be distributed with the plugin; only the `Avalonia*` assemblies themselves are forcibly shared by the loader).

Opening a panel: the host invokes the content factory on the **UI thread**, and you only need to return your control:

```csharp
IPluginPanel panel = await context.Ui.ShowPanelAsync(
    new() { Title = "My Panel", DisplayMode = PanelDisplayMode.Document },
    () => new MyPanelView(context));   // A compile-time AXAML UserControl, or any Control
panel.Closed += () => ...;             // Triggered when the user closes it or the plugin is deactivated
await panel.CloseAsync();              // Programmatic close
```

The plugin chooses the display mode:

- `PanelDisplayMode.Document` — docked tab: enters the main window's tab area, and users can **drag it to any split position** or split it from the context menu, making it equal to terminal/SFTP tabs. **Only inProcess mode** provides true docking; isolated mode always uses a separate card window (cross-process dock embedding conflicts with tab switching and has been deprecated);
- `PanelDisplayMode.Window` — separate window: in-process uses a host-style custom-drawn card window; an isolated process uses the plugin process's own window (automatically follows the host's light or dark theme).

A docked tab can also pick its **placement** (`PanelOptions.Placement`; ignored in window mode):

```csharp
new() { Title = "AI Assistant", Placement = PanelPlacement.Right, PlacementRatio = 0.3 }
```

`Tabs` (the default) joins the current tab group; `Right`/`Left`/`Bottom`/`Top` split off a column at
the matching outer edge of the tab area, sized by `PlacementRatio` (its share of the tab area,
0.15–0.85, default 0.3). Placement goes through the same drag-and-drop docking path, so the result is
identical to the user dragging it there — dragging it back, splitting again, and closing all take the
ordinary route. `PlacementRatio` only sets the width *on open*: a width the user drags is not written
back, so a plugin that wants to remember the preference stores it itself and passes it in next time
(the AI plugin does exactly that — see "Side pane width (%)" on its settings page).

**Theme tokens: write `{DynamicResource VelaXxx}` to follow the host theme.** All of the host's `Vela*` design tokens (semantic brushes, font-size scale, and font families) are available to plugins and follow light/dark switching immediately:

```xml
<Style Selector="TextBlock.title">
  <Setter Property="Foreground" Value="{DynamicResource VelaTextPrimary}" />
</Style>
<Border Background="{DynamicResource VelaBgInput}"
        BorderBrush="{DynamicResource VelaBorderPrimary}" />
<TextBlock Foreground="{DynamicResource VelaAccent}" />
```

- In-process: controls are in the host visual tree, so tokens are naturally available;
- Isolated process: after the handshake, the host sends a token snapshot (resolved for the current light/dark variant) over RPC, and PluginHost injects it into its application resources; it sends the snapshot again when the theme changes, so DynamicResource refreshes automatically. Embedded font sections (`fonts:...#`) do not cross processes; font tokens carry only the system fallback chain.
- Common tokens: `VelaTextPrimary/Secondary/Muted/Tertiary`, `VelaBgSurface/Page/Input/Hover`, `VelaBorderPrimary/Secondary`, `VelaAccent`, `VelaWarning/Error/Info`, `VelaFontSize9..16`, `VelaUiFont/VelaUiMonoFont`. See `src/VelaShell.Controls/Themes/VelaTokens.axaml` and `src/VelaShell/Themes/{Dark,Light}Theme.axaml` for the complete list.
- An unresolved token (due to a spelling error or an older host version) leaves the property at its default value and does not report an error. Provide semantic fallbacks for critical colors.

Discipline:

- The plugin directly handles control events and updates (standard Avalonia style; after `await`, execution returns to the UI thread automatically); update controls from background threads through `Dispatcher.UIThread`.
- Attach each control instance to only one panel; a panel is a live control and has no concept of refreshing the entire tree.
- Localization is the plugin's responsibility: retrieve translations through `context.Host.Locale` and hot-update on `LocaleChanged`.
- The host automatically closes all of a plugin's panels when the plugin is deactivated.
- Complete example (compile-time AXAML + bilingual copy + session/remote execution integration): `plugins/VelaShell.Plugin.HelloWorld` (DemoPanelView.axaml).

### 5.10 Secrets: Encrypted Secret Storage

```csharp
await context.Secrets.SetAsync("api-token", token);
string? saved = await context.Secrets.GetAsync("api-token");
await context.Secrets.DeleteAsync("api-token");
```

Difference from Storage: values are **encrypted by the host's secret protector before entering SonnetDB** (on Windows, a local key wrapped with DPAPI; primary key `<plugin id>|secret|<name>`, isolated by plugin and cleared on uninstall just like KV), and in isolated mode secrets are stored only on the host side. Always put API tokens and passwords here, never in Storage.
Suitable for small, short strings; if the protector is unavailable, the capability reports an error directly and never falls back to plaintext.

### 5.11 Terminal: Read/Search/Authorized Writes

```csharp
string tail = await context.Terminal.GetOutputAsync(sid, maxLines: 500);
var hits = await context.Terminal.SearchOutputAsync(sid, "error");         // Substring, case-insensitive
var rx   = await context.Terminal.SearchOutputAsync(sid, @"\d{3} error", isRegex: true);
await context.Terminal.WriteAsync(sid, "docker ps\n");                     // Triggers an authorization dialog
```

- Reading and searching use a **buffer snapshot** (scrollback plus the current screen, plain text without colors); regular expressions have a 1s timeout.
- **Writes require user authorization**: the host dialog offers four choices: Only this time / This run / Always / Deny; "Always allow" is persisted for the plugin in SonnetDB and can be revoked on the plugin management page. A denial throws `PluginPermissionDeniedException` (the plugin should degrade gracefully and not repeatedly bother the user).
- Writes go through the host's existing input serialization queue ("as if typed by the user"), with a limit of ≤4KB per call; a command executes only when a newline is sent.

### 5.12 Clipboard: System Clipboard

```csharp
await context.Clipboard.SetTextAsync(text);
string? current = await context.Clipboard.GetTextAsync();
```

Runs through the host main window (routed over RPC in isolated mode, with identical semantics). The clipboard often contains user passwords:
never log or transmit content that you read.

## 6. Isolated Process Mode (`isolated`)

Declare `"hostMode": "isolated"` in the manifest and the plugin runs in a separate
**VelaShell.PluginHost** process (one process per plugin, implementing the process design in 02/04/05):

```jsonc
{ "id": "acme.my-plugin", ..., "hostMode": "isolated" }
```

- **Zero source changes for the plugin**: the capability interfaces of `IPluginContext` are replaced with RPC proxies on the PluginHost side. This fulfills the SDK's transport-independent design. Change `hostMode` to switch modes.
- **Transport**: named pipe (random name + one-time token + current-user-only access; on macOS/Linux, .NET named pipes use Unix Domain Sockets underneath and are naturally cross-platform). The protocol is a lightweight bidirectional RPC using length-prefixed JSON (see the implementation notes in 05; the dependency tree deliberately does not introduce StreamJsonRpc or MessagePack).
- **Isolation benefits**: a plugin crash or hang affects only itself. Unexpected exits trigger **automatic restart** with backoff (1s → 5s → 30s by default; more than 3 failures within a 5-minute window is marked Failed and automatic recovery is abandoned); two consecutive missed heartbeat responses (30s by default) mark the process hung and force-kill/restart it; when the host exits, the plugin process exits automatically under parent-process supervision and never remains as an orphan.
- **Credentials do not leave the main process**: the plugin process receives only the pipe name and token; SSH keys and passwords never cross the process boundary.
- **Capability differences** (compared with in-process mode):

| Capability | Isolated mode |
| --- | --- |
| Sessions / RemoteExec / RemoteFs | ✅ RPC proxies, identical semantics (transport uses same-machine file paths, and progress returns through notifications) |
| Ui (full Avalonia) | ✅ Avalonia is built into the plugin process and windows are fully functional (software rendering saves memory by default; set `VELA_PLUGIN_GPU=1` to enable it); `Vela*` theme tokens are sent over RPC and are equally available |
| Commands / Events | ✅ Registry remains in the host; triggers and events return through notifications |
| Storage / Log | ✅ KV is routed over RPC into the host's SonnetDB (the plugin process does not write local files); logs are forwarded to the host with local Trace as a fallback |
| Secrets / Clipboard | ✅ Routed over RPC to execute on the host (secrets are encrypted and persisted only on the host side) |
| Dock into the main window's tab area | ❌ **Always a separate card window** (stable and consistent with the main program). Cross-process dock embedding (HWND adoption) has fundamental tension with dock tab reparenting (stuttering and windows floating out), and is **deprecated**; use inProcess for true docked tabs. The stable cross-platform solution is the shared-memory surface in blueprint 08 (future) |
| `CancellationToken` propagation across processes | ⚠️ Not propagated; both sides use timeouts as a fallback (`exec` follows `ExecOptions.Timeout`) |

- **Selection guidance**: use the default `inProcess` for first-party or trusted plugins (zero IPC overhead and draggable docked panels); use `isolated` for third-party or experimental plugins (one extra process provides crash isolation, with panels displayed in separate card windows).

## 7. Testing Plugins

Reference `VelaShell.PluginSdk.Testing` to write pure in-memory unit tests without the host:

```csharp
using VelaShell.PluginSdk.Testing;

[TestMethod]
public async Task Refresh_ListsContainers()
{
    using var ctx = new TestPluginContext();
    SessionInfo session = ctx.FakeSessions.AddConnected(host: "prod-1");
    ctx.FakeRemoteExec.Handler = (_, cmd) => cmd.StartsWith("docker ps") ? "abc123 nginx" : "";

    var plugin = new DemoPlugin();
    await plugin.ActivateAsync(ctx, CancellationToken.None);
    await ctx.RecordingCommands.RunAsync("velashell.demo.refresh");

    Assert.IsTrue(ctx.CollectingLog.Entries.Any(e => e.Message.Contains("nginx")));
}
```

Test doubles: `CollectingLogger`, `InMemoryStorage`, `FakeSessions`, `FakeRemoteFs`
(an in-memory path tree with semantics aligned to the real implementation), `FakeRemoteExec` (scripted responses),
`RecordingCommands` (use `RunAsync` to drive command bodies), `TestHostEvents` (the `Raise` method),
`TestHostInfo`, `FakeUi` (records panels and lazy content factories), `FakeSecrets`, and `FakeClipboard`.

## 8. Deployment, Disabling, and Troubleshooting

| Item | Location/Method |
| --- | --- |
| Built-in application plugins | `<application directory>/plugins/<id with dots replaced by hyphens>/` (for example, `plugins/velashell-ai/`; see §2.1) |
| User-installed plugins | `<data root>/plugins/<id>/` (on Windows, `%LocalAppData%\VelaShell\plugins\`; this is outside the `.app` and does not participate in signing, so the directory is still named by id) |
| Plugin data | KV/secrets in the host's SonnetDB (`plugin_data` collection); files in `<data root>/plugin-data/<id>/` |
| Uninstallation cleanup | Remove the plugin directory from `plugins/` → the next startup automatically clears its database data and data directory as a whole (`.disabled` only disables it and retains data) |
| Disable one plugin | Place an empty `.disabled` file in the plugin directory |
| Disable all plugins (troubleshooting) | Environment variable `VELASHELL_DISABLE_PLUGINS=1` |
| Id conflict | First match wins according to root-directory order (application directory first); the later one is marked Invalid |
| Logs | Debug output/Trace, with `[PluginManager]` and `[Plugin:<id>]` prefixes |

## 9. Performance and Behavior Discipline

The host is a terminal application that is extremely sensitive to memory and latency. Plugins must follow these rules:

1. **Do not block**: `ActivateAsync` must return in seconds; event handlers and command bodies must not wait on synchronous I/O.
2. **Do not poll**: use events instead of timers; when a timer is truly necessary, use an interval of ≥5s and stop it immediately when `Shutdown` is triggered.
3. **Use only the `Ui` capability for UI**: the host invokes panel content factories on the UI thread; command bodies and event handlers run on background threads, and controls must be updated through `Dispatcher.UIThread`. Reflection into the host's internal UI is not covered by compatibility guarantees.
4. **Manage memory**: download large files to disk instead of reading them into memory; cap caches; release everything on deactivation (otherwise the ALC cannot be reclaimed and memory grows steadily).
5. **Be friendly to remote systems**: combine probe commands into one execution (one exec with multiple output sections) instead of sending a command to the remote host every second.

## 10. Versioning and Compatibility Guarantees

- **apiLevel (currently = 1)**: within a generation, the SDK only adds and never changes or removes items (interface methods, DTO fields, and manifest schema). Breaking changes raise apiLevel; the host rejects plugins from a higher generation and gives a clear message.
- **minHostVersion**: declare this when the plugin depends on newer host capabilities. An older host marks the plugin Incompatible instead of allowing a runtime failure.
- **Host-mode independence**: capability interfaces are transport-independent. Switching `hostMode` between inProcess and isolated requires no plugin source changes (this is implemented); the only exception is behavior that differs in isolated mode, as shown in the capability differences table in §6.

## 11. Deliberate Gaps from the Long-Term Blueprint

| Blueprint capability | v1 status |
| --- | --- |
| One process + IPC per plugin (02/04/05) | **Implemented** (`hostMode: "isolated"`, see §6): named pipes + lightweight RPC + heartbeat + automatic crash-restart backoff |
| Permission system + Broker (06) | Not implemented: v1 targets first-party and self-installed plugins; installation implies trust |
| UI contribution points / VelaUI (08) | Available: command-palette commands + full Avalonia panels (dockable tabs in inProcess; always separate card windows in isolated processes) + plugin management page. The VelaUI declarative tree is **not being pursued** by user decision; cross-process dock embedding is deprecated (see the notes in 08); sidebar/status-bar mounting points are deferred |
| `.vpx` packaging / signing / store (03/10) | Not implemented: a directory is the plugin; the distribution system is explicitly deferred |
| Activation events / lazy activation (03) | **Implemented**: `onStartup` / `onCommand:<id>` + `contributes.commands` placeholders; other event types (onSessionConnect/onFileOpen, etc.) are deferred |
| Idle reclamation (04) | **Implemented** (isolated mode + `idlePolicy: "recyclable"`) |
| secrets / clipboard capability domains (07) | **Implemented** (§5.10/§5.11; without a permission system, installation implies trust) |
| terminal / localFs / audio / ai and other capability domains (07) | Not opened; any new capability domain must be added back to the blueprint and only add to the API without changing it |

Discipline for adding capabilities: first record them in this file and the corresponding blueprint document, then add the interface to `VelaShell.PluginSdk`; within the same apiLevel, only add and never change.
