# Plugin SDK Reference

> Applies to **SDK 1.4.0 / apiLevel 1**
> See also: [Development Guide](dev-guide.md) (tutorial) · [CLI Manual](cli.md) · [Packaging and Publishing](publishing.md)

This page is the **lookup-oriented** view of the SDK: package layout, contract surface, what each
capability can do and what constrains it, version history, and the test doubles. For a
step-by-step walkthrough, read the [Development Guide](dev-guide.md) first.

---

## 1. Packages

| Package | Referenced by | Contents |
| --- | --- | --- |
| **`VelaShell.PluginSdk.Build`** | **the plugin project (this one only)** | MSBuild props/targets, a version-pinned Avalonia, the bundled packer, manifest validation, the `PackVpx` target |
| `VelaShell.PluginSdk` | pulled in transitively | The contract assembly: entry interface, `IPluginContext` and every capability interface, DTOs, manifest model, `.vpx` container, host registry |
| `VelaShell.PluginSdk.Testing` | the plugin's **test** project | `TestPluginContext` and in-memory doubles for every capability |
| `VelaShell.Plugin.Cli` | developer machine (dotnet tool) | `vela-plugin`: inner loop, validation, packing, signing, health check |
| `VelaShell.Plugin.Templates` | developer machine | `dotnet new velaplugin` / `velaplugin-ui` |

```xml
<PackageReference Include="VelaShell.PluginSdk.Build" Version="1.4.0" />
```

Do **not** reference `VelaShell.PluginSdk` or `Avalonia` separately — a version mismatch is a
build error (`VELA1001`) instead of a runtime control-cast failure on a user's machine.

The contract assembly depends only on the BCL. That is a discipline: it is the **only type
source shared between host and plugin**, so a third-party dependency inside it would be imposed
on every plugin.

---

## 2. Entry contract

```csharp
using VelaShell.PluginSdk;

[VelaPlugin]                                  // exactly one public, non-abstract, parameterless class
public sealed class DemoPlugin : IVelaPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken ct)
    {
        context.Log.Info("activated");
        return Task.CompletedTask;            // must return quickly (10-second limit)
    }

    public Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;  // ~2-second limit
}
```

| Constraint | Detail |
| --- | --- |
| Activation limit | 10 seconds; **stretched to 10 minutes while a debugger is attached** |
| Deactivation limit | About 2 seconds (application exit path), abandoned on timeout |
| Long-running work | Start your own background task and observe `context.Shutdown` |
| Resource cleanup | Commands and event subscriptions registered through the SDK are cleaned up by the host; never put your own types into host static fields or long-lived events, or the ALC cannot be reclaimed |

---

## 3. `IPluginContext` capabilities

`IPluginContext` is the single entry point into the host. **Every interface is transport
agnostic** (async methods, DTOs, opaque ids only), so the same plugin source runs in both
`inProcess` and `isolated` mode.

### 3.1 Identity and infrastructure

| Member | Description |
| --- | --- |
| `PluginId` / `PluginVersion` | From `plugin.json` |
| `DataDirectory` | Private directory (already created). **All local writes belong here**; it is deleted on uninstall |
| `Host` (`IHostInfo`) | Host version, apiLevel, current locale and theme |
| `Log` (`IPluginLogger`) | `Debug/Info/Warn/Error` into the host log pipeline (prefixed with the plugin id) |
| `Shutdown` | Shutdown token: capability calls may start failing afterwards, so wind down |

### 3.2 Data

| Capability | Key methods | Notes |
| --- | --- | --- |
| `Storage` (`IPluginStorage`) | `GetAsync<T>` / `SetAsync<T>` / `RemoveAsync` / `GetKeysAsync` | Per-plugin namespaced KV backed by SonnetDB (JSON files in headless setups) |
| `Secrets` (`ISecretsApi`) | `GetAsync` / `SetAsync` / `DeleteAsync` | Encrypted private key-value store. **No plaintext fallback** — unavailable is reported as unavailable |
| `TimeSeries` (`ITimeSeriesApi`) | `OpenAsync` / `ListAsync` / `DropAsync`; on a series `WriteAsync` / `QueryAsync` / `CountAsync` / `DistinctTagValuesAsync` / `DeleteAsync` | A private embedded time-series store (append by time, retrieve by tag) |

### 3.3 Sessions and remote access

| Capability | Key methods | Notes |
| --- | --- | --- |
| `Sessions` (`ISessionsApi`) | `ListAsync` / `GetAsync` | Enumerate SSH sessions, **redacted, never credentials** |
| `RemoteFs` (`IRemoteFsApi`) | directory / attributes / read / write / transfer / rename / delete | SFTP over an existing session |
| `RemoteExec` (`IRemoteExecApi`) | `RunAsync` (whole result) / `StreamAsync` (per line) | A separate channel — **never the user's terminal** |
| `RemoteTunnel` (`IRemoteTunnelApi`) | `OpenUnixSocketAsync` / `OpenTcpAsync` | A **raw byte duplex stream** to a remote endpoint (Docker Engine API, tar streams). `inProcess` only |
| `Terminal` (`ITerminalApi`) | `GetOutputAsync` / `SearchOutputAsync` / `WriteAsync` | Read/search session output; **writing input requires user consent** (revocable on the manager page) |

### 3.4 UI and extension points

