# Telnet and Serial Connections: Feasibility Research

> Date: 2026-07-22　　Baseline code: `tmds-ssh` branch
>
> Purpose: Provide **factual basis and an implementation checklist** for subsequently implementing Telnet and serial session types.
> All code conclusions are supported by `file:line` evidence. Parts verified online include the version and license. Anything not verified is marked "unverified".

---

## 1. Conclusion First

**The good news: this project's terminal stack is already prepared for this, and not by coincidence. The author reserved the extension points long ago.**

The comment in `src/VelaShell.Core/ZModem/Abstractions/IByteDuplex.cs` reads:

> Decoupled from specific transports (SSH Shell, local ConPTY, **future serial / Telnet**) ...

Meanwhile, in the protocol tabs at `ConnectionProfileView.axaml:173-197`, **the entry points for Telnet and serial are already reserved** (currently `IsEnabled="False"`), and the localization key `Profile_Serial` **already exists in all five resx files**.

**Key judgment: the `IShellStreamWrapper` abstraction is general enough; its signature does not need to change.**

It has only "byte duplex + one size notification", **with no SSH types, no pty parameters, and no encoding conversion**.
SSH-specific pty parameters are all on the factory method `ISshClientWrapper.CreateShellStreamAsync`, not on the stream interface.
The strongest evidence is that **the local terminal (ConPTY) already follows this path successfully**: the entire flow in `MainWindowViewModel.cs:663-772` has zero SSH dependencies.

**The real workload is not in the transport layer, but in "protocol generalization"**. Across the repository, a number of places implicitly assume that "non-local terminal = SSH".

---

## 2. Directly Reusable (No Changes)

| Component | Location |
|---|---|
| `IShellStreamWrapper` contract itself | `Core/Ssh/IShellStreamWrapper.cs` |
| Terminal bridge (read-loop batching, EOF→Closed, echo suppression) | `Terminal/SshTerminalBridge.cs` |
| Complete VT engine and custom-rendered controls | `Terminal/Emulation/*`, `Rendering/VelaTerminalControl.cs` |
| **Complete ZModem pipeline** | `Core/ZModem/*`, `Terminal/ZModem/*` |
| Tab lifecycle (Attach/Detach/reconnection state machine/disconnect overlay) | `ViewModels/TerminalTabViewModel.cs` |
| Session logging and session recording | Hooked to `Bridge.DataReceived` |
| Terminal search, buffer export, synchronized input, command completion | All based on the emulator and unrelated to transport |
| SonnetDB persistence pipeline | `Infrastructure/Persistence/*` |

> **ZModem comes for free**: as long as the new transport implements `IShellStreamWrapper` and is mounted through `AttachTransport`, ZModem works automatically.
> However, there are two prerequisites. Failing to meet them will **silently corrupt the transport**:
> - **Telnet**: 0xFF must be escaped and restored through IAC doubling, and **the entire output stream must never undergo CRLF rewriting**
> - **Serial**: it must use 8 data bits and **no XON/XOFF software flow control** (software flow control consumes 0x11/0x13)

---

## 3. Areas That Must Be Changed (Protocol Generalization)

### 3.1 Four Hard-Coded Enum Clamps (**Not Changing Them Silently Drops Data**)

`ConnectionType` currently has only `SSH=0` / `SFTP=1`, and four ternary clamps rewrite unknown values to SSH:

| Location | Code |
|---|---|
| `Core/Models/SessionProfile.cs:10` | `value == ConnectionType.SFTP ? SFTP : SSH` |
| `Core/Models/RecentConnectionEntry.cs:13` | Same as above |
| `Infrastructure/Persistence/SonnetDbRecentConnectionService.cs:127-130` | `ParseConnectionType` |
| `ViewModels/ConnectionProfileViewModel.cs:158-160` | Another clamp at the VM layer |

These four locations implement the "legacy data compatibility strategy" (unknown value → SSH).
**Change**: replace the ternaries with an `Enum.IsDefined` whitelist. The semantics stay the same, while the code becomes extensible.

