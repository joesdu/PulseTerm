# FTP Client Support: Feasibility Research

> Date written: 2026-08-13    Baseline code: `main` @ `937d322`
>
> Purpose: provide a **factual basis and change checklist** for the subsequent implementation of FTP / FTPS session types.
> All code findings are backed by `file:line number` references. Findings verified online are marked with version and license details; anything not verified is marked "unverified".
>
> Prerequisite reading: [`telnet-and-serial-feasibility-research.md`](telnet-and-serial-feasibility-research.md). The protocol-generalization changes in its Section 3 are fully shared with this document and are not repeated here.
>
> **Implementation status (2026-08-13)**: This document's P0 and P1 have been implemented and merged: `ConnectionType.FTP` + `SessionProfile.Ftp`,
> `FtpFileService` (an `ISftpService` implementation) + `FtpConnectionPool`, the session-routed `RoutingRemoteFileService`,
> the FTP tab and FTPS form in the connection dialog, the certificate-fingerprint trust flow, and importer conversion of FTP/FTPS to supported types.
> **The only remaining item to validate is Risk 1 in Section 5 (TLS session reuse)**: run one real connectivity test each against vsftpd and FileZilla Server using their default configurations.
> It determines whether the LGPL-licensed `FluentFTP.GnuTLS` must be introduced. The rest of this document remains unchanged as a record of the design tradeoffs.

---

## 1. Conclusion First

**FTP is not the same kind of problem as Telnet / serial.** The latter two are **terminal protocols** and can reuse the existing `IByteDuplex` / `IShellStreamWrapper` abstractions directly, with the conclusion from that research being "zero transport-layer changes." FTP is a **file protocol**. It needs to connect to the file-pane path, which currently contains an SSH-specific implementation.

**The good news: the seam already exists, and it is higher-level than expected.**

The correct seam is **`ISftpService`**, not `ISftpClientWrapper`:

| Interface | Shape | Can carry FTP? |
|---|---|---|
| `Core/Sftp/ISftpService.cs:21-88` | Everything is keyed by `Guid sessionId`, returns `RemoteFileInfo`, with **zero SSH types** | ✅ Yes |
| `Core/Ssh/ISftpClientWrapper.cs:9-140` | Requires seekable streams, `posix-rename@openssh.com`, integer UID/GID, and `ResumeSafetyMargin` | ❌ This is the shape of SFTP |

The decisive evidence is the **model actually consumed by the upper layer**: `Permissions` / `Owner` / `Group` in `Core/Models/RemoteFileInfo.cs:26-30` are all **strings** (`rwxr-xr-x`, user name, group name), exactly what FTP's `LIST`/`MLSD` can provide. Integer UID/GID exists only within the `SftpEntry` hop (`Core/Ssh/SftpEntry.cs:25-28`), and must be translated to names by running `getent passwd` over an **SSH exec channel** in `RemoteIdentityResolver.cs:28,42`. **FTP can use the `ISftpService` layer and bypass this SSH dependency entirely.**

**Bad news 1: FluentFTP data streams are not seekable, which directly conflicts with the `ISftpClientWrapper` contract.**

Verified from source: `FtpSocketStream.CanSeek => false`, `Seek()` directly `throw new InvalidOperationException()`, and the setter for `FtpDataStream.Position` explicitly throws ("You cannot modify the position of a FtpDataStream"). The contract in `ISftpClientWrapper.cs:99-106` states that an **implementation must return a seekable stream**, and `SftpService.cs:583-586` actively asserts and throws during resume validation.

→ This confirms from the opposite direction that the seam must be `ISftpService`: **do not attempt to make FTP implement `ISftpClientWrapper`.**

**Bad news 2: FTP has no multiplexing, while the existing code makes concurrent transfers a design assumption.**

The original comment in `SerializedSftpService.cs:7-12` states:

> **Transfers (upload/download/remote copy) do not occupy the serialization gate**... The underlying Tmds.Ssh `SftpClient` is itself designed for concurrent use, so allowing it through is safe.