| Capability | Key methods | Notes |
| --- | --- | --- |
| `Commands` (`ICommandsApi`) | `Register` / `TryExecute` | Command ids must be prefixed by the plugin id; manifest placeholders are replaced by real handlers on activation |
| `Ui` (`IUiApi`) | `ShowPanelAsync(options, contentFactory)` | Present your own Avalonia controls: dockable main-window tabs in `inProcess`, standalone card windows in `isolated` |
| `TerminalView` (`ITerminalViewApi`) | `Create(...)` | **Borrows the host's terminal emulator** (VT parsing, screen buffer, selection, IME, key encoding) as a control you embed in your own UI. `inProcess` only |
| `Protocols` (`IProtocolsApi`) | register a protocol implementation | Your own remote **file** protocol, a first-class citizen of the connection page next to SSH/SFTP/FTP. `inProcess` only |
| `Workspaces` (`IWorkspacesApi`) | register a workspace provider | **Non-file** connection types (Redis, MySQL, …) whose session document you render entirely. `inProcess` only |
| `Clipboard` (`IClipboardApi`) | text read/write | System clipboard |
| `Events` (`IHostEvents`) | session connect/disconnect, theme and locale changes | Subscriptions are cleaned up by the host on deactivation |

> **The four `inProcess`-only capabilities** (`RemoteTunnel`, `TerminalView`, `Protocols`,
> `Workspaces`) throw "capability unavailable" in isolated mode. They hand out live native
> objects or raw streams, which have no equivalent across a process boundary — if you need them,
> set `hostMode` to `inProcess`.

---

## 4. Manifest

The full field table is in the [Development Guide §3](dev-guide.md); the three version gates are
in [Packaging and Publishing §1.2](publishing.md). Three things bite most often:

1. `id` is immutable after publication (command prefix + data namespace).
2. Using newer SDK surface requires `minSdkVersion`, otherwise old hosts fail at runtime with
   `MissingMethodException`.
3. Declarative contributions (`contributes.commands` / `protocols` / `workspaces`) take effect
   **during discovery**, without loading any assembly — that is what makes zero startup cost and
   lazy activation possible.

---

## 5. SDK version history

`apiLevel` only moves on **breaking** changes (still `1`); additive surface is gated by
`minSdkVersion`.

| SDK | Added | Does a plugin need `minSdkVersion`? |
| --- | --- | --- |
| 1.0 | First contract | — |
| 1.1 | `ExecResult` gains stderr and exit code; streaming remote exec | Yes, if used |
| 1.2 | `IRemoteTunnelApi` (raw byte duplex stream) | Yes, if used (`1.2.0`) |
| 1.3 | `ITerminalViewApi` (borrow the host terminal control) | Yes, if used (`1.3.0`) |
| 1.3.1 | Workspace **variants**: `WorkspaceVariant`, `VariantKey`/`Variants`, `NoCredentials`/`NoEndpoint` | Yes, if used (`1.3.1`) |
| **1.4** | `HostRegistry` (host self-registration for `vela-plugin`) | **No** — toolchain surface, plugin code never calls it |

---

## 6. Testing without starting the host

```csharp
using VelaShell.PluginSdk.Testing;

[TestMethod]
public async Task Activate_RegistersCommand()
{
    using var context = new TestPluginContext();
    var plugin = new DemoPlugin();

    await plugin.ActivateAsync(context, CancellationToken.None);

    Assert.Contains("acme.demo.run", context.RecordingCommands.Registered);
    await plugin.DeactivateAsync(CancellationToken.None);
}
```

Available doubles: `CollectingLogger`, `InMemoryStorage`, `InMemoryTimeSeries`, `FakeSessions`,
`FakeRemoteFs`, `FakeRemoteExec`, `FakeRemoteTunnel`, `FakeTerminal`, `FakeTerminalViewApi`,
`FakeUi`, `FakeSecrets`, `FakeClipboard`, `RecordingCommands`, `RecordingProtocols`,
`RecordingWorkspaces`, `TestHostEvents`, `TestHostInfo`.

What unit tests cannot cover (real UI, real sessions, real protocol tabs) belongs to the inner
loop: `vela-plugin dev init` → F5, see the [CLI Manual](cli.md).

---

## 7. Loading model: three rules

1. **One collectible ALC per plugin**; the plugin's own dependencies resolve inside its
   directory through `deps.json`, so you may reference any NuGet package without colliding with
   the host.
2. **Two assembly families are always shared**: `VelaShell.PluginSdk` and `Avalonia*` fall back
   to the loading side. Your Avalonia version must match the host's (the SDK package pins it),
   and third-party packages whose name starts with `Avalonia` cannot be used.
3. **Development plugins load from a shadow copy** (`~/.velashell/dev-shadow/<id>/gen-N`), so
   the running host does not lock your `bin` and Reload on the manager page picks up a rebuild.
   The production path does not use shadow copies.

---

## 8. Host registry (`HostRegistry`, SDK 1.4)

`VelaShell.PluginSdk.Hosting.HostRegistry` reads and writes `~/.velashell/host.json`, where the
host registers its executable path, version, apiLevel, bundled SDK version, Avalonia version and
data root on every launch.

It targets the **toolchain** (`vela-plugin dev init` / `doctor` / `hosts`); plugin runtime code
does not use it. If you write your own build script or IDE integration:

```csharp
HostRegistryEntry? host = HostRegistry.Resolve();          // most recently started
HostRegistryEntry? preview = HostRegistry.Resolve("1.5");  // by version
IReadOnlyList<HostRegistryEntry> all = HostRegistry.List();
```

Every read path returns an empty list instead of throwing when the file is missing or corrupt —
it is a cache that speeds up tooling, and a broken cache should mean "you have to point at the
path yourself", never "something fails to start".