> Compatibility tests `ModelSerializationTests.cs:86-105`, `:235-254` assert "missing key → SSH, value 99 → SSH".
> They will still pass after adding `Telnet=2/Serial=3` (99 remains unknown), but round-trip tests for each new type must be added.
>
> **Note the one-way downgrade risk**: an older version reading `connectionType: 2` will treat it as SSH and may overwrite it when saving.
> This is an inherent cost of the existing strategy, not a newly introduced problem.

### 3.2 Two Main Dispatch Points (Most Important)

- **`MainWindowViewModel.cs:2112-2116`** — At the beginning of `TryConnectProfileAsync`, only SFTP is checked; everything else goes through SSH. **This is where branches for new protocols must be inserted.**
- **`MainWindowViewModel.cs:1779-1791`** — Anything other than `LocalShell` follows the SSH workflow in `ReconnectTabAsync`.
  **Telnet/serial tabs carry a Profile and will enter the SSH reconnection path incorrectly**, so a branch must be added.

### 3.3 Validation Predicates Block Serial Completely

- `ConnectionProfileViewModel.cs:126-138` — `canExecute` requires `Host` and `Username` to be non-empty, and `Port` to be 1-65535
- `ConnectionWorkflowService.ValidateProfile:239-256` — imposes the same requirements

**None of these apply to serial. The Save/Connect buttons will remain disabled forever.** Validation predicates must dispatch based on `ConnectionType`.

### 3.4 Other SSH-Specific Features That Need Guards

| Location | Problem |
|---|---|
| `MainWindowViewModel.cs:1026-1029` | The tunnel panel already blocks SFTP; Telnet/Serial must be blocked in the same way |
| `MainWindowViewModel.cs:1117-1126` | Ping latency uses `Profile.Host`; serial has no Host |
| `MainWindowViewModel.cs:785-808` | `RebindFileBrowser` uses `LocalShell is not null` to determine "no SFTP". **The new types would display file contents from the previous SSH session** (this bug is documented in a comment) |
| `MainWindowViewModel.cs:1744-1751` | `ResourceMonitor` depends on the SSH `SessionId` |
| `MainWindowViewModel.cs:1656-1658` | The status bar hard-codes `$"SSH • {name}"` |
| `SessionTreeNodeViewModel.cs:22-31` | The `IsSshProfile`/`CanOpenSftp` criteria need to be extended |

### 3.5 Adding to the Configuration Model

`ConnectionInfo` consists of `required` + `init` **pure SSH transport parameters**. **Do not extend it**.
Telnet/serial should not go through `ConnectionWorkflowService` to perform the SSH handshake.

Consistent with the existing compatibility strategy, add two **nullable nested objects** to `SessionProfile` (missing means null, with zero impact on old data):

```csharp
public SerialSettings? Serial { get; set; }   // PortName/BaudRate/DataBits/StopBits/Parity/Handshake/DTR/RTS/newline normalization
public TelnetSettings? Telnet { get; set; }   // TerminalTypeOverride/PreferBinary/EnterMode(CRLF|CRNUL|CR)/LocalEcho
```

Reason: this follows the existing `JumpHostProfileId` approach; it avoids polluting the SSH path with flat fields and avoids semantic smuggling through `Host`/`Port` values such as `COM3`/`115200`.

**Cost**: `SessionProfile` is copied field by field throughout the repository (with no `with`/clone method). New fields must be synchronized in five places:
`SonnetDbSessionRepository.cs:131-151`, `ConnectionWorkflowService.cs:113-131`,
`SessionTreeViewModel.cs:340-355`, `ConnectionProfileViewModel.cs:521-542`, `MainWindowViewModel.cs:2333-2341`.

### 3.6 Missing Capability: Local Echo

**There is no local echo capability anywhere in the repository** (zero hits for `LocalEcho`). `VelaTerminalControl.WriteInput:379` only sends and does not echo.
Telnet line mode and "no-echo serial devices" both need it. This is the only place where a new capability must be added at the terminal layer.

