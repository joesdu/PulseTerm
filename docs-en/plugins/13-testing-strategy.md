# 13 · Testing Strategy

The plugin system crosses process boundaries, so testing is divided into four
layers. Follow the existing testing discipline: UI test classes must not create
an App, call StartNew, or dispose sessions themselves (a lesson from
velashell-tests); all new plugin-system tests must use the existing test
infrastructure.

## 1. Layers

### L1 Pure Logic Unit Tests (Largest Share, No Processes/No I/O)

- Every lifecycle state-machine migration path (M-3)
- 20+ invalid cases for the manifest validator (M-1)
- `when` expression evaluator (M-4)
- Broker permission matrix: each dangerous permission × seven authorization states (B-2)
- VelaUI differ: tree transformation → minimal patch (U-7)
- Scope matching (path-normalization boundaries: `..`, trailing slash, case)

### L2 Protocol Contract Tests (In-Process Loopback on Both Ends, No Real Processes)

- Replace Pipe/UDS with an in-memory duplex stream, directly connecting the host
  endpoint and SDK proxy
- For every capability domain: the four quadrants of normal, permission denied,
  cancellation, and mid-connection loss (C-8)
- DTO binary compatibility: MessagePack integer-key snapshots +
  PublicApiAnalyzer, with CI failing on breaking changes within an apiLevel (P-3)
- Protocol fuzzing: out-of-order, oversized, and invalid payloads, plus calls
  before the handshake (P-7)

### L3 Real-Process Integration Tests

- A fixed set of **test plugins** (in the repository and built in CI):
  `wellbehaved` (all features work normally) / `crasher` (crashes on activation) /
  `hanger` (blocks a thread and does not respond to ping) / `hog` (consumes
  memory) / `chatty` (RPC storm) / `malformed` (invalid protocol bytes)
- Scenarios: startup/handshake/activation-time baselines; host state-machine
  transitions after killing a process; heartbeat-loss handling; backoff restart
  to Faulted; forced termination after the shutdown timeout; orphan self-kill
  (kill the host and assert that the plugin process disappears within 2s)
- Chaos acceptance (H-8): while a faulty plugin is running, the host UI thread
  remains unblocked (frame probe), and RPC latency for a second plugin on the
  same machine does not degrade
- Run the three-platform matrix (Windows/macOS/Linux, exposing differences in
  Pipe/UDS, Job Object, and process semantics at this layer)

### L4 End-to-End (Official Samples Are Tests)

- Smoke scripts for the three official sample plugins: install → authorize
  (inject pre-authorization in test mode, without showing UI) → operate the core
  scenario → uninstall and assert cleanup
- Connect remote capabilities to the SSH test container in the existing
  docker-compose.test.yml
- Four distribution scenarios: upgrade/rollback/signature tampering/revocation (D-2/D-3)

## 2. Special Areas

- **Performance baseline** (connected to 05/08 acceptance): empty-call latency,
  stream throughput, IPC traffic for scrolling a 1,000-row VelaUI list, and
  activation P95; put the values into CI trend alerts (follow perf-pass baseline
  discipline: multiple samples and removal of startup drift).
- **Leaks**: after 100 activation/deactivation cycles, host memory returns to
  baseline ±10%; after 50 ALC hot reloads (dev mode), PluginHost memory remains
  bounded.
- **Five languages**: CI checks for missing NLS entries (already included in
  validate); authorization-dialog and management-page text follows the existing
  five-language parity process.
- **Security**: archive the red-team script set (X-1) by threat-list number so
  regressions can be replayed.

## 3. CI Orchestration

| Trigger | Scope |
| --- | --- |
| Every PR | L1 + L2 (minute-level) |
| Every PR ( `plugins` label) / nightly | L3 on three platforms + L4 |
| Nightly | Performance baseline + leaks |
| Before release | Full suite + red-team regression + manual acceptance checklist |

## 4. Development Plan (This Area)

| Task | Description | Dependencies | Estimate |
| --- | --- | --- | --- |
| Q-1 | Six test plugins + build integration | H-1 | 2d |
| Q-2 | L2 loopback test infrastructure (in-memory duplex, pre-authorization injection, four-quadrant capability templates) | P-3, B-2 | 3d |
| Q-3 | L3 process-test infrastructure (process-reaping safeguards, three-platform CI matrix) | Q-1 | 3d |
| Q-4 | Frame probe and performance-baseline collection scripts | Q-3 | 2d |
| Q-5 | L4 smoke-test framework + four distribution scenarios | D-2 | 3d |

Acceptance: a fully green CI is a hard condition for releasing each milestone;
every "fixed plugin-system bug" must include a regression test in one of L1–L4.
