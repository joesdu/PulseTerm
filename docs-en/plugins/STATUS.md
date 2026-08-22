# Plugin System Progress Overview

> 📦 **Repository split note (2026-08-21, revised 2026-08-22)**: the plugin SDK, `dotnet new`
> templates, the `vela-plugin` CLI and the Redis / S3 / Telnet / HelloWorld plugins moved to
> [joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain).
> Paths such as `plugin-sdk/…`, `tools/…`, `templates/…` and
> `tests/VelaShell.Plugin.{Redis,S3,Telnet,HelloWorld}.Tests` that appear on this page
> (and in blueprints 01–15) now refer to that repository.
>
> **Exception: the AI plugin (`plugins/VelaShell.Plugin.Ai`, tests in
> `tests/VelaShell.Plugin.Ai.Tests`) stays in this repository** and is built and released
> together with the app — it is coupled to the host at compile time (it borrows the host's
> AvaloniaEdit, must load in-process, and must compile against the exact Avalonia version the
> host loads). The reasoning is in [`plugins/README.md`](../../plugins/README.md).
> This repository therefore keeps `src/` (host implementation), `plugins/` (the AI plugin) and
> `tests/` (host tests, with plugin fixtures under `tests/fixtures/`).
> The conclusions and acceptance evidence are unaffected — only which repository the paths live in.

> Updated: 2026-08-12. This page is the **single source of truth** for implementation progress: each blueprint area is listed as complete / partially complete / not started, with acceptance evidence (tests) and recommended next steps. To write a plugin, read [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md).

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
| `VelaShell.Plugin.Cli` | dotnet tool | `vela-plugin`: dev init/run/list/prune/link, hosts, doctor, validate, pack, sign, verify, info, unpack, keygen |
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

## 2026-08-23: faster load path and a background-activity indicator

Two user-visible problems, handled together: plugin loading is slow (especially the first trigger of
a lazily-waiting plugin), and **while it is slow the UI says nothing at all** — the click just looks
like it did not register.

### Background-activity indicator (bottom-right of the status bar)

- New `Core/Services/BackgroundActivityService`: a global ledger of background work (begin → report
  progress → dispose). It only records; it schedules nothing. Structural changes (begin/end) notify
  immediately, while pure progress updates are coalesced into a 120 ms window — a tight per-file
  reporting loop must not flood the UI dispatcher (the same trap large-file transfers fell into).
- New `Controls/CircularProgressRing`: a hand-drawn ring (JetBrains style). Indeterminate mode spins
  a fixed-length arc; determinate mode sweeps clockwise from 12 o'clock. A `DispatcherTimer` drives
  the spin, and **only while it is genuinely spinning and genuinely visible** — the tick rechecks
  `IsEffectivelyVisible`, which in Avalonia 12 is not an AvaloniaProperty and therefore cannot be
  observed.
- It sits at the leading edge of the status bar's right-hand group, so the fixed-width fields to its
  right do not shift relative to the window edge as it appears and disappears. Hovering lists every
  activity; clicking opens the same list as a flyout. Aggregation rule: **if any one activity cannot
  state its progress, the whole ring goes indeterminate** — folding "unknown" into an average as
  zero produces a percentage that is simply a lie.
- The only producer today is the plugin runtime (loading / verifying / prewarming). The ledger is
  generic: cloud sync, SFTP transfers and the GeoIP download can all be attached to it.

### Load-path speedups

1. **Discovery no longer hashes plugin contents.** `ValidateInstallReceipt` reads every byte of every
   installed plugin; hanging that off `Describe` parked startup on the disk. Now:
   - discovery reads manifests only, so protocol tabs and placeholder commands are available
     **immediately**;
   - verification runs in the background **in parallel** (degree 4 — this is disk-bound work, and more
     threads only make the IO queue fight itself), and `StartAsync` still awaits it, so the external
     contract "a modified plugin is already flagged by the time StartAsync returns" is unchanged;
   - **the security boundary did not move**: `EnsureActivatedAsync` gained a gate that every load path
     must pass. The result is memoized per plugin, so reactivating after idle recycling does not pay
     the cost again.
2. **onStartup plugins activate in parallel**, instead of queueing up behind the slowest one.
3. **Housekeeping moved to the end**: `PurgeOrphanShadows` / `PurgeUninstalledDataAsync` have nothing
   to do with whether a plugin is usable, so they run on a background chore chain.
4. **Cold-start prefetch** (`PrewarmLazyPlugins`, on by default, `VELASHELL_DISABLE_PLUGIN_PREWARM=1`
   to stop it): five seconds after the main window has painted its first frame, the top-level DLLs of
   every lazily-waiting plugin are read once, purely to lift them into the OS file cache. It **does
   not load assemblies, create an ALC, or run `ActivateAsync`** — lazy activation's semantics are
   untouched and memory does not grow (what is read lands in kernel page cache). Plugins whose
   directories verification just read are skipped: for installed plugins the prefetch rides along
   with that hashing pass for free.

> Deliberately **not** done: a warm process pool for isolated plugin hosts. The bulk of isolated
> start-up cost is the .NET runtime plus Avalonia initialization (300–800 ms); removing it means
> moving PluginHost's plugin identity from environment variables to a post-handshake RPC push, which
> is a wide protocol change. Left for the next round.