---

## 4. Technology Selection

### 4.1 Serial

**Recommendation: `System.IO.Ports` 10.0.10 (MIT, 2026-07-14)**, consistent with the repository's licensing policy.
Cross-platform support for Windows/Linux/macOS has been confirmed (RID native packages provide complete coverage).

**Alternative**: `RJCP.SerialPortStream` 3.0.5 (**MS-PL**, explicitly targets net10.0, solid on Windows/Linux, weaker on macOS).
⚠️ MS-PL is weak copyleft. This repository has `LICENSE-COMMERCIAL.md`; **compliance must be confirmed before pursuing commercial licensing**.
`SerialPort.Net` has been unmaintained since 2021 and is not recommended.

#### Confirmed pitfalls (all are still-open dotnet/runtime issues)

| Problem | Issue | Impact |
|---|---|---|
| `DataReceived` is unreliable | #106631 | Drops bytes at 115200 baud. **Do not use the event. Wrap blocking `Read()` in `Task.Run()`** |
| `Close()` deadlocks | #20362 | `Close()` blocks forever when hardware flow control CTS is stuck |
| `Close()` race NRE | #44952 | |
| **`BaseStream.ReadAsync` does not respond to CancellationToken on Windows** | #30850 | Reported by Microsoft itself, Future milestone, never fixed. Responds on Unix, **platform asymmetry** |

> **A free benefit for this project**: `SshTerminalBridge.Dispose:50-61` already uses the design of
> "**dispose the stream first to wake the read, with the token only as a fallback**", naturally avoiding #30850.
> Also, `TerminalTabViewModel.DetachTransport:673` already uses `Task.Run(bridge.Dispose)`, avoiding the #20362 UI-thread deadlock.
> **However, any newly added synchronous Dispose path would trigger both issues again.**

#### Port enumeration (the approach differs across the three platforms)

- **Windows**: `SerialPort.GetPortNames()` reads the registry. Official documentation explicitly warns that "the order returned is unspecified".
  → **Numeric sorting must be done separately** (otherwise string sorting puts COM2/COM10 in the wrong order).
  Friendly names ("USB Serial Port (COM3)") require WMI `Win32_PnPEntity`, filtering `Caption` for `"(COM"`.
  ⚠️ `System.Management` is Windows-only and must be guarded with `OperatingSystem.IsWindows()` (CA1416).
- **Linux**: enumerate `/dev/ttyS*` (real UART), `/dev/ttyUSB*` (FTDI/CP210x/CH340), and `/dev/ttyACM*` (Arduino).
  Use `/sys/class/tty/*/device/driver` to filter out phantom `ttyS*` devices registered by the 8250 driver.
  *Unverified*: the exact literal path for obtaining USB friendly names from sysfs.
- **macOS**: `/dev/tty.*` is dial-in and **blocks while waiting for the DCD carrier**; `/dev/cu.*` is call-out and opens immediately.
  **Terminal-class applications must use `/dev/cu.*`**, otherwise opening an unplugged adapter hangs forever.
  *Unverified*: the explicit wording in Apple's official documentation (currently based on pySerial source code and third-party technical references).

### 4.2 Telnet

**Conclusion: as of 2026-07, there is no mature .NET Telnet library that can be used directly as the network layer for a VT100 full-screen terminal. Implementing it on top of `TcpClient` is recommended.**

| Package | Version / Last release | License | Real negotiation? |
|---|---|---|---|
| `PrimS.Telnet` | 0.13.1 / 2024-11 | MIT | **No**. It is positioned as an automation helper for "sending commands and capturing responses" |
| `TelnetNegotiationCore` | 2.5.3 / **2026-07 (active)** | Apache-2.0 | **Yes**, but aimed at MUDs and **states that it does not provide VT100 emulation** |
| `TentacleSoftware.Telnet` | 2.1.0-rc1 / 2018 | — | Abandoned |
| `System.Net.Telnet` | — | — | **Does not exist** |

