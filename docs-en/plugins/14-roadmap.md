# 14 · Overall Development Roadmap

Task ID index: A (architecture 02) / M (plugin model 03) / H (PluginHost 04) /
P (protocol 05) / B (permissions 06) / C (capabilities 07) / U (UI 08) / S (SDK 09) /
D (distribution 10) / T (automation 11) / I (AI 11) / X (security 12) / Q (testing 13).
Estimates assume 1 full-time + 1 part-time developer; week counts are calendar weeks and include buffer.

## Milestone Overview

```text
M0 Specification Freeze & Spike ──▶ M1 Core Runtime ──▶ M2 Permission System ──▶ M3 Capabilities & Contribution Points
             (2w)                         (4w)                  (3w)                         (4w)
                                                                                               │
               ┌───────────────────────────────────────────────────────────────────────────────┤
               ▼                                                                               ▼
          M4 UI Extensions (4w)                                                   (parallel after M4)
               │
               ▼
          M5 SDK Toolchain (3w)──▶ M6 Packaging & Distribution (3w)──▶ M7 Automation & Audio (4w)──▶ v1 Release
                                                                                               │
                                                                         M8 AI Gateway + Sandbox Research (later release, 4w+)
```

Total to the v1 release is approximately **27 calendar weeks (≈6.5 months)**; with two full-time developers it can be compressed to
~4.5 months (M4/M5 and M6/M7 have room for internal parallelization).

## M0 · Specification Freeze & Spike (2 Weeks)

| Task | Content |
| --- | --- |
| A-1..A-4 | Three-project skeleton, cross-platform process + RPC + ALC spike, domain model |
| X-1 (first half) | Security review of design documents (finalize review of 01–13 in this directory) |

**Gate**: The spike passes on all three platforms (pipeline/UDS connectivity, ALC unload/reload round trip, startup-time baseline within budget); the security review has no unresolved high-severity items. **If the spike fails, return to 02 and revise D2/D3; do not enter M1.**

## M1 · Core Runtime (4 Weeks)

| Task | Content |
| --- | --- |
| P-1..P-4 | Transport, handshake, initial endpoints, error model |
| H-1..H-4, H-6 | Executable PluginHost, ALC loading, process management, Supervisor, logging |
| M-1, M-3 | Manifest validation, lifecycle state machine |
| Q-1..Q-3 | Six-piece test plugin suite, L2/L3 testing infrastructure |

**Demo**: From the command line, load a hand-written plugin → activate → crash → automatically restart → complete the Faulted end-to-end flow; killing the host leaves no orphan processes.

## M2 · Permission System (3 Weeks)

| Task | Content |
| --- | --- |
| B-1..B-7 | Finalize permission model, Broker, persistence, authorization dialog, settings page, audit, analyzer |

**Demo**: A test plugin requests `remote.files.read` → dialog appears → behavior is correct for every choice; revocation on the settings page takes effect immediately; the permission-matrix tests are all green.
(Note: The B-1 permission-model review is a prerequisite for all API signatures in 07 and is placed in the first week of this milestone.)

## M3 · First Batch of Capabilities & Contribution Points (4 Weeks)

| Task | Content |
| --- | --- |
| C-1..C-4 | Capability foundation + storage/settings/secrets/events + sessions/remoteFs + localFs/clipboard/notifications |
| M-2, M-4, M-6 | Activation-event routing, contribution-point registry + `when` evaluation, NLS pipeline |
| U-1, U-2 | Placeholder wiring for commands/menus/status bar/sidebar |

**Demo**: A plugin is activated through a context menu, reads a remote file, and sends a notification; permissions and auditing are visible throughout.

## M4 · UI Extensions (4 Weeks)

| Task | Content |
| --- | --- |
| U-3..U-10 | Document pages in VelaDock, generated settings pages, end-to-end VelaUI, image surface, themes/i18n |
| P-5, P-6 | Streaming channel, shared-memory channel |
| S-7 | **Official example: image viewer (S1 acceptance scenario)** |