Evidence: `PluginLoadingPipelineTests` (5, under `TestCategory=Plugins`) — loading leaves a trace on
the ledger and always clears it, the failure path clears it too, several onStartup plugins all end up
active, prefetch reads files but never activates, prefetch off leaves no trace;
`BackgroundActivityServiceTests` (8, including 64 concurrent activities settling back to empty — the
ring must not spin forever); `StatusBarBackgroundActivityTests` (6, aggregation rules); and one UI
test, `StatusBar_BackgroundRing_*` (hidden → visible → solid arc → hidden).

## 2026-08-23 (2): more producers on the indicator, build hygiene closed out

The first round of making the ring pay for itself: wire it to the places users actually wait on,
and turn the plugin-mirroring build target into a real mirror.

### Plugin installation on the ring

`InstallFromVpxAsync` is several seconds of pure waiting — verify signature → extract → SHA-256 the
whole directory for the install receipt — and until now the UI sat perfectly still
(`PluginManagerViewModel` only posted a notice once it was over). It now reports per phase: the
package filename is the subtitle first, swapped for the plugin's display name once the manifest is
readable. Progress is a coarse ladder rather than a smooth curve: none of these steps offers
fine-grained callbacks, and rather than invent a smooth fake, the arc jumps a step at a time — every
jump corresponds to something genuinely finished.

> Note: the plugin store (market.easilynet.top) is a link opened in the browser; the download does
> not happen in-app. The slow seconds are entirely local — verification, extraction, hashing.

**One thing deliberately not done**: activating right after install re-computes the directory hash
that `SaveInstallReceiptAsync` just computed. It looks wasteful, but do not pre-seed the verification
result to skip it — that would carve the window between "receipt written" and "first load" out of
verification, and all it buys is one hash of a directory that is already in page cache (tens of
milliseconds). This repository's posture on the plugin trust surface has consistently been to hash
twice rather than trade a window for that. A comment in the code holds the line against the next
person who wants to "just optimize this".

### Cloud sync on the ring

The four entry points of `GistSyncService` (sync now / push only / pull only / restore revision) each
open an indeterminate activity, with the subtitle reusing the label of the identically-named button
on the settings page (`SetSync_*`, already present in all five languages, nothing new added) — the
wording a user sees matches the button they pressed. Indeterminate because the whole span is a
network round trip: there is no meaningful percentage, and the question this activity answers is
only ever "is it syncing right now?".

Automatic sync has always been silent (startup pull, debounced push after a save, failures never
interrupt). **"Not interrupting" should not mean "invisible"**: the user's connection profiles are
being uploaded or overwritten, and that deserves somewhere to show up.

### Build hygiene: the mirroring target actually mirrors now

`CopyVelaPluginsToOutput` (which lays the `artifacts/plugins/` staging directory into the host's
output) only ever added, never removed, and the files it copied were never recorded in MSBuild's
clean ledger. Both consequences have genuinely bitten:

- delete a plugin from staging and the copy in the output stays until someone removes it by hand;
- `dotnet clean` could not remove them (their names were not on the ledger), which shows up as
  "I cleaned and the plugins are still there".

The fix: the target moved from `AfterTargets="Build"` to `AfterTargets="CopyFilesToOutputDirectory"`
(ahead of `IncrementalClean`), and its copies are registered as `FileWrites`. Pruning and clean then
come free from MSBuild's own incremental-clean machinery. One sweep is added on top:
`IncrementalClean` removes files, so a pruned directory is left behind empty — the sweep removes
directories with no `plugin.json` (equivalent to empty as far as the host is concerned) so that
`ls plugins` never shows a plugin name that should have disappeared.

Only this target's copies are registered: the self-built plugin (`velashell-ai`) is laid into the
same `plugins/` by its own project and is not on this ledger, so it can never be pruned by mistake —
verified by test.

Evidence (four scenarios, measured): staging mirrors into the output; removing a plugin from staging
prunes it on the next build; the emptied directory is swept; `dotnet clean` removes the mirrored
plugins and leaves `velashell-ai` alone. New regressions:
`InstallFromVpx_ReportsProgressToTheBackgroundLedger_AndClearsIt`,
`InstallFromVpx_WhenRejected_StillClearsTheBackgroundLedger`,
`SyncEntryPoints_ReportToTheBackgroundLedger_AndAlwaysClearIt` (all four exits distinguishable and
each one always cleared).

### Reviewed, and deliberately deferred

- **Warm process pool for isolated plugin hosts**: a full activation cycle measures at roughly one
  second (process launch plus Avalonia initialization dominate), so the number is real. But
  `velashell.ai` and `velashell.redis` must both run `inProcess` (the former borrows the host's
  AvaloniaEdit, the latter hands the host an Avalonia control to dock), so **there is not a single
  isolated plugin today** — the pool would help nobody. Revisit when third-party isolated plugins
  actually appear.
- **ReadyToRun**: publishing uses `-p:SelfContained=true` with **no** `PublishReadyToRun`, so both
  the app and `VelaShell.PluginHost` JIT cold on the user's machine. Enabling it benefits every user
  on every launch and would absorb part of the isolated host's cold start without touching the IPC
  protocol. The cost is 30–50% larger R2R'd assemblies (noticeable under self-contained). **The
  benefit must be measured**, so it gets its own round: change the configuration, measure cold start
  on all three platforms, then decide whether the size is worth it.
