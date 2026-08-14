# SFTP Dual-Pane and WinSCP Gap Analysis

> Date written: 2026-07-22　　Baseline code: `tmds-ssh` branch
>
> Purpose: **decision checklist**. List the gaps between VelaShell's dual-pane SFTP and WinSCP item by item, so features can be selected for future implementation.
> Each item is marked with its current status and implementation cost. Anything not verified is marked "unverified"; no assumptions are made.

---

## I. Conclusion First

VelaShell is currently a **quite solid one-way file transfer tool**, but it is not yet a **dual-pane file manager**.

The transfer pipeline itself is clearly better than that of similar small tools: recursive upload/download planning, four conflict strategies, resumable transfers including offset verification, rate limiting, concurrency, a progress popup, transfer logs, cancellation semantics, and `LocalPathSafety` path traversal validation.
The remote pane's table, with 7 columns, sorting, hideable columns, resizable columns, and double-click auto-sizing, also exceeds expectations.

The gaps are concentrated in three layers, and **they must be handled differently and should not be scheduled together**:

| Category | Meaning | User perception | Recommendation |
|---|---|---|---|
| **A. Wiring debt** | The code is written but not connected to the dual-pane path | **Feels like a bug** | Fix first, very low cost |
| **B. Missing capability** | It genuinely has not been built, but the architecture supports it | Fewer features | Schedule based on demand |
| **C. Architectural gap** | The interface layer has no provision for it | Fundamental gap versus WinSCP | Requires dedicated design |

---

## II. Category A: Wiring Debt (Recommended First)

These are **not "not implemented"; they are "implemented but not connected"**. Users will report them as bugs, while the cost to fix them is low.

| # | Problem | Location | Symptom |
|---|---|---|---|
| A1 | **The external editor always reports "not configured" in the dual-pane view** | `SftpDocumentViewModel.cs:33-40` does not assign `GetDefaultEditorPath` | The editor configured in settings cannot be used. The terminal-side host is wired (`MainWindowViewModel.cs:846`), but the dual-pane view is missing it |
| A2 | **The local pane header cannot sort** | `LocalFilePaneView.axaml:128-138` uses plain `TextBlock` headers | `SortCommand` (`LocalFilePaneViewModel.cs:49`) and `ToggleSort:499` are fully available, but not bound |
| A3 | **The transfer popup cannot cancel, retry, or clear an individual item** | `FileTransferView.axaml` binds only `CancelAllCommand` and `HidePanelCommand` | Three commands (`FileTransferViewModel.cs:106-112`) have no corresponding controls. **Also, `RetryTransfer:419` only changes the state and does not resend**, so connecting the UI would still be a no-op |
| A4 | **The entire `TransferManager` queue has zero call sites** | `Core/Sftp/TransferManager.cs` | Queuing, concurrency, and cancellation are implemented and DI is registered, but production code never calls `QueueTransferAsync`. The browser uses `SemaphoreSlim` directly |
| A5 | **The remote pane has no button for the parent directory** | `GoUpCommand` (`FileBrowserViewModel.cs:648`) has no axaml binding | The only option is double-clicking the `..` row. The local pane has a button |
| A6 | **The remote pane cannot be restored after being closed in the dual-pane view** | `ToggleVisibilityCommand` → `IsVisible=false` | This button was designed for the terminal host, which has a reopen path. In the dual-pane view, clicking it leaves only empty space |
| A7 | **Transfer settings in the dual-pane view are a construction-time snapshot** | `SftpDocumentViewModel.cs:36-37` | Changes to settings do not affect already-open SFTP documents. `OnSettingsSaved` only traverses the terminal-side cache |
| A8 | **The dual-pane view does not read or write column visibility and hidden-file settings** | `SftpDocumentViewModel.cs:33-40` | The terminal side wires `ShowHiddenFiles` and `ColumnVisibilityToggled`; the dual-pane view does not |
| A9 | **`DownloadItemCommand` is not bound** | `FileBrowserViewModel.cs:670` | This also makes `PickSavePathForDownload`, "save as when downloading a single file", unreachable from the UI |
| A10 | **Rate limiting is not applied to resumed downloads** | The resume branch in `SftpService.cs` does not wrap the stream in `ThrottledStream` | Bandwidth limiting silently stops working during resumable transfers |
| A11 | **The transfer log records Copy as DOWNLOAD** | `TransferLogService.cs:40` uses a ternary that distinguishes only Upload and everything else | The log type is inaccurate |
| A12 | **Dragging a file from the OS into the local pane refreshes but does not copy** | `LocalFilePaneView.axaml.cs:234-243` | Nothing happens after the drop |

