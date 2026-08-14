# Terminal Input Ordering ("Character Jumping") Problem Analysis and Architecture Recommendations

> 2026-07: Users reported that letters "jump around" while typing, and that entering `docker status` caused the characters to be split apart after appearing on screen.
> This document records the root cause, fix, comparison with other open-source terminals, and an architecture assessment for AI integration.

## 1. Symptoms and Root Cause

**Symptoms**: When typing quickly, especially in SSH sessions with network latency, echoed characters appear out of order and split apart,
looking as if the "cursor is jumping around". The issue is easier to reproduce when the autocomplete popup is visible, so the suggestion feature was initially suspected.

**Root cause**: Outbound writes were not serialized, while the underlying channel does not allow concurrent writes.

- `SshTerminalBridge.OnUserInput` called `_ = WriteUserInputAsync(data)` for **every keystroke**,
  which was fire-and-forget, with no queue and no lock.
- Tmds.Ssh's `SshChannel.WriteAsync` (through `RemoteProcess.WriteAsync`) had **no concurrency protection**
  (verified against upstream source): when two writes run concurrently, each reads the send window and packetizes independently, so bytes enter the channel in an arbitrary interleaving order.
- Sequence: key A's `WriteAsync` suspends at an await because the send window tightens or network latency occurs → key B's
  `WriteAsync` starts synchronously on the UI thread and may finish first → the remote side receives `B A` → the remote shell echoes in receive order
  → characters appear out of order on screen.
- The same applies to the local ConPTY terminal: concurrent `FileStream.WriteAsync` + `FlushAsync` calls can interleave.

**Why it did not "appear only recently"**: The old SSH.NET `ShellStream` used an internal lock, masking this race condition.
After migration to Tmds.Ssh, the latent issue surfaced. The probability of triggering it is positively correlated with typing speed and network RTT, so it appears intermittently.

**Why autocomplete looked guilty**: The suggestion feature itself (`TerminalInputTracker` side-channel tracking and the popup UI) never writes to the PTY,
so it is not the root cause. However, accepting a suggestion writes a larger payload in one operation (backspaces + completed text), and the window for concurrency with subsequent keystroke writes is larger.
It amplifies the visibility of the disorder, so it is always present at the scene of the incident, but is only an accomplice under suspicion.

## 2. Fix (Implemented)

`SshTerminalBridge` now has a **single-writer outbound queue**:

- All bytes sent to the PTY (keystrokes through `OnUserInput` + programmatic injection through `SendRaw`) only call
  `Channel<byte[]>.Writer.TryWrite` to enqueue them (both are triggered on the UI thread, and enqueueing preserves order immediately).
- The sole `WriteLoopAsync` writes each segment in FIFO order with `await WriteAsync`; the next segment never starts until the current one completes.
- Segments accumulated while the previous one is suspended are merged into a single write (semantically equivalent, with fewer SSH packets).
- On `Dispose`, the queue is completed, and the write loop drains it before exiting on its own.

Regression test: `TerminalBridgeTests.UserInput_RapidKeystrokes_NeverWriteConcurrently_AndPreserveByteOrder`
simulates a 30 ms write suspension while rapidly typing "docker status", asserting that at most one write is in flight at any time and that the byte order is complete.
The test consistently failed on the old implementation (13 concurrent writes observed in practice) and passes with the new implementation.

## 3. Comparison with Other Open-Source Terminals

All major terminals follow the same iron rule: **there is only one writer for PTY outbound writes, and input is always flushed serially through a queue**.

| Terminal | Approach |
| --- | --- |
| Windows Terminal | Keystrokes uniformly go through `TerminalInput` → the connection object's single `WriteInput`; the ConPTY write handle is touched by only one pump |
| Alacritty | Keystroke events are sent to the PTY event loop thread's write buffer, and that thread flushes them in order when writable (`polling` single-threaded) |
| iTerm2 | One write queue per session (`writeTask`), consumed serially in the background |
| xterm.js / VS Code | `Terminal.write`/`onData` use a single channel, and the host-side pty service strictly serializes writes to stdin |

Our read side has actually already followed this shape (a single read loop + UI batching pump). This change only completes the write side with a symmetrical structure.

## 4. Architecture Assessment: Is a Refactor Needed?

**The current layering is better than the feeling of "mixing" suggests**, so a large-scale rewrite is not recommended:

- `VelaShell.Terminal` does not depend on any SSH library (through the `IShellStreamWrapper` abstraction); SSH/ConPTY/
  future serial and Telnet share the same bridge.
- The VT engine (`TerminalEmulator`/`VtParser`) is pure logic and can be unit-tested.
- ZMODEM is decoupled from transport (`IByteDuplex`).
- Input tracking (`TerminalInputTracker`) is a side-channel observer and does not interfere with the data flow.

The feeling of "mixing" mainly comes from two places, which should be addressed **incrementally**:

1. `VelaTerminalControl` is about 2,500 lines, with rendering, keyboard/IME, selection, search, folding, and scrolling all in one class.
   It can later be split by component (input encoding dispatch, selection/search, and gutter/folding as separate classes, with the control responsible only for composition and drawing).
   Move one piece at a time, make it testable, and then move to the next.
2. Session-level "data plane" responsibilities are scattered through `TerminalTabViewModel` (assembly of the bridge, tracker, suggestions, and ZModem routing).
   They should be gathered into a session facade (see below).

## 5. Integration Points for AI Operations

AI needs three things, and the existing architecture already provides about 90% of each:

| AI capability | Existing mechanism | Gap |
| --- | --- | --- |
| Read terminal output stream | `SshTerminalBridge.DataReceived` (raw bytes, read thread) | Need a decoded text stream / ring buffer |
| Read current screen / scrollback | `TerminalScreen` + `ScrollbackBuffer` | Need a thread-safe text snapshot API |
| Inject input into the terminal | `SendRaw` (does not disturb suggestion tracking) / `WriteInput` (equivalent to a keystroke) | None. After this fix, injected input is naturally ordered |
| Understand user commands | `TerminalInputTracker` (command submission event) | None |

Introducing an `ITerminalSessionAutomation` facade is recommended (attached to the session VM, with internal forwarding to the mechanisms above):
`GetScreenText()` / `GetScrollback(n)` / `IObservable<string> OutputText` / `SendText(string)` /
`SendControl(byte)`. The AI layer should depend only on this interface and should not touch control or bridge internals.
Audit and confirmation policies, such as which commands require user approval, should also be consolidated in this facade.

## 6. Known Edge Cases (Recorded, Not Being Addressed Yet)

- During a ZMODEM session, user keystrokes still enter the queue and are written to the stream, so in theory they can be mixed into protocol frames (behavior is unchanged before and after the fix; only the cancel sequence has meaning).
  Later, non-cancel input can be discarded inside the bridge while the session is active.
- `EchoSuppressor` only applies to connection-initialization injection and is unrelated to this issue.
- Local echo (`LocalEcho`) is forcibly disabled by `PeerEchoesInput` under SSH/ConPTY and is not involved in this incident.
