# Plugin System Progress Overview

> Updated: 2026-08-12. This page is the **single source of truth** for implementation progress: each blueprint area is listed as complete / partially complete / not started, with acceptance evidence (tests) and recommended next steps. To write a plugin, read [dev-guide.md](dev-guide.md).

## I. Overall Status

**The plugin system framework layer is production-ready**: dual host modes, a complete capability surface (9 capability domains), UI (full Avalonia, in-process dock / isolated independent window), reliability (heartbeat/self-healing/reclamation), data layer (isolated SonnetDB storage + unload cleanup), management page (enable/disable/uninstall/.vpx installation), SDK test doubles, and developer documentation are all ready.
**The first first-party business plugin has shipped: the AI assistant plugin** (`plugins/VelaShell.Plugin.Ai`, ID `velashell.ai`): multi-provider streaming conversations (three wire protocols—OpenAI Responses / OpenAI Chat Completions compatible / Anthropic Messages—covering OpenAI/Grok/Ollama/relay services, user-supplied Base URL + API Key, with the key encrypted through Secrets) + Agent mode (Microsoft.Extensions.AI `FunctionInvokingChatClient` tool loop, bridging sessions/terminal/remoteExec/remoteFs, with dangerous operations approved individually in the tool panel). Lazy activation through `onCommand`, with five-language copy. Acceptance: 14 `VelaShell.Plugin.Ai.Tests` cases (toolbox approval gate/capability-bridge semantics/settings and secret access). The container-management plugin has not started.

Quality baseline (full regression each round): full-repository build with 0 warnings and 0 errors; approximately 1,280 tests all green, including 75+ plugin-specific tests (with real two-process end-to-end coverage: cross-process activation, process-kill self-healing, idle reclamation and relaunch, embedded/streaming/terminal RPC paths, and .vpx install/uninstall).

## II. Progress by Area

### ✅ Completed

| Area | Blueprint | Implemented form | Acceptance evidence |
| --- | --- | --- | --- |
| SDK contract and manifest | 03/09 | `plugin-sdk/VelaShell.PluginSdk` (BCL only); validation of every manifest field (20+ malformed cases rejected with readable errors) | PluginManifestReaderTests, LazyActivationTests |
| In-process host | 02 | Collectible ALC, fault guard, zero startup overhead for background activation | PluginManagerTests (real ALC e2e) |
| Isolated-process host | 02/04/05 | PluginHost process + custom lightweight RPC over named pipes (deliberately does not use StreamJsonRpc; see the note in 05); token handshake, parent-process watchdog, credentials never leave the main process | IsolatedPluginTests, RpcConnectionTests |
| Reliability | 04 | Heartbeat (force-kill after 2 failures at 30s intervals), crash-backoff automatic restart (1s/5s/30s, Faulted after the window limit), idle reclamation (recyclable) | Process-kill self-healing e2e, reclamation/relaunch e2e |
| Lazy activation | 03 | `onStartup` / `onCommand:<id>` + `contributes.commands` placeholder commands | LazyActivationTests |
| UI (full Avalonia) | 08 (redirected) | VelaUI declarative tree **not implemented by user decision**; plugins use full Avalonia directly (compile-time AXAML/bundled styles/i18n/third-party packages), constrained only by matching the host's Avalonia version (forced ALC sharing) | PluginPanelUiTests |
| Dual dock/window forms | 08 | In-process: draggable dock tab splitting + custom-drawn card window; isolated: independent card window (`PluginHostShellWindow`, matching the resource-monitoring window specification) | Full-path UI API tests |
| Theme tokens | 08 | `{DynamicResource Vela*}` works in both modes (isolated mode sends an RPC snapshot + pushes again on switching) | PluginThemeTokensTests |
| Capability domains: sessions/remoteFs/remoteExec | 07 | Reuse host connections, throttle progress updates, and enforce semantics such as returning null for a missing path from Stat | Contract-level tests + e2e |
| Capability domains: commands/events | 07 | Command-palette registration (prefix required/automatic cleanup), session/theme/language events | PluginCommandsApiTests |
| Capability domains: storage/secrets/clipboard | 06/07 | SonnetDB `plugin_data` single collection with a composite primary key, **strict isolation by plugin**; secrets encrypted with DPAPI at rest; automatic cleanup on uninstall (disabled ≠ uninstalled) | SonnetDbPluginDataStoreTests, cleanup tests |
| Capability domain: terminal (read/search/authorized write-back) | 07 | Buffered snapshot reads + regex search; write-back through an authorization gate (once/this session/always/deny, with always persisted in SonnetDB) + serialized input queue | PluginPermissionGateTests, HelloWorldTerminalTests |
| Capability domain: remoteFs streaming reads | 07 | Sequential `OpenReadAsync` stream; isolated-mode RPC chunking (openRead/streamRead/close, automatic release at EOF) | StreamingRoutingTests |
| Plugin management page | 02/06 | Sidebar plugin icon → custom-drawn card window (matching the resource-monitoring specification: min/max/close + resize): list/status/enable-disable/**uninstall**/**install from .vpx**/revoke terminal authorization; Changed triggers automatic refresh | PluginManagerEnableDisableTests, PluginInstallUninstallTests |
| SDK test doubles | 09/13 | `TestPluginContext` + in-memory doubles for all capabilities, allowing plugins to be unit-tested without a host | Dogfooded by HelloWorld tests |
| Examples and inner development loop | 09 | HelloWorld (AXAML panel/bilingual/all-capability demonstrations); F5 automatically rebuilds the mirror plugin | HelloWorldDemoPanelTests |
| Developer documentation | 09 | dev-guide.md (sole authority); implementation notes added to blueprints 01–15 | — |