One FTP control connection can execute only one command at a time. FluentFTP locks internally, and community issue [#1499](https://github.com/robinrodricks/FluentFTP/issues/1499) reports that `GetListing` fails during transfers. **The FTP backend must include its own connection pool**: at least one control connection for metadata and one connection per concurrent transfer. Otherwise, it either degrades to full serialization or fails unpredictably.

**Overall assessment: feasible, with a moderate-to-large effort.** The core work is "build an `FtpFileService : ISftpService` plus a session-routed dispatcher," alongside the protocol-generalization changes already listed in the Telnet research. **The true uncertainty is not in the code, but in FTPS TLS session reuse (see Risk 1 in Section 5). That determines whether real production servers can be reached and must be verified before implementation begins.**

---

## 2. Directly Reusable, With Zero Changes

| Component | Location | Notes |
|---|---|---|
| Dual-pane file browser | `ViewModels/FileBrowserViewModel.cs:89`, `LocalFilePaneViewModel.cs` | Depends only on `ISftpService` + `Guid sessionId` |
| Transfer manager and throttling | `Core/Sftp/TransferManager.cs`, `ThrottledStream.cs` | Protocol-agnostic |
| Transfer flyout, conflict policy, drag and drop | `ViewModels/FileTransferViewModel.cs`, `Core/Sftp/DragDropFormats.cs` | Protocol-agnostic |
| Serialization decorator | `Core/Sftp/SerializedSftpService.cs` | `ISftpService → ISftpService`, reusable. However, the **gate policy must be adjusted for FTP**. See §3.2. |
| Lifecycle of standalone SFTP dual-pane documents | `ViewModels/SftpDocumentViewModel.cs:42` | Consumes only `ISftpService` |
| SonnetDB persistence pipeline | `Infrastructure/Persistence/*` | See the field synchronization cost in §3.7 |
| Session-import framework | `Infrastructure/Import/*` | See below |

> **Import-side win**: `WinScpImportService.cs:183` currently maps `FSProtocol=5` to `(SSH, false, "FTP")`.
> Once `ConnectionType` includes `FTP`, changing one enum value here converts it to "supported."
> The fully automatic importer just introduced in PR #157 will **automatically select** these sessions, so WinSCP users can use them after migration.
> The `_ =>` fallback in `XshellImportService.cs:101-108` also needs FTP/FTPS branches.

---

## 3. Required Changes

### 3.1 Protocol Generalization, Fully Shared With Telnet / Serial

Four enum constraints, two primary dispatch points, validation predicates, and session-tree node criteria. See the itemized checklist in [`telnet-and-serial-feasibility-research.md`](telnet-and-serial-feasibility-research.md) §3.1–3.4; it is not repeated here. Whether Telnet or FTP is implemented first, this set of changes must be completed once. The second protocol costs roughly 40% of the first.

Two additional locations FTP must address, not covered by the Telnet research:

| Location | Problem |
|---|---|
| `Views/ConnectionProfileView.axaml:174-198` | The protocol tab strip only has clickable SSH/SFTP tabs plus two disabled Borders for Telnet/serial. **FTP does not even have a disabled placeholder**. The sliding underline in `ConnectionProfileView.axaml.cs:41` is a binary ternary expression. |
| `tests/VelaShell.Tests/Views/ConnectionProfileViewUiTests.cs:46,54` | Hard assertions expect "exactly 2 proto-tab Buttons + exactly 2 disabled Borders." **Adding the FTP tab will necessarily fail both assertions.** |

### 3.2 File-Service Routing, the Core Addition in This Document

`ISftpService` is a **singleton**. Every method accepts `sessionId` (`ISftpService.cs:24-87`), and its only DI wiring point is `InfrastructureServiceCollectionExtensions.cs:95-114`. Adding FTP requires a router that dispatches by session protocol:

```
ISftpService (the sole public contract, no UI changes)
  └── RoutingRemoteFileService            ← new: look up protocol by sessionId, then forward
        ├── SftpService  (existing, SSH sessions)
        └── FtpFileService (new, FTP sessions) ── FtpConnectionPool ── AsyncFtpClient × N
```

Key points:

- `SftpService.cs:731-732` resolves the session through `ISshConnectionService.GetSession(sessionId)` and requires `Status == Connected`. FTP needs an equivalent `IFtpConnectionService`, and **the session IDs of both must come from the same namespace**, otherwise the router cannot determine the protocol.
- The DI statement `wrapper is not TmdsSshClientWrapper → throw "SFTP requires Tmds.Ssh backend."` (`InfrastructureServiceCollectionExtensions.cs:102-103`) is a **hard downcast**. Once the router is introduced, it should become a factory selected by `ConnectionType`.
- `SerializedSftpService` must differentiate its gate policy by protocol. SSH continues to let transfers bypass the gate. **For FTP, either transfers also occupy the gate, degrading to full serialization, or the connection pool inside `FtpFileService` guarantees every transfer an exclusive connection.** The latter is recommended, with its concurrency limit aligned to the user's "maximum concurrent transfers" setting.

### 3.3 Do Not Reuse `ConnectionWorkflowService` / `ConnectionInfo`

`ConnectionWorkflowService.cs:64-85` unconditionally calls `_sshConnectionService.ConnectAsync(...)`. `ConnectionInfo.cs:11-48` contains `required` + `init` **SSH-only transport parameters** (`Port=22`, `PrivateKeyPath`, `JumpHost`). The conclusion is the same as in the Telnet research: **do not extend it**. FTP needs its own connection service, but must join the same session lifecycle, including connect/disconnect, events, status bar, and session tree.

Also note that `ConnectionWorkflowService.ValidateProfile:239-270` forces `Username` to be non-empty. **Anonymous FTP login would be blocked**, so the validation predicate must dispatch by protocol.

### 3.4 Metadata Mapping, a Comparison of Three Models

| `RemoteFileInfo` (consumed by UI) | Current SFTP path | New FTP path (FluentFTP `FtpListItem`) |
|---|---|---|
| `Name` / `FullPath` | `SftpEntry.Name/FullName` | `Name` / `FullName` |
| `Size` | `Length` | `Size` |
| `IsDirectory` | `IsDirectory` | `Type == FtpObjectType.Directory` |
| `LastModified` | `LastWriteTime` | `Modified` (after time-zone conversion) / `RawModified` (original) |
| `Permissions` (string) | Constructed from 9 Boolean bits | `RawPermissions`, or constructed from `OwnerPermissions`/`GroupPermissions`/`OthersPermissions` |
| `Owner` / `Group` | `UserId`/`GroupId` → translated through **SSH exec `getent passwd`** (`SftpService.cs:785-786`, `RemoteIdentityResolver.cs:28,42`) | `RawOwner` / `RawGroup` are **already names** |

**Conclusion: FTP does not pass through `SftpEntry`. It produces `RemoteFileInfo` directly and bypasses `RemoteIdentityResolver` completely.**
The cost is that these fields are empty on many FTP servers, such as Windows/IIS-style LIST output. The UI must tolerate empty values. See §3.6.

### 3.5 Resume Semantics Must Be Redefined

Current resume behavior relies on two SFTP-specific capabilities:

1. `ResumeSafetyMargin` (`ISftpClientWrapper.cs:129-139`, `TmdsSftpClientWrapper.cs:23` = 64×32KB), which leaves a margin for holes caused by Tmds.Ssh write-buffer completions arriving out of order. **FTP writes sequentially over a single data connection, so this value should be 0.**
2. Tail comparison requires a seekable stream (`SftpService.cs:520-586`), which FTP cannot provide.

**Alternative, feasible and verified**: FluentFTP's
`DownloadStream(Stream outStream, string remotePath, long restartPosition = 0, IProgress<FtpProgress> progress = null, CancellationToken token = default, long stopPosition = 0)`
accepts `restartPosition` + `stopPosition`, which is effectively **range reading**. Tail verification can use it to read the last N remote bytes without seeking. Uploads use `FtpRemoteExists.Resume` or writing with an offset.

### 3.6 Capability Flags: Stop Assuming Everything Is Available

| Capability | SFTP | FTP |
|---|---|---|
| chmod | Native `setstat` | Depends on `SITE CHMOD` (supported by FluentFTP), **which many servers do not implement** |
| Owner/group | UID/GID + lookup | Depends on the LIST dialect and may be empty |
| Preserve timestamps | `setstat` (`ISftpClientWrapper.cs:94`) | `MFMT`/`MDTM`, not mandatory |
| Symbolic links | Native | `MLSD` has `type=OS.unix=symlink`, but dialects vary |
| Atomic rename | `posix-rename` extension (`:77`) | `RNFR`/`RNTO`; cross-directory behavior varies by server |

Add a capability query to `ISftpService`, or the layer above it, so `FileBrowserViewModel` can hide the chmod menu and owner/group columns according to capabilities instead of showing them unconditionally and then throwing errors.

### 3.7 Add `FtpSettings?` to `SessionProfile`

Use the approach from §3.5 of the Telnet research: a nullable nested object. When absent it is null, so old data is unaffected:

```csharp
public FtpSettings? Ftp { get; set; }
// EncryptionMode(None|Explicit|Implicit|Auto) / DataConnectionType(PASV|EPSV|PORT|EPRT)
// / Anonymous / Encoding(UTF8|Auto|specified) / MaxConnections / ValidateCertificate policy
```

**The cost is unchanged**: `SessionProfile` is copied field by field manually across the repository. The new field must be synchronized in five locations:
`SonnetDbSessionRepository.cs:131-151`, `ConnectionWorkflowService.cs:113-131`,
`SessionTreeViewModel.cs:341`, `ConnectionProfileViewModel.cs:520-542` (`BuildProfile`), and `MainWindowViewModel.cs:2553-2555`.

> The line numbers for these five locations in the Telnet research have drifted with subsequent commits. The locations above were rechecked against `937d322`.

### 3.8 Localization

None of the five resx files currently contains any FTP keys, not even an "unsupported" placeholder. Add the protocol name, FTPS encryption modes, passive/active mode, anonymous login, certificate-trust prompts, and so on. **All five languages must be updated together**, as `LocalizedKeyUsageTests` and key-set consistency tests enforce this.

---

## 4. Technical Selection

### 4.1 Recommended: FluentFTP 54.2.0 (MIT)

| Item | Value |
|---|---|
| Version / release | **54.2.0 / 2026-05-26** |
| License | **MIT**, compatible with this repository's AGPL-3.0 + commercial dual license |
| Dependencies | **None** |
| Downloads | 56.9M cumulative |
| TFM | net462, net472, netstandard2.0/2.1, net7.0, net8.0, **net9.0** |

**TFM note**: this repository uses `net11.0` (`Directory.Build.props`). FluentFTP does not yet include net10/net11 assets, so it resolves to the **net9.0** asset. This is functionally fine, but worth documenting in a CPM comment. There is no publication risk: `VelaShell.csproj` uses `SelfContained=true` + `PublishTrimmed=false` and **deliberately does not use PublishSingleFile**. Pure managed DLLs do not involve trimming or native-resource packaging.

**API comparison (`ISftpService` → `AsyncFtpClient`)**:

| `ISftpService` member | FluentFTP |
|---|---|
| `ListDirectoryAsync` | `GetListing(path, FtpListOption, token)` → `FtpListItem[]` (also `GetListingAsyncEnumerable`) |
| `UploadFileAsync` (including `resumeOffset`) | `UploadStream` / `UploadFile` + `FtpRemoteExists.Resume`, `IProgress<FtpProgress>` |
| `DownloadFileAsync` (including `resumeOffset`) | `DownloadStream(out, path, restartPosition, progress, token, stopPosition)` |
| `DeleteAsync` (recursive) | `DeleteFile` / `DeleteDirectory` (recursive deletion must enumerate and expand manually to report progress) |
| `CreateDirectoryAsync` / `EnsureDirectoryAsync` | `CreateDirectory(path, force)` |
| `RenameAsync` | `Rename` / `MoveFile` / `MoveDirectory` |
| `SetPermissionsAsync` | `Chmod` (`SITE CHMOD`, requires capability detection) |
| `GetFileInfoAsync` | `GetObjectInfo` |
| `OpenReadAsync` | `OpenRead` (**stream is not seekable**, compatible because this `ISftpService` member already documents "sequential reading") |
| `ExistsAsync` | `FileExists` / `DirectoryExists` |
| `GetWorkingDirectoryAsync` | `GetWorkingDirectory` |
| Verification, optional enhancement | Built-in MD5 / CRC32 / SHA-1/256/512 |

It also includes parsing for 30+ server-specific LIST dialects, automatic capability discovery, throttling, and reconnect handling. **These are precisely the most time-consuming and error-prone parts of a custom implementation.**

### 4.2 Options Not Recommended

| Option | Conclusion |
|---|---|
| `FtpWebRequest` (built into BCL) | **Deprecated** (`SYSLIB0014`, marked obsolete since .NET 6), with no MLSD and insufficient FTPS control. Not usable. |
| Custom FTP client | The protocol surface is an order of magnitude larger than Telnet: dual control/data connections, PASV/EPSV/PORT/EPRT, RFC 4217 FTPS AUTH/PBSZ/PROT, RFC 3659 MLSD/REST/SIZE/MDTM, plus vendor-specific LIST dialects. The Telnet conclusion that a custom implementation was more cost-effective **does not apply here**. |
| Other .NET FTP libraries | *Unverified*. Their maintenance status and licenses were not verified individually. FluentFTP has a clear advantage in activity, licensing, and download volume. |

### 4.3 ⚠️ `FluentFTP.GnuTLS` Is LGPL, Do Not Add It Casually

| Item | Value |
|---|---|
| Version / release | 1.0.40 / 2026-05-05 |
| License | **LGPL-2.1-only** |
| Purpose | "Adds support for TLS1.3 streams into FluentFTP using a .NET wrapper of GnuTLS" |
| Dependencies | FluentFTP ≥ 48.0.3 |

It is the mainstream workaround for Risk 1 in Section 5, TLS session reuse. However, this repository includes `LICENSE-COMMERCIAL.md`, so an **LGPL dependency requires legal confirmation for the commercial-license distribution path**, similar to the MS-PL reminder for `RJCP.SerialPortStream` in the Telnet research. It also includes native GnuTLS components, and packaging for three SelfContained platforms requires separate verification. *Unverified: which RIDs are covered by this package's native binaries.*

**Recommendation: first validate target servers with the pure .NET `SslStream` path. Introduce GnuTLS as an optional plugin only if session-reuse issues are confirmed and server configuration cannot be changed.**

---

## 5. Main Risks

### 1. FTPS TLS Session Reuse (**Highest Risk, Verify Before Implementation**)

Many production FTPS servers require the **data connection to reuse the TLS session from the control connection**. vsftpd has `require_ssl_reuse` **enabled by default**, and FileZilla Server also forces reuse when TLS 1.3 is negotiated. If the requirement is not met, the server immediately returns `522 SSL connection failed; session reuse required`.

This has long been a class of FluentFTP issues ([#236](https://github.com/robinrodricks/FluentFTP/issues/236), [#347](https://github.com/robinrodricks/FluentFTP/issues/347), [#773](https://github.com/robinrodricks/FluentFTP/issues/773), [#951](https://github.com/robinrodricks/FluentFTP/issues/951), [#1283](https://github.com/robinrodricks/FluentFTP/issues/1283), [#1738](https://github.com/robinrodricks/FluentFTP/issues/1738)). The root cause lies on the .NET `SslStream` side. The community solution is to switch to `FluentFTP.GnuTLS` (`Config.CustomStream = typeof(GnuTlsStream)`), the LGPL package from §4.3.

> **This is not an implementation detail. It is a selection prerequisite**: if target users' FTPS servers commonly require session reuse and LGPL is unacceptable, FTPS requires another solution, or support is limited to plaintext FTP + SFTP, greatly reducing its value.
> **Before implementation, run a connectivity test against real or Dockerized vsftpd with its default configuration and FileZilla Server with TLS 1.3.**

### 2. Single-Connection Serialization vs. Concurrent Transfers

See §1 and §3.2. It manifests as "refreshing a directory fails during a transfer" ([#1499](https://github.com/robinrodricks/FluentFTP/issues/1499)). Without a connection pool, the only option is full serialization, silently forcing the user's "maximum concurrent transfers" setting back to 1, precisely the degradation `SerializedSftpService.cs:7-12` explicitly aims to avoid.

### 3. Missing Security Semantics, Shared Origin With Telnet

Plain FTP has neither encryption nor host identity authentication. The existing `IHostKeyService` / known-hosts / `SecurityAlertService` chain **applies only to SSH host keys and is wholly unsuitable for X.509**. The following are required:

- FTPS certificate-validation and trust UI, using FluentFTP's `ValidateCertificate` event. This is a **new** trust chain, do not force it into the host-key mechanism.
- A conspicuous "unencrypted" marker for plaintext FTP in the session tree, tab, and status bar, so users do not mistake it for SSH-equivalent security.
- Credentials continue through the existing `ISecretProtector`; nothing new is required.

### 4. LIST Dialects and Time Zones

Only `MLSD` (RFC 3659) provides reliable UTC timestamps and structured facts. Legacy servers expose only `LIST`, which returns local time and varies by server. Files older than a year may even contain only a year, without hours and minutes. FluentFTP provides both `Modified` (converted) and `RawModified` (original). **The choice between them must be explicit for both "preserve timestamps" and "sort/compare by time," otherwise false cross-time-zone differences will occur.**

### 5. Passive Mode and Network Environments

PASV/EPSV has many failure modes behind NAT and firewalls, including servers returning private-network addresses or unopened port ranges. Expose configuration switches in `FtpSettings`, and translate such failures into prompts users can understand instead of surfacing raw socket exceptions. Follow the existing `TmdsSshInterop.Translate` approach, which consolidates library exceptions into the `VelaSsh*Exception` family. See [`architecture-design.md`](architecture-design.md) §3, "Layering and Dependency Direction," from `:78`: library exceptions are translated only in Infrastructure.

### 6. Good News: Automated Verification Is Possible

Unlike serial, which requires real hardware, FTP can use Docker to start vsftpd / pure-ftpd for integration tests. The repository already has a `docker-compose.test.yml` precedent, currently for SSH integration tests. **Create a matrix directly: plaintext / explicit FTPS / implicit FTPS × session-reuse setting × MLSD availability.**

---

## 6. Implementation Recommendations

### Effort

| Dimension | Estimate |
|---|---|
| New files | 8–10: connection service, connection pool, `FtpFileService`, router, `FtpSettings`, exception translation, certificate-trust dialog, port/mode form |
| Modified files | ~18: the complete §3.1 protocol-generalization table + §3.2 DI wiring + five §3.7 copies + 5 resx files + 2 importer locations |
| New tests | 5–7: Docker FTP integration matrix, LIST-dialect mapping, resume range reading, capability-flag fallback, importer FTP-to-supported conversion |

> The protocol generalization in §3.1 is **shared** with Telnet/serial. If Telnet lands first, the cost of this item drops by about 40%.

### Recommended Phasing

| Phase | Scope | Value |
|---|---|---|
| **P0** | Plain FTP + anonymous/password login + browse / upload / download / delete / create directory / rename, connection pool, router, protocol generalization | Completes the end-to-end path and is testable |
| **P1** | FTPS, explicit first, + certificate-trust UI + resumable transfers with range-read verification + importer conversion of FTP to supported | Reaches production-usable quality |
| **P2** | `SITE CHMOD`, preserve timestamps, capability-driven UI fallback, MLSD time-zone precision, checksum comparison | Aligns experience with SFTP |

**Insert P-1 before P0: run one FTPS connectivity test each against Docker vsftpd, using its default configuration with `require_ssl_reuse=YES`, and FileZilla Server.** The result directly determines whether P1 needs an LGPL dependency and therefore affects the commercial-license feasibility of the whole feature.

### New Files Required

**Core**
- `Core/Models/FtpSettings.cs` (encryption mode / data-connection mode / anonymous / encoding / concurrency)
- `Core/Ftp/IFtpConnectionService.cs`, `FtpSession.cs` (equivalent to `SshSession`, integrated into the shared session lifecycle)
- `Core/Sftp/IRemoteFileCapabilities.cs` (capability query for UI fallback)

**Infrastructure**
- `Infrastructure/Ftp/FluentFtpConnectionService.cs` (connect, authenticate, status events)
- `Infrastructure/Ftp/FtpConnectionPool.cs` (metadata connection + transfer connections, limit aligned to "maximum concurrent transfers")
- `Infrastructure/Ftp/FtpFileService.cs` (`ISftpService` implementation, `FtpListItem → RemoteFileInfo` mapping)
- `Infrastructure/Ftp/FluentFtpInterop.cs` (library exceptions → Core exception family, analogous to `TmdsSshInterop`)
- `Infrastructure/Sftp/RoutingRemoteFileService.cs` (dispatch by `ConnectionType`)

**App / UI**
- Protocol tab and FTP form (`ConnectionProfileView.axaml`), certificate-trust dialog
- New keys in all five resx files

---

## 7. One-Sentence Summary

**It is fully technically feasible, and the seam, `ISftpService`, is cleaner than expected. The file browser, transfer manager, throttling, and drag and drop all require zero changes. The actual work is "build an FTP backend with a connection pool and decouple SSH from sessions, enums, and dispatch." The only issue that could overturn the approach is FTPS TLS session reuse: it puts the licensing question of whether to introduce an LGPL dependency ahead of the technical decision, so it must be verified first.**