**Demo**: S1 end to end—double-click a remote image in SFTP, open a preview in the document area, and zoom/pan smoothly; kill the plugin process and the preview page is grayed out and can restart.

## M5 · SDK & Toolchain (3 Weeks)

| Task | Content |
| --- | --- |
| S-1..S-6 | SDK package, MSBuild targets, CLI (`validate`/`pack`/`install`), templates, test doubles, documentation |
| H-7 | Dev-mode hot reload + wait-debugger |
| M-5 | Install/uninstall/upgrade transactions |
| X-1 (second half) | First round of red-team exercises |

**Gate**: A new developer can get started within 30 minutes; the public `apiLevel 1` surface is frozen (all subsequent changes follow compatibility discipline).

## M6 · Packaging & Distribution (3 Weeks)

| Task | Content |
| --- | --- |
| D-1..D-6 | Signing, installation pipeline, source client, management pages (installed/browse), establish the official source |
| Q-5 | Distribution-scenario testing |

**Demo**: Complete browse → install → authorize → upgrade → roll back → uninstall flow; three attack cases are rejected.

## M7 · Automation & Audio (4 Weeks) → v1 Release

| Task | Content |
| --- | --- |
| T-1..T-5 | Rule engine, management page, plugin contribution, confirmation flow; auto-runner example (S5) |
| C-5, C-6, C-7 | terminal/remoteExec domains, audio output, net wrapper |
| S-8, S-9 | **Official examples: MP3 player (S2), auto-runner (S5)** |
| H-5, H-8, Q-4 | Resource monitoring, chaos acceptance, performance baseline |
| X-2 | User security documentation |

**v1 release gate**: All success metrics in 01 §6 meet their targets; the four chaos-test suites are green in CI; all three examples use only the public SDK; every item in the security checklist (12 §4) is checked off.

## M8 · AI Gateway & Sandbox Research (After v1)

| Task | Content |
| --- | --- |
| I-1..I-4 | AI gateway, `vela.ai`, outbound-data guardrails, AI assistant example (S3) |
| X-3 | Linux Landlock sandbox PoC |
| — | Prioritize based on community feedback: more contribution points, editor extension points (S7), evaluation of non-.NET SDKs, official marketplace (Phase B) |

## Risk Register

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Type-identity problems from shared assemblies in ALC (multiple versions of StreamJsonRpc loaded together) | M1 blocked | Cover specifically in the M0 spike; finalize the shared allowlist mechanism early |
| Per-plugin process memory overhead causes user complaints | Reputation | Lazy activation + idle reclamation (03 §7); show usage transparently in the management page |
| VelaUI lacks expressive power, causing developers to complain that they "can't build the UI they want" | Ecosystem | Validate with the three examples first; expand the allowlisted controls incrementally based on demand (additive changes within `apiLevel`) |
| Audio backend choice causes cross-platform output problems | M7 delayed | Spike C-6 first; in the worst case launch the audio domain on Windows only; capability negotiation naturally supports platform-specific declarations |
| Permission dialogs feel disruptive, causing user backlash | Reputation | Narrow the scope + guide plugins toward low-permission selector paths (07 §5); show details before requesting |
| A single developer's bandwidth is insufficient, leaving too many workstreams open | Overall | Use milestone gates; the end of every milestone is a stable pause point; beginning with M4, examples serve as acceptance tests to avoid last-minute integration |

## Execution Discipline

- At the end of each milestone: update the corresponding documents in this directory (design is documentation; drift is technical debt); do not start the next milestone until the gate passes.
- All new public surfaces (permission IDs, contribution points, RPC methods) must first be added to the 03/06/07 documents before code is written.
- Relationship to mainline development: the plugin system progresses on the long-lived `feature/plugins` feature branch and is merged back into `dev` by milestone (host-side changes affect the main application starting at M1 and must pass the full existing test suite).
