# 04 · PluginHost Process Design

> **Implementation note (2026-08)**: `src/VelaShell.PluginHost` is implemented. It provides one process per plugin, carries the pipe name/token through environment variables (not the command line), loads a collectible ALC, enforces time-limited activation/deactivation, watches the parent process to prevent orphans, redirects stdout/stderr to the host Trace, and includes Avalonia (full plugin UI). Crash handling is also implemented: a heartbeat (30s by default; two consecutive unanswered heartbeats are treated as a hang and force-killed) plus automatic restart after unexpected exit using a backoff sequence (1s→5s→30s by default; exceeding the limit within a five-minute window marks the plugin Failed and awaits user action). Idle reclamation is also implemented: `idlePolicy: "recyclable"` plus continuous idleness (no RPC traffic and no open panel, 15 minutes by default) disables the plugin and reclaims its process. The manifest's placeholder commands remain, and the process starts again when triggered.

PluginHost is a standalone executable distributed with the main application (`VelaShell.PluginHost`, `OutputType=Exe`, `net11.0`). Its sole responsibility is to **load one plugin in one isolated process, connect it to the host's RPC channel, and ensure that it exits cleanly**.

## 1. Process Startup and Parameters

The host starts PluginHost as follows (parameters are passed through environment variables to prevent command-line leakage into the process list):

```text
VELA_PLUGIN_ID        = acme.image-viewer
VELA_PLUGIN_DIR       = <installed/<id>/<ver>/ absolute path>
VELA_PLUGIN_DATA_DIR  = <data/<id>/ absolute path>
VELA_PIPE_NAME        = velashell-plugin-<random GUID>
VELA_AUTH_TOKEN       = <one-time 256-bit random token>
VELA_HOST_PID         = <host process id>
VELA_API_LEVEL        = 1
VELA_LOCALE           = zh-Hans
```

- **Authentication**: A random GUID is included in the pipe name, and the first frame must return AUTH_TOKEN. Together, these prevent other local processes from hijacking the connection (see 05 §3). On Windows, the pipe ACL additionally restricts access to the current user's SID. On Unix, socket-file permissions are 0600.
- **Orphan self-termination**: PluginHost continuously watches the host PID (waiting on the process handle). If the host disappears (crashes or is killed), PluginHost exits by itself within 2 seconds, ensuring that no orphan plugin process remains. Conversely, the host actively terminates all child processes on its exit path, with a finalizer fallback for abnormal exits. On Windows, Job Object `KILL_ON_JOB_CLOSE` provides a hard guarantee.

## 2. Assembly Loading (Collectible ALC)

```text
PluginHost default ALC: PluginHost itself + PluginProtocol + PluginSdk + StreamJsonRpc and other shared layers
      │
      └── PluginLoadContext : AssemblyLoadContext(isCollectible: true)
             Uses the entry assembly's .deps.json for resolution (AssemblyDependencyResolver)
             Loads the plugin and all of its private dependencies
```

Resolution rules:

- **Promote contract types**: `PluginProtocol` / `PluginSdk` / `StreamJsonRpc` and their transitive closures are always resolved from the default ALC (otherwise the types differ and the RPC bridge fails immediately). Maintain an explicit shared-assembly allowlist; for allowlisted matches, the loader returns null and lets the default ALC take over.
- All other dependencies are resolved from the plugin directory (bundled with the plugin), independently of PluginHost's dependency versions. This is the key benefit of D2: a plugin can use a different version of Newtonsoft.Json, SkiaSharp, and so on from the host.
- Native dependencies (`runtimes/<rid>/native`) are supported through the native resolution path of `AssemblyDependencyResolver`. The documentation explicitly warns: **plugins containing native code cannot be cleanly hot-unloaded** (native DLLs cannot be unloaded from the process). Such plugins use a process restart directly for "reload."

**Why use a collectible ALC when each plugin already has an exclusive process?** There are two purposes: (1) hot reload in development. `vela-plugin dev` watches build output, unloads the old ALC and loads new assemblies when files change, while keeping the process and connection alive for second-level inner-loop iteration; (2) retaining an implementation path for the future shared-host mode (multiple lightweight trusted plugins in one process). Production unloading/upgrading does not depend on successful ALC unloading. **Process exit is the final fallback**, so an ALC that does not unload cleanly (a common cause is leftover event subscriptions) is not a correctness problem.

## 3. Runtime Structure Inside the Host

```text
Main()
 ├─ Establish pipe connection + authentication + handshake (see 05)
 ├─ PluginLoadContext loads entry → reflection discovers [VelaPlugin] type → instantiate
 ├─ Attach JsonRpc bidirectionally:
 │    · Expose IPluginEndpoint locally (activate/deactivate/command/uiEvent/ping...)
 │    · Proxy IHostEndpoint remotely (entry point for all capability calls)
 ├─ Construct IPluginContext (capability proxies point to the remote endpoint) and pass it to the plugin's ActivateAsync
 └─ Block and wait: connection close / shutdown command / host PID disappears → orderly cleanup → exit
```

Threads and scheduling:

