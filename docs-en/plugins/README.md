# VelaShell Plugin System Design Documents

> 📦 **Repository split note (2026-08-21)**: the plugin SDK, `dotnet new` templates, the
> `vela-plugin` CLI and all first-party plugins moved to
> [joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain).
> Paths such as `plugin-sdk/…`, `plugins/VelaShell.Plugin.…`, `tools/…`, `templates/…` and
> `tests/VelaShell.Plugin.*.Tests` that appear on this page (and in blueprints 01–15) now
> refer to that repository; this one keeps only `src/` (host implementation) and `tests/`
> (host tests, with plugin fixtures under `tests/fixtures/`).
> The conclusions and acceptance evidence are unaffected — only which repository the paths live in.

> Status: **v1 simplified version implemented** (2026-08: dual host modes + full Avalonia UI); documents 01–15 are retained as the **long-term design blueprint** for the complete plugin platform (process isolation, permission system, packaging/distribution/store, etc. will be delivered in phases as needed; the distribution system has been explicitly deferred). Any decision changes made during implementation should be written back to the corresponding document.
>
> ⚠️ **The four plugin-author documents moved out with the toolchain** (2026-08-21): `dev-guide.md` / `cli.md` / `publishing.md` / `sdk-reference.md` now live in **[joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain/tree/main/docs-en)**, next to the SDK, templates, CLI and first-party plugins they describe — documentation for plugin authors belongs with the code it documents. What stays here is the **host-side** blueprint.
>
> **To write a plugin, read [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md) directly** — it describes the APIs that are actually available today; the interface shapes in the blueprint documents (one process per plugin, Broker, etc.) are not implemented in v1.

## One-Sentence Vision

Make VelaShell an **extensible operations workbench**: plugins run in independent PluginHost processes and are fully isolated from the main application and from one another—plugin hangs or crashes do not affect the main application; plugins obtain sensitive capabilities such as remote files, local files, and terminals through Android-style explicit authorization; developers use the official SDK and standard interfaces to develop, package, and publish plugins, while users can install and uninstall them dynamically.

## Document Map

| Document | Contents | Audience |
| --- | --- | --- |
| [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md) | **Development guide (implemented)**: quick start, manifest, lifecycle, capability APIs, isolation modes, testing, deployment, performance discipline | Plugin developers (required reading) |
| [cli.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/cli.md) | **`vela-plugin` manual**: development inner loop (`dev init`), health check (`doctor`), validate/pack/sign, host launch arguments | Plugin developers |
| [publishing.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/publishing.md) | **Packaging and publishing**: release build, `.vpx`, signing and trust, submitting to the [marketplace](http://market.easilynet.top), CI packaging | Plugin developers |
| [sdk-reference.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/sdk-reference.md) | **SDK reference**: packages, entry contract, capability surface, SDK version history, test doubles, loading model | Plugin developers |
| [STATUS.md](STATUS.md) | **Progress overview (single source of truth)**: completion by area, acceptance evidence, deliberate decisions, next-step recommendations | Everyone |
| [01-vision-and-goals.md](01-vision-and-goals.md) | Vision, goals/non-goals, typical scenarios, comparison with systems such as VS Code | Everyone |
| [02-architecture.md](02-architecture.md) | Overall architecture, process model, component divisions, project-directory plan, key decision records | Everyone |
| [03-plugin-model.md](03-plugin-model.md) | Plugin package format (.vpx), manifest specification, activation events, lifecycle, contribution points | Host developers, plugin developers |
| [04-plugin-host.md](04-plugin-host.md) | PluginHost process design, loading/unloading, health monitoring, crash recovery, resource controls | Host developers |
| [05-ipc-protocol.md](05-ipc-protocol.md) | Transport layer, JSON-RPC protocol, handshake and version negotiation, streaming and large-data channels | Host developers |
| [06-permission-system.md](06-permission-system.md) | Permission catalog, permission levels, authorization interaction, persistence and revocation, auditing | Everyone |
| [07-capability-apis.md](07-capability-apis.md) | Capability-domain APIs: remote files, local files, terminals, sessions, storage, networking, and more | Host developers, plugin developers |
| [08-ui-extensions.md](08-ui-extensions.md) | UI contribution points, VelaUI remote interface tree, dedicated image/audio surfaces, themes and i18n | Host developers, plugin developers |
| [09-sdk-and-tooling.md](09-sdk-and-tooling.md) | SDK NuGet package, project templates, `vela-plugin` CLI, debugging experience, example plugins | Plugin developers |
| [10-packaging-and-distribution.md](10-packaging-and-distribution.md) | Packaging, signing, installation/update flow, plugin-source (Registry) design | Host developers |
| [11-automation-and-ai.md](11-automation-and-ai.md) | Automation trigger/action model, AI capability gateway design | Host developers, plugin developers |
| [12-security-threat-model.md](12-security-threat-model.md) | Trust model, threat analysis, attack surface and mitigations, OS-level sandbox roadmap | Everyone (required reading) |
| [13-testing-strategy.md](13-testing-strategy.md) | Contract testing, host testing, plugin test tools, chaos testing | Host developers |
| [14-roadmap.md](14-roadmap.md) | Overall development plan: milestones, task breakdown, dependencies, acceptance criteria | Everyone |
| [15-ecosystem-ideas.md](15-ecosystem-ideas.md) | **Proposals**: plugin idea catalog, evaluation of new extension points (VFS/OSC 133/session groups, etc.), v1 simplifications and enhancements | Everyone |

Each component document ends with its own "Development Plan" section;
[14-roadmap.md](14-roadmap.md) consolidates all component plans and lays out the milestone order.

## Glossary

| Term | Meaning |
| --- | --- |
| **Host** | The VelaShell main process, which owns all UI, SSH connections, and user data |
| **PluginHost** | An independent executable distributed with the main application; by default, each plugin runs in its own PluginHost process |
| **Plugin** | A first-party or third-party extension developed with the official SDK and distributed as a .vpx package |
| **Manifest** | `plugin.json` inside the plugin package, declaring identity, entry point, activation events, contribution points, and permissions |
| **Contribution** | A declarative UI/behavior extension slot that a plugin registers with the host: commands, menus, sidebar views, document pages, status bar, settings pages, and more |
| **Activation Event** | A condition that moves a plugin from "installed" to "activated" (starts the process and invokes its entry point) |
| **Capability** | A service domain exposed by the host to plugins over RPC, such as `vela.remoteFs` and `vela.terminal` |
| **Permission** | An authorization item required to use a capability; permissions are divided into ordinary and dangerous permissions, with dangerous permissions requiring explicit user consent |
| **Broker** | The permission proxy inside the main process: the mandatory checkpoint for all capability calls |
| **VelaUI** | A declarative remote-interface protocol for plugins: the plugin describes a control tree, the host renders it, and events are sent back |
| **.vpx** | VelaShell Plugin Package, a ZIP container containing the manifest, assemblies, resources, and signature |
| **apiLevel** | The integer version of the plugin API; the host promises backward compatibility within the same apiLevel |

## Recommended Reading Order

- **Writing a plugin**: [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md) (the sole authority for the current implementation), with [cli.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/cli.md) and [sdk-reference.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/sdk-reference.md) at hand; read [publishing.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/publishing.md) when you are ready to ship.
- **Checking progress**: [STATUS.md](STATUS.md).
- **Studying the long-term design**: 01 → 02 → 12 (trust model) → 03 → 06 → 07, and the rest as needed;
  note that the "Implementation Notes" at the top of each document identify deviations between the current implementation and the blueprint.
