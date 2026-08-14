# 01 · Vision and Goals

## 1. Vision

Today, VelaShell is an SSH/terminal/SFTP workbench. The goal of the plugin system is to turn it into a **platform**: the core remains lean, stable, and fast, while all "nice-to-have" capabilities, such as an AI assistant, media viewing, log analysis, and bulk operations automation, are provided by plugins, with the following properties:

1. **Isolated**: Plugins run in separate processes. A hung or crashed plugin does not affect the main application, and plugins cannot see one another.
2. **Controlled**: Users decide what plugins can do. An Android-style permission model explicitly prompts for authorization the first time a sensitive capability is used, and permissions can be revoked at any time.
3. **Open**: A stable public SDK and standard packaging format let anyone develop and publish plugins.
4. **Dynamic**: Plugins can be installed, enabled, disabled, uninstalled, and upgraded without restarting the main application.

## 2. Typical Scenarios (Use Cases Driving the Design)

Every capability in the design must support at least one of the scenarios below. Conversely, each scenario corresponds to an acceptance-test example plugin in [14-roadmap.md](14-roadmap.md).

| # | Scenario | Required capabilities |
| --- | --- | --- |
| S1 | **Image viewer**: Double-click a remote image in the SFTP panel to open a preview page in the document area, with zoom support | Remote file reading, document-page contribution point, image surface (shared-memory bitmap) |
| S2 | **MP3 player**: Browse and play local/remote music files, with playback progress shown in the status bar | Local/remote file reading, audio output service, VelaUI panel, status-bar contribution point |
| S3 | **AI assistant**: Explain an error selected in the terminal, generate a command from natural language, and insert it back into the terminal | Terminal reading (selection/output), terminal writing, AI gateway, sidebar view |
| S4 | **Log highlighting/analysis**: Subscribe to the terminal output stream, identify error patterns, and aggregate them in a sidebar | Terminal-output subscription, sidebar view, notifications |
| S5 | **Automation**: Automatically execute a set of commands after a session connects, and trigger remote verification after a file transfer completes | Event subscriptions, remote execution, automation rule engine |
| S6 | **Server dashboard**: Periodically run collection commands on a session and render CPU, memory, and disk charts | Remote execution, scheduled triggers, VelaUI |
| S7 | **Enhanced configuration-file editing**: Provide additional syntax definitions or linting for remote editing scenarios | Remote file read/write, (future) editor extension point |

## 3. Goals

- G1: Plugins and the main process have **process-level isolation**. A plugin using 100% CPU, consuming excessive memory, crashing, or deadlocking must not affect the responsiveness of the main application UI, and users can terminate it with one click from the plugin management page.
- G2: Plugins are **isolated from one another by default** (separate processes, separate data directories, and no calls between plugins).
- G3: The **permission system** covers every sensitive capability. Calls to undeclared permissions fail immediately. Dangerous permissions show a first-use prompt, and authorization decisions can be persisted and revoked from the settings page.
- G4: **Dynamic lifecycle management**: Installation, uninstallation, upgrades, and disabling take effect immediately without restarting the main application. After uninstallation, the plugin's contribution points, process, and temporary resources are all cleaned up.
- G5: **Public SDK**: A `VelaShell.PluginSdk` NuGet package, a `dotnet new` template, a packaging CLI, and an F5 debugging experience. The SDK promises backward compatibility within an apiLevel.
- G6: **Remote capabilities**: With authorization, plugins can access SFTP and command execution on sessions already connected by the user, reusing the host's connections (without re-authenticating or creating another connection unless explicitly requested).
- G7: **UI extensions**: Plugins can contribute commands, menus, status bars, sidebar views, document pages, and settings pages, and render custom interfaces through the declarative VelaUI protocol.
- G8: **Five-language consistency**: Plugin manifest and contribution-point text support localization (`%key%` plus `plugin.nls.<locale>.json`) and work with the main application's existing five-language mechanism.
- G9: **Performance**: The main application's cold-start time must not degrade significantly because "N plugins are installed." Plugins use lazy activation by default, driven by activation events, and no plugin processes are started during application startup.

## 4. Non-Goals

- N1: **No browser-level security sandbox initially**. The first goal of the permission system is *transparency and user control*, not protection against deliberately malicious native code. A plugin process is still an ordinary OS process, and a malicious plugin can bypass the Broker and call OS APIs directly. Protection against malicious code depends on signature and source trust (see 10) and the OS-level sandbox evolution planned in the threat model (see 12). VSCode also does not sandbox extensions, which is common in the industry, but we must clearly and honestly inform users of this in the documentation and UI.
- N2: **No in-process plugins**. There is no back door to "load plugins into the main process for performance," avoiding erosion of the isolation promise. The host's own built-in features do not use the plugin channel.
- N3: **No WebView-style plugin UI initially**. Embedded WebView support in the Avalonia ecosystem is not yet mature and carries a substantial size cost. UI extensions primarily use contribution points and VelaUI, with WebView listed for future reassessment.
- N4: **No cross-plugin dependencies or calls initially** (plugin A depends on plugin B). Manifest fields are reserved for this, but the first version rejects packages that declare dependencies.
- N5: **No native UI windows embedded in plugins**. Windows opened directly by plugins are not managed by the host and undermine a consistent experience. This cannot technically be prohibited, but no API support is provided and such behavior is considered non-compliant.
- N6: **Non-.NET plugins (JS/Python/native) are out of scope for the first version**. The IPC protocol itself is language-neutral (JSON-RPC), leaving room for future multi-language SDKs, but the first version publishes only a .NET SDK.

## 5. Comparisons with and Lessons from Existing Systems

| System | Lessons adopted | Not adopted |
| --- | --- | --- |
| **VSCode** | Activation events, contribution-point declarations, `package.json` manifests, an extension-host process, and the NLS localization mechanism | All extensions share one host process (one hung extension affects all of them), whereas we default to **one process per plugin**; WebView UI |
| **Android** | Permission levels (normal/dangerous), first-use prompts, centralized management and revocation in settings, and permission groups | One-time authorization of all permissions at installation (legacy model); we do not use install-time authorization |
| **Chrome extensions** | Prominent permission display on store pages, and host permissions that narrow authorization by target domain/path | — |
| **JetBrains plugins** | Compatibility-range validation and marketplace review workflows | In-process loading (a crashed plugin can bring down the IDE) |
| **Native plugins such as OBS/Audacity** | — | In-process native plugin model (conflicts with the isolation goal) |

The key differentiating decision is **one independent PluginHost process per plugin by default**. VSCode's single extension host is a pain point we explicitly aim to avoid: one extension entering an infinite loop can disable every extension. The cost is approximately 30–60 MB of base memory and process startup time per process, offset by "lazy activation + idle reclamation" (see 04).

## 6. Success Metrics

- Killing any plugin process leaves the main application completely unaffected (except that the plugin's UI becomes dimmed), with automated test coverage.
- P95 from an activation event triggering a plugin to `Activate()` returning is < 800 ms (< 300 ms after warm-up).
- A "Hello World" plugin can go from `dotnet new` to running in VelaShell in ≤ 5 minutes.
- Three official example plugins (image viewer, MP3 player, and automation) are implemented entirely with the public SDK.

## 7. Development Plan (This Workstream)

This document is a guiding document and has no independent development tasks. Its acceptance criteria are the success metrics in Section 6, implemented across the milestones in [14-roadmap.md](14-roadmap.md).
