# 15 · Ecosystem Ideas: Plugin Catalog, Simplifications & Enhancements

> Status: **Proposal** (assessment output dated 2026-07-24). Once any item in this document is adopted, update the corresponding document among 01–14 and mark it here; unadopted items remain here as decision records.

## 1. High-Value Plugin Ideas (Ordered by Positioning)

Evaluation dimensions: value (fit with the operations-workbench positioning) / complexity / required capabilities.
**Bold** items require new extension points (see §2); the rest can be implemented with the capabilities already available in apiLevel 1.

### Tier 1 (Directly Addressing Everyday Operations Pain Points)

| Plugin | Description | Required capabilities | Complexity |
| --- | --- | --- | --- |
| Database client | Connect to MySQL/PostgreSQL/Redis through an SSH tunnel, with VelaUI table browsing + SQL editing; a frequent operations scenario: "quickly check the database" | sessions.read, network (tunnel provided by the host's existing tunnel feature), VelaUI | High |
| Docker/Compose panel | List containers, view logs, start/stop, and enter via exec; parse output from the remote docker CLI | remote.exec, VelaUI, terminal output channel | Medium |
| systemd service manager | List services, show status, start/stop, enable at boot, and show the tail of the journal | remote.exec, VelaUI | Low |
| **Command snippet library / Runbook** | Library of common commands with variable placeholders; a Runbook is a documented operating procedure executed step by step into the terminal with one click | terminal.write, storage; **recommended as a contribution point** (§2.5) | Low |
| Multi-host log aggregation | Run `tail -f` on multiple servers, merge the streams with highlighted host prefixes, and aggregate error patterns | remote.exec (streaming), terminal output channel | Medium |
| Process manager | htop-style panel (parse `ps`), with sorting/search/kill | remote.exec, VelaUI | Low |
| Disk usage analyzer | Render `du` output as an ncdu-style tree/rectangle chart, with drill-down + delete | remote.exec, remote.files.write, VelaUI | Medium |
| Certificate inspection | Check TLS certificate expiration dates on each host and notify for certificates nearing expiry; naturally suited to an automated cron | remote.exec, notifications, automation | Low |
| crontab editor | Visually edit the remote crontab, explain expressions, and preview the next run | remote.exec, VelaUI | Low |

### Tier 2 (Increasing Stickiness / Team Scenarios)

| Plugin | Description | Required capabilities | Complexity |
| --- | --- | --- | --- |
| Session recording and playback | Record the `terminal.read` stream in an asciinema-style format, with local playback/export; useful for both auditing and training | terminal.read, fs.local.write, image/terminal surface | Medium |
| IM notification integrations | DingTalk/Feishu/Slack/Telegram webhooks as automation actions ("deployment complete → send a Feishu message") | network, automation action contribution | Low |
| Network toolkit | ping/mtr/traceroute/port probing/DNS lookup, initiated locally or through a remote host | remote.exec, VelaUI | Low |
| File synchronization/diff | Diff view for a local directory vs. a remote directory, with selective synchronization (rsync semantics) | remote.files.*, fs.local.*, VelaUI | High |
| Nginx configuration assistant | Site list, configuration validation (`nginx -t`), reload, and access-log summary | remote.exec, remote.files.* | Medium |
| K8s panel | kubectl wrapper: pod list/logs/exec/describe | remote.exec, VelaUI | High |
| man/tldr lookup | Select a command in the terminal → show a tldr/man summary in the sidebar | terminal.read (selection), network | Low |
| **Theme/color-scheme package** | Import community color schemes such as iTerm2; the easiest contribution format for attracting the community during the cold-start period | **Requires a theme contribution point** (§2.5) | Low |

### Already Covered by the Design (For Comparison)

Image viewer (S1), MP3 player (S2), AI assistant (S3), log highlighting (S4), automation (S5), and server dashboard (S6)—this catalog extends them and does not list them again.

## 2. Recommended New Extension Points (Ordered by Return on Investment)

### 2.1 Virtual File System Provider (VFS Provider) ★ Strongly Recommended as a Flagship apiLevel 2 Feature

A plugin registers a "remote file-system type" (S3/OSS/WebDAV/FTP/SMB/k8s Pod), and the host presents it in the **existing file panel**: dual-pane browsing, transfer queue, resumable transfers, and context menus are all reused.

```jsonc
"contributes": { "fsProviders": [ { "scheme": "s3", "displayName": "Amazon S3",
                                    "connectionForm": { ...VelaUI form schema... } } ] }
```

- Host → plugin reverse RPC: `fs/list`, `fs/stat`, `fs/read`, `fs/write`, etc. (the mirror of 07 §2 `IRemoteFs`, with the direction reversed);
- Why it is worthwhile: one extension point unlocks an entire "connect to every storage system" ecosystem and provides an openness that competitors (WinSCP/Termius) cannot offer;
- Prerequisite: the file panel must abstract a "file source" interface (it is currently bound to SFTP)—a host-side refactor. Reserve the interface when bridging remoteFs in M3; this is the lowest-cost time to do so.

### 2.2 Shell Integration Events (OSC 133) ★ Recommended as an Early Host-Side Feature; Free Fuel for the Plugin System

The terminal subsystem recognizes OSC 133 command-boundary sequences (an integration script must be injected into the remote shell, with one-click installation provided), and adds the following to the plugin event system:

```text
CommandStarted { sessionId, commandLine }        // requires terminal.read
CommandFinished { sessionId, exitCode, duration }
CwdChanged { sessionId, cwd }
```

Value: automation advances from "blind scheduling" to "trigger when a command fails"; the AI assistant receives structured information about "what was just executed and what was the exit code" instead of guessing from a byte stream; audit granularity also improves. This feature is valuable to the host terminal itself (command navigation/re-run), so it should be established as a terminal-subsystem feature, with the plugin system merely consuming its events.

### 2.3 Multi-Host Broadcast Execution (Session Groups)

The multi-target form of `remote.exec` + a separate `remote.exec.multi` permission:

```csharp
Task<MultiExecResult> RunOnGroupAsync(string[] sessionIds, string command, ExecOptions o, CancellationToken ct);
```

- Mandatory UX: before execution, the host displays a confirmation checklist ("The command will run on 12 hosts: …", with per-host checkboxes);
- Aggregate results (group successes/failures; the output comparison view is delegated to the plugin UI);
- Audit each host separately. This is an essential operations capability with high security sensitivity, so it deserves first-class support rather than allowing plugins to implement their own loop (bypassing the "confirm N hosts" UX).

### 2.4 Credential Provider — Proceed Cautiously; Evaluate After M8

Password managers (1Password/Bitwarden/KeePass) **provide** credentials when the host connects: the direction is plugin → host, so it does not violate the red line that "host credentials must never leave the host"; however, credentials pass through the PluginHost process and RPC channel, expanding the trust boundary. If implemented: use a dedicated `credentials.provide` permission (highest sensitivity), use provided credentials only during a single connection handshake, do not persist them in the host credential store, and require the provider plugin to have a trust level of "verified publisher" or higher. **Given that password-manager official CLIs are already highly usable, this can be deferred.**

### 2.5 Two Low-Cost Contribution Points (Recommended for Direct Inclusion in apiLevel 1)

- `contributes.snippets`: command snippets (name/command template/variables/applicable shell), surfaced in the command palette and terminal context menu; declarative only, rendered by the host, estimated at 2–3 days;
- `contributes.themes`: terminal color schemes (declarative color table, reusing the existing theme-switching mechanism), estimated at 2–3 days. Neither requires launching a plugin process, creating a "zero-code plugin" format and greatly lowering the barrier to a community's first contribution.

## 3. Recommended Simplifications (v1 Subtractions; Save Approximately 3–4 Weeks Total)

| # | Simplification | Original design | Recommendation | Rationale / when to restore |
| --- | --- | --- | --- | --- |
| C1 | Implement signing in two stages | D-1/D-6 complete "publisher verification + revocation list" | v1: official signatures + self-signing (strong yellow warning at installation); defer verification/revocation until third-party authors exist | During ecosystem cold start there are only official plugins, so there is nothing to revoke; do not simplify the **signature format** (retain revocation fields), only defer the operational side |
| C2 | Narrow the permission-scope UI | B-4 includes a "scope narrowing editor" (change path prefix/switch session) | v1 authorization dialog provides only five whole-permission decisions + session-level scope; defer the path-prefix editor to v1.1 | Retain the scope data structure and Broker matching logic **unchanged** (do not cut B-2); only defer the authorization UX |
| C3 | Reduce the VelaUI control allowlist | Full set in 08 §2.2 | Remove `TabControl`, `DatePicker`, `TreeView`, and `Sparkline` (defer the S6 dashboard scenario) | None of the three official examples needs them; additive controls can be added within apiLevel at any time |
| C4 | Defer idle reclamation | 03 §7 `idlePolicy` | Do not implement in v1; keep processes resident until deactivation | Purely a memory optimization that does not affect correctness; transparent usage display in the management page is sufficient initially |
| C5 | Move the `onSchedule` activation event into M7 | Listed among the first batch in 03 §4 | Deliver it alongside the automation engine (T-1) | Without a rule engine, there is no consumer scenario for cron activation |
| C6 | Turn the audio-domain fallback plan into a decision | Three-candidate spike in 07 §9 | Adopt "Windows (WASAPI) first, mac/Linux with v1.x" directly; halve the spike scope | Capability negotiation naturally supports platform-specific declarations; the MP3 example can still ship |

**Not recommended for simplification** (considered previously and explicitly rejected): one process per plugin (isolation is the reason this design exists), the audit pipeline (the foundation of trust for an operations tool), five-language NLS (would violate established repository discipline), and VelaUI as a whole (otherwise S2/S6-class plugins have no path forward, and retrofitting the protocol later would cost far more than shipping it initially).

## 4. Recommended Enhancements

| # | Enhancement | Location |
| --- | --- | --- |
| E1 | Export audit logs (JSON/CSV) + retention-period setting—for compliance/retrospective scenarios, an operations tool should provide this | 06 §4, +1d |
| E2 | Export/import the plugin inventory with configuration (list of installed plugin IDs + each plugin's settings) for a consistent multi-machine synchronization experience | 10 management page, +2d |
| E3 | Permission "trial" mode: during the first 24 hours after installing a new plugin, notify on every dangerous permission even if it is set to "always allow" (can be disabled)—a safety net for users who "install first and ask questions later" | 06 §2, +1d |
| E4 | Group the plugin store "by scenario" (containers/database/monitoring/transfer…), aligned with the categories in §1 | 10 §4, Phase B |
| E5 | `contributes.snippets` / `contributes.themes` (see §2.5) | 03 §6 + U series, +5d |

## 5. Adoption Tracking

| Item | Decision | Update location | Status |
| --- | --- | --- | --- |
| §2.1 VFS Provider | TBD | 07 (reverse RPC), 14 (M3 reserved interface + apiLevel 2 planning) | Proposal |
| §2.2 OSC 133 | TBD | 07 §8 events, independent terminal-subsystem initiative | Proposal |
| §2.3 Session-group broadcast | TBD | 06 permission table, 07 §3 | Proposal |
| §2.4 Credential Provider | TBD (inclined to defer) | Preliminary analysis in the 12 threat model | Proposal |
| §2.5 snippets/themes contribution points | TBD | 03 §6, 08 §1, 14 (M3/M4) | Proposal |
| §3 C1–C6 simplifications | TBD | Each corresponding document and the 14 milestones | Proposal |
| §4 E1–E5 enhancements | TBD | Each corresponding document | Proposal |