- PluginHost has **no UI thread**. Plugin callbacks run on the thread pool by default, and the SDK does not provide a SynchronizationContext. The documentation explicitly requires plugin code to be async-friendly. Long computations should use a thread created by the plugin. Even if a plugin blocks its own process, the host and other plugins are unaffected (this is the purpose of isolation), but heartbeat loss handling will be triggered (§4).
- Unhandled exceptions thrown by a plugin during an RPC call are serialized back to the host for logging and returned to the caller. Unobserved exceptions on background threads are logged through `AppDomain.UnhandledException`, after which the process exits and follows the crash-recovery path.

## 4. Health Monitoring and Fault Handling (Host-Side PluginSupervisor)

| Signal | Detection | Response |
| --- | --- | --- |
| Process exits (any reason) | Process.Exited | Record the exit code and log tail → crash-recovery flow |
| Lost heartbeat | Host sends `ping` every 5s; three consecutive unanswered pings (15s) | Mark Unresponsive: cover the UI surface with an "Unresponsive" overlay and offer "Wait/Terminate"; if still unresponsive after 60s with no user action, force-kill and enter crash recovery |
| Protocol error | RPC deserialization failure/unknown-method storm | Treat as a crash and force-kill |
| Memory limit exceeded | Periodically sample WorkingSet; exceeds manifest `resources.maxMemoryMB` (512 by default) | Send one GC notification, then retest after 10s. If still over the limit, handle as a crash; display a memory graph on the management page |
| Sustained CPU saturation | >90% continuously for 5 minutes within the sampling window | Do not kill automatically (it may be a legitimate long-running task); show a yellow warning on the management page and notify the user |

Crash recovery uses a backoff of 1s → 5s → 30s, with at most three attempts in a 10-minute window. Exceeding the limit enters Faulted (no more automatic starts, red marker on the management page, one-click log viewing/manual restart). **Incomplete calls are not replayed automatically after recovery**. Failed capability calls are truthfully returned to the caller as exceptions, and state restoration is the plugin's responsibility (the SDK provides the `Activation.IsRestart` flag and the Storage capability to help).

Resource-control implementation layers (an honest statement of what can be done):

- v1: **monitoring + response** (the table above). On Windows, the process is attached to a Job Object with `KILL_ON_JOB_CLOSE` and a hard memory limit. On macOS/Linux, v1 only monitors.
- v2 (combined with the sandbox roadmap in 12): Linux cgroups v2 and macOS `posix_spawn` resource attributes. See [12-security-threat-model.md](12-security-threat-model.md).

## 5. Shutdown and Unload Sequence

```text
Host sends shutdown(reason, timeoutMs=5000)
 → PluginHost cancels the context.Shutdown cancellation token
 → Call plugin DeactivateAsync (time-limited)
 → Flush logs, disconnect RPC, process exits with code 0
If timeout expires → host Kill(entireProcessTree: true) → record "unclean exit" count (visible on management page and used as a plugin-quality signal)
```

## 6. Development Mode (dev loop)

`vela-plugin dev --project .` does three things:

1. Build the plugin project with `--watch`.
2. Notify the host through a local management interface (enabled only in debug builds) to load the build output directory as a "development plugin" (skip signature validation; permission authorization follows the normal flow but is marked DEV).
3. On file changes, instruct the host to have PluginHost unload the ALC → reload → replay `Activate` (retain permission grants and UI placeholders without restarting the process).

For debugging, `vela-plugin dev --wait-debugger` makes PluginHost wait for a debugger to attach after loading. In VS/Rider, attach directly to the PluginHost process to set breakpoints (the template includes launchSettings configuration).

## 7. Development Plan (This Workstream)

| Task | Description | Dependency | Estimate |
| --- | --- | --- | --- |
| H-1 | PluginHost executable skeleton: parameters, authenticated connection, orphan self-termination, exit cleanup | A-2 | 3d |
| H-2 | PluginLoadContext: deps.json resolution, shared-allowlist promotion, native dependencies; unload round-trip tests | H-1 | 3d |
| H-3 | Host-side process management: startup, Job Object (Windows), shutdown sequence, force-kill fallback | H-1 | 3d |
| H-4 | PluginSupervisor: heartbeat, crash detection, backoff restart, Faulted transition; integrate with the state machine in 03 | H-3, M-3 | 4d |
| H-5 | Resource monitoring (memory/CPU sampling and response) + management-page data source | H-4 | 2d |
| H-6 | Logging pipeline: persist stdout/stderr, structured logging channel, log-view entry point | H-1 | 2d |
| H-7 | Development mode: dev loading, ALC hot reload, wait-debugger | H-2, dependency on the CLI skeleton in 09 | 3d |
| H-8 | Chaos test group: four fault-injection plugins that kill the process, block a thread, consume memory, or send malformed protocol bytes + automated acceptance (zero main-application impact) | H-4 | 3d |

Acceptance (corresponding to G1): all four fault-injection plugins from H-8 pass CI. Whenever any fault occurs, the main-process UI thread remains unblocked (frame-rate probe), RPC latency for other plugins does not degrade, and the faulty plugin follows the expected state-machine transitions.