### ⏳ Partially Complete

| Area | Available | Gap |
| --- | --- | --- |
| Activation events | onStartup / onCommand | onSessionConnect, onFileOpen, onSchedule, onUri (blueprint 03 §4) |
| UI mount points | Command palette, dock document, independent window, plugin management page | Sidebar view, status bar, settings page, context-menu contribution points (blueprint 08) |
| Cross-process dock embedding | Retained at the RPC protocol layer (EmbedRoutingTests), but the **host Win32 implementation has been removed**: cross-process window adoption fundamentally conflicts with dock reparenting (stutter/windows escaping), and is **deprecated** | Isolated plugins always use independent card windows; true dock tabs use in-process mode; stable cross-platform approach = shared-memory surface (blueprint 08 §4, long term) |
| Distribution form | Directory as plugin + **one-click install/uninstall of .vpx package** (ZIP, zip-slip protection); SDK used via ProjectReference; **published artifacts carry `plugins/<directory-name>/` and `VelaShell.PluginHost.*`** (the former follows each plugin's `<VelaPluginShip>` choice, example plugins are not packaged, and directory name = ID with dots replaced by hyphens to avoid nested-bundle misclassification by macOS codesign; to ensure the host process has a real executable on disk, the main application has used flat publishing since 2026-08-12) | SDK NuGet package publishing, `dotnet new` templates, .vpx signing/validation (blueprints 09/10) |

### ❌ Not Started

| Area | Blueprint | Description |
| --- | --- | --- |
| Permission system + Broker | 06 | **Not implemented by user decision** (first-party/self-installed plugins, trust comes with installation); revisit if a third-party ecosystem opens in the future |
| .vpx signing / store | 10 | .vpx installation/uninstallation is complete (see management page); **signature verification and store distribution** remain deferred by user decision |
| Capability domains: localFs / audio / net / ai | 07/11 | Not exposed. Recommend defining `vela.ai` when work begins with the AI plugin (the terminal domain is complete) |
| Plugin management page log viewer | 02/06 | List/enable-disable/revoke authorization are complete; tailing each plugin's logs is deferred |
| Advanced SonnetDB models exposed to plugins | — | Time-series/full-text/vector, etc. are not exposed to plugins for now; revisit based on real demand (apiLevel follows additive-only discipline) |
| First-party business plugins | 15 | The AI plugin and container-management plugin have not started (the framework is ready) |

## III. Deliberate Architectural Decisions (Do Not "Correct")

1. **The VelaUI-lite declarative tree was removed**—plugin UI = full Avalonia (user decision, converged over three rounds).
2. **Custom lightweight RPC protocol** instead of StreamJsonRpc + MessagePack (zero-dependency discipline; see the note in 05).
3. **Both host modes coexist**, with manifest `hostMode` selecting the mode; plugin source requires zero changes between the two modes.
4. **Plugins never connect directly to SonnetDB**: capability instances are namespaced by plugin ID; all data from isolated processes goes through RPC.
5. **PluginHost uses software rendering by default** (saving most of the GPU-driver mapping memory per process); `VELA_PLUGIN_GPU=1` enables it.
6. **Isolated plugins always use independent card windows** (`PluginHostShellWindow`, matching the resource-monitoring window specification); only in-process mode uses dock tabs. Cross-process dock embedding is deprecated (fundamentally conflicts with dock reparenting), its Win32 implementation has been removed, and only the RPC protocol remains for future reuse with shared-memory surfaces.

## IV. Known Items Awaiting Validation

- The **visual appearance** of the terminal authorization dialog, plugin management window, and isolated plugin independent window (`PluginHostShellWindow`) has only had logic tests; it has not been visually checked through F5 (dialog field population is covered by regression tests; the isolated panel background is already bound to the `VelaBgSurface` token).
- Isolated e2e on CI (GitHub Actions) depends on the runner being able to launch child processes and the Avalonia Win32 platform; the local suite is all green, but the first CI run requires attention.

## V. Recommended Next Steps (Ordered by Value)

1. **Write the first business plugin** (container management: RemoteExec + an AXAML panel is enough to form a working version)—use real requirements to feed gaps back into the framework;
2. Validate the visual appearance on a real machine via F5 (authorization dialog / management window / isolated plugin independent window), and fix visual rough edges;
3. Validate **packaged isolated mode** on a real machine: as of 2026-08-12, the main application uses flat publishing (`plugins/` and `VelaShell.PluginHost.*` are both shipped in the package), self-updater replacement changed from "move" to "copy", and the updater now runs in place in the unpacked directory; run the complete flow on all three platforms: "install a third-party isolated plugin → use it → replace via self-update → restart";
4. Define the `vela.ai` capability-domain interface alongside the AI plugin (blueprint 11);
5. Add sidebar/status-bar mount points (the final contribution points before building the UI ecosystem).

## 2026-08: SDK productization (NuGet packages / templates / dedicated package format / debugging loop)

The plugin SDK went from "in-repository projects consumed via ProjectReference" to a real,
externally distributed SDK.

**Five NuGet packages** (versioned by `VelaSdkVersion`, decoupled from the host version;
`AssemblyVersion` is `<major>.0.0.0` and moves only with the major — it is the identity plugins bind
to at compile time, and moving it per patch would force every compiled plugin to rebind for nothing,
while `FileVersion` and `InformationalVersion` track the real version. The rule is **SDK major ==
`apiLevel`**: a major bump means the contract broke, so `VelaPluginApi.Level` goes up with it and an
older host rejects the plugin cleanly at discovery time instead of throwing an assembly binding error
at load time):

| Package | Referenced by | Contents |
| --- | --- | --- |
| `VelaShell.PluginSdk.Build` | **the plugin project (this one alone)** | MSBuild props/targets plus the bundled packer; transitively brings in the two below and **Avalonia pinned to the host's exact version** |
| `VelaShell.PluginSdk` | transitively | Contract assembly (BCL only) |
| `VelaShell.PluginSdk.Testing` | plugin test projects | `TestPluginContext` and capability doubles |
| `VelaShell.Plugin.Cli` | dotnet tool | `vela-plugin`: validate / pack / sign / verify / info / unpack / keygen / install / dev-link |
| `VelaShell.Plugin.Templates` | dotnet new | `velaplugin`, `velaplugin-ui` |

The Build package handles four things on the plugin project's behalf: `EnableDynamicLoading` and
`plugin.json` output; keeping shared assemblies (`VelaShell.PluginSdk` and `Avalonia*`, matching
`PluginAssemblyLoadContext` exactly) out of the plugin directory; Avalonia version consistency
(NU1608 promoted to an error plus a `VELA1001` build-time check); and manifest validation with
`dotnet build -t:PackVpx`.

**`.vpx` became a dedicated container** (`VelaShell.PluginSdk/Packaging/VpxContainer.cs`, one
implementation shared by host and tooling): a 64-byte header (magic `56 50 58 1A`, format version,
flags, payload length, SHA-256, mask nonce, header CRC32), a masked zip payload, and an optional
ECDSA P-256 signature block. The magic bytes and mask only stop "rename it and unzip"; integrity
and provenance come from the digest and the signature. An invalid signature is always rejected
even with no signing policy configured, while unsigned packages are allowed by default. Install
time also enforces zip-bomb limits (10,000 entries / 512 MB, accounted by **bytes actually
written**). There is **no plain-zip compatibility path**: no `.vpx` package shipped before the
container format was defined, so there is no installed base to protect, and a renamed zip is
rejected outright with the repack command in the error message.

**Debugging loop**: `plugins.dev.txt` / `VELA_PLUGIN_DEV_ROOT` mount a plugin project's output
directory straight into the host (badged DEV on the management page; an installed plugin with the
same id wins). `VELA_PLUGIN_WAIT_DEBUGGER=<id>|*` makes an isolated plugin's child process wait for
a debugger before loading the assembly, and **relaxes the activation timeout and stops the
heartbeat** at the same time (otherwise a breakpoint reads as a hung plugin and the process is
killed). In-process plugins get the relaxed activation timeout whenever `Debugger.IsAttached`.

**The manifest gained `author`** (display-only, distinct from `publisher`, which is the trust
identity; ≤128 characters, control characters rejected). It is shown on the plugin manager page and
falls back to `publisher` when absent.

**CI**: `.github/workflows/nuget.yml`, triggered by an `sdk-v<version>` tag. Before publishing it
runs the packaging tests and an **end-to-end template smoke test** (install templates → generate a
project → restore → build → produce a .vpx → re-read the container → assert no shared assemblies
leaked into the plugin output). That step is not ceremony: it is what caught "the SDK carries
`RequiresPreviewFeatures`, so plugin projects that do not opt into preview features fail with
CA2252 everywhere" and "NuGet does not flow build assets transitively, so Avalonia's AXAML compiler
never reached the plugin project" — both invisible from inside the repository.