> **Duplicate definitions**: `AppSettings.AutoResume` (:623) and `ResumeEnabled` (:668) overlap semantically; only the latter is consumed.
> `TransferMaxRetries` (:671) and `AutoCleanTempFiles` (:674) have code comments admitting that they are "planned, with no runtime consumer".

---

## III. Category B: Missing Capabilities (Compared by WinSCP Feature Domain)

### 3.1 Browsing and Navigation

| WinSCP capability | VelaShell | Description |
|---|---|---|
| Editable path input | ❌ None | Both sides have only breadcrumbs, so a path cannot be pasted directly for navigation. **High-frequency basic need** |
| Forward/back history | ❌ None | Neither VM has a history stack |
| Bookmarks/favorites | ❌ None | WinSCP users rely on this heavily |
| Directory history dropdown | ❌ None | |
| Breadcrumbs | ✅ Implemented on both sides | |
| Hidden-file toggle | ⚠️ Remote only | The local pane always shows hidden files and has no toggle |
| Local drive switching | ✅ Implemented | |

### 3.2 File Operations

| WinSCP capability | VelaShell | Description |
|---|---|---|
| Create folder/file | ✅ Complete on remote | The local pane has **create folder only, not create file** |
| Rename | ✅ Both sides | |
| Delete, recursive + progress + cancellation | ✅ Both sides | Good quality, with weighted progress on the remote side |
| Move/copy, remote → remote | ⚠️ Requires typing an absolute path | No directory picker and no drag-and-drop move |
| chmod | ⚠️ Single file only | Has a 9-cell rwx matrix with octal synchronization, but **no batch operation, recursive application, or setuid/sticky bits** |
| **chown, change owner/group** | ❌ None | `ISftpService` has no interface; Owner/Group in the properties dialog are read-only |
| **Create/identify symbolic links** | ❌ None | `RemoteFileInfo` has no symbolic-link field. The first character of the permission string is only `d`/`-`, so links are treated as regular files |
| **Modify remote timestamps** | ❌ None | Timestamps are preserved only during download; uploads do not write back mtime |
| Local file attributes/permissions | ❌ None | The local pane has only 5 context-menu items |

### 3.3 Transfers

| WinSCP capability | VelaShell | Description |
|---|---|---|
| Drag and drop, between panes and OS → remote | ✅ Implemented | |
| **Drop onto a specific target-folder row** | ❌ None | Drop currently applies only to the "current directory" and does not resolve the drop target |
| **Double-click transfer** | ❌ Different semantics | Double-clicking remote files downloads them and opens them with an OS program; double-clicking a local file **does nothing** |
| Conflict handling, overwrite/skip/rename/ask | ✅ All four | Very good quality |
| Resumable transfers | ✅ Implemented | Includes offset verification and a safe fallback window |
| Rate limiting/concurrency | ✅ Implemented | See A10 |
| **Transfer queue, visualizable, pausable, reorderable** | ❌ See A4 | WinSCP's queue is a core part of its experience |
| **Post-transfer verification, checksum** | ❌ None | |
| Completion notification | ✅ Implemented | |

### 3.4 Selection and Batch Operations

| WinSCP capability | VelaShell | Description |
|---|---|---|
| Multiple selection, add via context menu, retain selection across navigation | ✅ Implemented | |
| **Wildcard selection (`*.log`)** | ❌ None | WinSCP's `Select Files` is a frequent operation |
| **Invert selection** | ❌ None | |
| **Recursive selection** | ❌ None | |
| Select all | ⚠️ Only through the default ListBox behavior | No explicit `Ctrl+A` binding |
| **Keyboard shortcuts (F5/F2/Del/Enter)** | ❌ Completely absent | Neither View has `KeyBindings` or `KeyDown` handling. **WinSCP veterans will find this very unfamiliar** |

### 3.5 Search and Filtering

| WinSCP capability | VelaShell | Description |
|---|---|---|
| **Current-directory filter box** | ❌ None | In a directory with 500 files, finding one requires scanning by eye |
| **Remote recursive search** | ❌ None | `ISftpService` has no Find/Search API |

### 3.6 Editing and Viewing