The RFC 854 protocol surface is small, and this project **already has a VT engine; it only lacks the negotiation layer**.
Introducing a MUD-oriented dependency for a few hundred lines of code is not good value.

#### Protocol constants (confirmed item by item from the RFC text)

```
IAC=255  WILL=251  WONT=252  DO=253  DONT=254  SB=250  SE=240

IAC escaping: a literal 0xFF in the data stream is sent as IAC IAC (255 255), and the receiver restores it to a single 0xFF
Subnegotiation frame: IAC SB <option> <params...> IAC SE     (0xFF within params must also be doubled)
```

#### Minimum negotiation set required to run vt100 full-screen programs

| Option | Code | RFC | Requirement |
|---|---|---|---|
| **SUPPRESS-GO-AHEAD** | 3 | 858 | **Required** |
| **ECHO** | 1 | 857 | **Required** |
| **TERMINAL-TYPE** | 24 | 1091 | **Required** (otherwise the remote side does not know TERM) |
| **NAWS** | 31 | 1073 | **Required** (otherwise htop/vim render at 80x24) |
| BINARY | 0 | 856 | Strongly recommended (8-bit transparency for UTF-8 / ZModem), negotiated separately by direction |

**Core mechanism**: RFC 858 explicitly states that ECHO and SGA "normally have to be in effect simultaneously to effect what is commonly understood to be **character at a time echoing**".
That is, the server sends `WILL ECHO` + `WILL SGA`, and the client responds to both with `DO`, entering character-at-a-time + remote echo mode.
**This is a prerequisite for vim/htop to work.**

> **kludge line mode**: if the server does not provide ECHO+SGA together, it falls back to local line buffering,
> and the server does not see input until Enter is pressed. Full-screen curses programs cannot work in this mode.
> **This is an excellent runtime diagnostic point**: once negotiation completes, if both options have not been received together, a clear warning should be shown to the user.

**NAWS byte layout** (RFC 1073, confirmed by two independent pieces of evidence):

```
IAC SB 31 <width-hi> <width-lo> <height-hi> <height-lo> IAC SE
```

Two 16-bit big-endian values. The RFC 1073 text says: "any occurrence of 255 in the subnegotiation must be doubled".
Therefore, **every one of the four payload bytes equal to 0xFF must be doubled** (for example, width=255 sends `0x00 0xFF 0xFF` on the wire).
**This is easy to miss and only appears when the window width/height is exactly 255 or 65280+.**

**TERMINAL-TYPE**: `SEND=1, IS=0`. Server → `IAC SB 24 1 IAC SE`; client → `IAC SB 24 0 'V','T','1','0','0' IAC SE`.

### 4.3 Newline Handling (Integration with the Existing VT Engine)

**Current state**: `Terminal/Emulation/InputEncoder.cs:51` — `Key.Enter => modes.NewLineMode ? "\r\n" : "\r"`,
**by default it sends a bare `\r`** (LNM defaults off). An incoming bare CR only returns the carriage and does not advance the line.

**Telnet**: RFC 854 specifies `CR LF` as newline; a plain carriage return must use `CR NUL`, and a bare CR is invalid in NVT ASCII.
RFC 1123 §3.3.1 requires the client to be configurable, with **CR LF as the default SHOULD**.
→ **The Telnet transport layer must rewrite outgoing bare `\r` to `\r\n` itself; it cannot rely on LNM.**

**Serial**: there is no protocol-level newline contract, and many embedded devices send only a bare CR.
When fed into the VT engine, this appears as **each line overwriting the previous line**.
PuTTY/minicom/screen therefore all provide an "Implicit LF in every CR" switch. **A similar switch must be added**.
*(This section is a consensus-level conclusion; first-party PuTTY/minicom documentation was not checked item by item.)*

---

## 5. Main Risks

### Serial