| WinSCP capability | VelaShell | Description |
|---|---|---|
| Built-in editor + automatic upload on save | ✅ Implemented, 5MB limit | **Syntax highlighting added on 2026-07-22**, see the next section |
| External editor | ⚠️ See A1 | Logic is complete, but not wired into the dual-pane view |
| **File preview pane** | ❌ None | |

### 3.7 Views

| WinSCP capability | VelaShell | Description |
|---|---|---|
| Sort remote pane by column / column visibility / resize columns | ✅ Implemented | |
| Local-pane sorting | ⚠️ See A2 | |
| **Remember column widths across restarts** | ❌ None | Exists only in the VM instance |
| **Remember pane ratio** | ❌ None | GridSplitter can be dragged but the value is not persisted |
| **Local pane has only 3 columns** | ❌ | Missing permissions/owner/group/type |
| Switch between icon view and detailed view | ❌ None | |

---

## IV. Category C: Architectural Gaps

These three capabilities **have no provision at the interface layer** (`ISftpService.cs` is 82 lines in full and has no related signatures), so they require dedicated design.

### C1. Synchronization and Directory Comparison (**the most fundamental gap versus WinSCP**)

VelaShell has **zero lines of code** for WinSCP's three major synchronization capabilities:

- **Directory comparison**: show side-by-side which items exist only on the left, only on the right, differ, or match
- **Synchronization, one-way / two-way / mirror**: preview differences → confirm → execute in batch
- **Keep remote directory up to date**: watch local changes and upload them automatically

This is the **only reason many people choose WinSCP**. Implementing it requires a comparison result model, difference calculation based on size + mtime with optional checksum, a preview UI, and an execution engine that reuses the existing transfer pipeline.

### C2. Search Capability

`ISftpService` needs a new recursive search API. Pay attention to the cost of recursively traversing a remote directory. It needs streaming results and cancellation. The existing `ListDirectoryAsync` returns a `List` all at once and is not suitable for direct recursion.

### C3. Terminal Integration

- The file pane's context menu has no "Open terminal here" action
- The terminal's current directory cannot be synchronized with the SFTP pane

The SFTP document is currently a pure dual-pane view (`SftpDocumentView.axaml` has no terminal control).
This project **already has a complete terminal stack**, so the architectural foundation for this feature is actually ready. The main work is UI orchestration.

---

## V. Recommended Priority

Ordered purely by return on investment, for reference:

**First tier, low cost and highly visible**
1. Category A wiring debt (A1, A2, A3, A5, A6, A7, A8), the items users treat as bugs
2. Keyboard shortcuts (F5 refresh / F2 rename / Del delete / Enter enter / Ctrl+A select all)
3. Editable path input
4. Current-directory filter box

**Second tier, moderate cost and clear demand**
5. Wildcard selection / invert selection
6. Visual transfer queue (A4 + individual cancel and retry)
7. Persist column widths and pane ratio
8. Complete the local pane, sorting, hidden-file toggle, and create file
9. Drop onto a target-folder row

**Third tier, high cost and differentiation**
10. **Directory comparison + synchronization** (C1), which determines "whether it can replace WinSCP"
11. Remote recursive search (C2)
12. Terminal integration (C3)
13. chown / symbolic links / timestamp modification
14. Post-transfer verification

---

## VI. Appendix: Editor Enhancements Completed on 2026-07-22

The built-in editor (`RemoteFileEditorView`) originally had **no syntax highlighting at all**. The following was added:

- **Automatic file-type detection** (`Services/Syntax/FileTypeDetector.cs`): extension → special filename (`Dockerfile`/`sshd_config`/`fstab`/`.bashrc`…) → **shebang**.
  The third level is especially important for remote editing: many executable scripts on servers have no extension, and only `#!/bin/bash` identifies what they are.
- **Added missing AvaloniaEdit syntax definitions** (`Syntax/*.xshd`): among the 20 built-in definitions, **Shell, YAML, INI/conf, Dockerfile, and Log were conspicuously absent**. These are precisely the five types most often edited in daily operations, so definitions were written in-house.
- **Theme following** (`SyntaxHighlightingService`): AvaloniaEdit's built-in definitions are tuned for light backgrounds, with keywords in `Blue` and punctuation in `Black`. On this application's Dracula surface, `#282A36`, punctuation becomes invisible. Named colors are now recolored globally for Dracula (dark) / Alucard (light), with contrast fallbacks for roles not covered by the theme.

**Known limitation**: changing the theme after opening the editor does not update its colors in real time. Reopen the editor to apply the new colors.