1. **`Close()` blocks forever when hardware flow control is stuck** (#20362), causing the entire window to deadlock if Dispose runs on the UI thread.
   The existing design prevents this, but **new synchronous Dispose paths would trigger it again**.
2. **`PublishSingleFile` + `SelfContained`** (`VelaShell.csproj:15-19`) and packaging of `runtime.native.System.IO.Ports`.
   Linux/macOS artifacts may lack `libSystem.IO.Ports.Native`, resulting in a runtime `PlatformNotSupportedException` that **cannot be detected on a local Windows development machine**. `tests/VelaShell.Tests/Integration/CrossPlatformPublishTests.cs` must be expanded.
3. USB-to-serial hot-unplug IOException storms must be normalized to EOF, following the ConPTY approach (`ConPtyShellStream.ReadAsync:58-69`).
4. **Cannot be automated completely**: real hardware or a virtual serial pair from `socat` / `com0com` is required.

### Telnet

1. **IAC doubling/undoubling must cover every byte path**. If omitted, the symptom is "everything works normally, but large-file transfers or output containing 0xFF is randomly corrupted".
   **This directly destroys ZModem and is extremely difficult to diagnose.**
2. **Doubling 0xFF inside the NAWS payload** only triggers when the window width/height is exactly 255, so it will almost certainly be missed. This is a typical latent bug.
3. **Scope of CRLF rewriting**: it may only apply to the user's Enter key. Applying it to the entire outgoing stream corrupts ZModem and pasted data.
   This directly conflicts with the principle that "the transport layer only moves bytes", while **`IShellStreamWrapper` has only one `WriteAsync` and cannot distinguish the two**.
   This is the only place where the abstraction **may genuinely need to be extended**.
   Alternative: do no rewriting once BINARY negotiation succeeds; otherwise perform CR→CRLF at the transport layer, and temporarily set a bypass flag in the router during a ZModem session.
4. **When the server does not provide ECHO+SGA**, users cannot see their own input. A local echo fallback and a clear notice are needed.
5. **Missing security semantics**: this is a plaintext protocol with no host keys and no encryption.
   The existing `IHostKeyService` / `SecurityAlertService` / known-hosts chain **does not apply**.
   The UI needs a clear "unencrypted" indicator so users do not mistake it for SSH-level security.

---

## 6. Implementation Recommendations

### Workload

| Dimension | Serial | Telnet |
|---|---|---|
| New files | 6–8 | 6–8 |
| Modified files | ~16 (the full table in Section 3 + 5 resx files) | ~6 (most shared with serial) |
| New tests | 3–4 | 3–4 |

> **The "protocol generalization" work in Section 3 is shared by both. It must be completed regardless of which comes first;
> the second feature will cost only about 40% as much as the first.**

### Recommended Order: **Telnet First, Serial Second**

Reason: Telnet is fully managed and can be integration-tested by starting telnetd in Docker (the repository already has a precedent in `docker-compose.test.yml`); serial depends on real hardware and is difficult to verify automatically.

Doing Telnet first allows the "protocol generalization" work to be completed under **testable conditions**.
When serial is added, only its own transport implementation and UI form remain.

### Files That Need to Be Written

**Shared**
- Local echo component (inside `VelaShell.Terminal`, optionally enabled)
- Five new resx keys (currently 938 keys, **all five languages must be synchronized**)

**Telnet**
- `Core/Models/TelnetSettings.cs`
- `Infrastructure/Telnet/TelnetOptions.cs` (constant table)
- `Infrastructure/Telnet/TelnetNegotiator.cs` (IAC state machine, option policy table, subnegotiation codec)
- `Infrastructure/Telnet/TelnetShellStream.cs` (`IShellStreamWrapper`, `Resize`→NAWS)

**Serial**
- `Core/Models/SerialSettings.cs`, `SerialPortInfo.cs` + `ISerialPortEnumerator`
- `Infrastructure/Serial/SerialShellStream.cs` (blocking `Read()` + `Task.Run`, exception normalization to EOF, no-op `Resize`)
- `Infrastructure/Serial/SerialPortEnumerator.cs` (three-platform implementation)
