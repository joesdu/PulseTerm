# VelaShell Architecture Design (Engineering Refactoring Blueprint v2)

> This document was written after cross-analyzing three inputs:
>
> 1. The design file `VelaShell-zh.pen` (node tree, design tokens, and screenshots read through Pencil MCP);
> 2. The interaction specification `docs/interaction-and-ui-specs.md` (the sole visual and interaction baseline);
> 3. **A file-by-file walkthrough of the existing codebase** (6 src projects + 6 test projects, ~129 .cs files / ~22 .axaml files).
>
> Goal: without discarding existing usable capabilities (the custom VT engine, SSH, Dock split panes, command palette, theme/i18n scaffolding, and 359 passing tests), provide an **engineering architecture** capable of supporting the 26 goals you listed, and map every lag or defect you reported to its **root cause** and an **architecture-level remediation**.
>
> Updated: 2026-07-07; corrected on 2026-07-10 to reflect the implementation state (§1 inventory, §4.8 conflict policy, §11 explicit non-goals);
> corrected again on 2026-07-22: the target framework has switched to **net11.0** (§2.1 decision completed), the SSH transport layer has migrated from SSH.NET to **Tmds.Ssh** (§2.2, §3), and `StatusMetricChip` has been removed (§6).

---

## 0. Executive Summary (TL;DR)

- **The layering does not need to be redone**: the existing 6-project split (App / Presentation / Controls / Terminal / Core / Infrastructure) and dependency direction are basically correct. This document only **consolidates responsibilities** and adds **4 cross-cutting subsystems**, without adding projects.
- **Performance is the top engineering problem** (your #5/#16). The root cause of the lag is **not SSH but the rendering pipeline**: `SshTerminalBridge` calls `Dispatcher.UIThread.InvokeAsync` for every 4 KB read block and triggers one full-screen `InvalidateVisual`, while `VelaTerminalControl.Render` **creates a new `FormattedText` for every cell on every frame** (120×32 ≈ 3,840 text-layout allocations per frame). Solving this requires an output pipeline of **read → batch → frame-limit → glyph-cache** (see §4.1), which is the core of this architecture.
- **The connection and close hangs** (#17/#18) are caused by **synchronous blocking on the UI thread**: `SshTerminalBridge.Dispose()` calls `_readTask.Wait(2s)` on the UI thread and synchronously calls `ShellStream.Dispose()`. The solution is to **decouple session lifecycle from the transport layer and use a background teardown queue** (§4.3).
- **The Chinese input crash in htop** (#14) is caused by **missing IME composition-state handling**: during candidate composition, `OnKeyDown` still encodes arrow keys, Enter, and ESC as bytes and sends them to the PTY. The solution is to connect Avalonia's `TextInputMethodClient`, **suppress key encoding during composition**, and position the candidate window at the cursor (§4.2).
- **The scrollbar does nothing** (#15) because the XAML `ScrollBar` is only a **one-way, read-only display**. Actual scrolling occurs inside the control through the wheel-driven `_scrollOffset`, and the `Updated` event **unconditionally resets `_scrollOffset` to zero**, so any background output sends you back to the bottom. The solution is a **viewport/scroll model + `IScrollable`** (§4.1.4).
- **SFTP (#22) is already implemented in the core layer** (`SftpService`/`TransferManager` are complete), but it was **not registered in DI and the VM was given null**. This is a wiring and UI task, not a ground-up implementation (§4.8).
- **The UI must be rebuilt from the design file** (#26): `VelaShell.Controls` currently has only 1 control plus a token dictionary. It needs to become a real **custom control library**, aligned frame by frame with the design file (§4.9 / §6).

Priority: **P0 = rendering pipeline + lifecycle decoupling + IME + scrolling** (directly eliminates the lag/crashes you reported); **P1 = SFTP wiring + custom control library rebuild + command registry/overlay manager**; **P2 = advanced panels and the full settings experience**.

---

## 1. Current-State Inventory (Honest Layering)

| Capability | Status | Key files |
| ------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| VT parsing/emulation/screen buffer/scrollback history | ✅ Complete (custom implementation, not a wrapper) | `Terminal/Emulation/{VtParser,TerminalEmulator,TerminalScreen}.cs` |
| Custom-rendered terminal control | ✅ Usable, but has **structural performance and scrolling problems** | `Terminal/Rendering/VelaTerminalControl.cs` |
| SSH connection + shell I/O (async) | ✅ | `Infrastructure/Ssh/*`, `Terminal/SshTerminalBridge.cs` |
| Password / private-key authentication | ✅ | `Infrastructure/DependencyInjection/*` |
| Split panes / tab docking (custom VelaDock, floating windows disabled by product decision) | ✅ | `App/Docking/*` (Model + Controls), `App/Controls/ReparentingHost.cs` |
| dark/light/system themes (token dictionary) | ✅ Scaffolding exists, but **real-time system following and runtime accent-color overrides are missing** | `Core/Services/ThemeService.cs`, `App/App.axaml(.cs)`, `Controls/Themes/*` |
| i18n (five languages: en default + zh-Hans/zh-Hant/ja/ko satellite resources, 938 keys fully covered, real-time switching) | ✅ Completed on 2026-07-12: all UI copy extracted across the repository (~900 locations), axaml uses `{loc:Localize}`, C# uses `Strings.Get/Format`; key-set parity and fallback-chain tests provide coverage | `Core/Resources/Strings*.resx`, `Core/Localization/*`, `App/Localization/LocalizeExtension.cs` |
| Command palette Ctrl+P/K | ✅ (but command sources are scattered, with no unified registry) | `App/ViewModels/CommandPalette*.cs` |
| Port-forwarding tunnels (service layer) | ✅ | `Infrastructure/Tunnels/TunnelService.cs` |
| SonnetDB embedded persistence (sessions/settings/known_hosts/connection history/audit) | ✅ | `Core/Data/*`, `Infrastructure/Persistence/SonnetDb*` (legacy JSON is automatically imported on first run) |
| **SFTP + transfer queue** | ✅ Wired: browser, transfer overlay, bidirectional conflict policies (overwrite/skip/rename/ask), concurrency/throttling/logging all work | `Core/Sftp/*`, `App/ViewModels/FileBrowserViewModel.cs` |
| Localization / host trust / shortcut services | ✅ Registered in DI | `App/App.axaml.cs`, `Infrastructure/DependencyInjection/*` |
| Update service (custom portable self-updater) | ✅ Integrated (GitHub Releases `latest.json` manifest + SHA-256 verification + external process replaces the version and restarts after exit, automatic rollback on failure; Velopack removed on 2026-07-17) | `App/Services/UpdateService.cs`, `App/Services/Update/*` |
| Semantic highlighting / URL and error recognition (#9) | ✅ Implemented | `Terminal/Semantics/SemanticMatcher.cs` |
| Unified overlay/floating-panel management (§17) | ✅ Not implemented (each panel is independent) | — |
| Reconnect using the same session tab (#19) | ✅ Only the `ReconnectAttempts` field exists; no driving logic | `App/ViewModels/TerminalTabViewModel.cs` |

**Two technical-debt items to clean up**:

1. **Duplicate input paths**: `TerminalTabView.axaml.cs` has its own `OnKeyDown/OnTextInput/copy` implementation that overlaps with the one inside `VelaTerminalControl`. **Consolidate everything into the control**. The code-behind should retain only forwarding global shortcuts to the command system.
2. **`ScrollbackBuffer.cs`/`TerminalLine.cs`/`SearchMatch.cs`** are legacy shells (`new ScrollbackBuffer(1)` is used as a placeholder in the control). Terminal search (#8/§5.3) should be rewritten on the new viewport model.

---

## 2. Target Runtime and Solution

### 2.1 .NET 11 Notes (Your Requirement)

> ✅ **This section has been implemented (2026-07)**: the repository has switched to **`net11.0`**. `Directory.Build.props` sets `<TargetFramework>net11.0</TargetFramework>` centrally (one location across the repository, so individual csproj files do not need editing), `global.json` pins `11.0.0` with `rollForward: latestFeature`, and the actual build uses `11.0.100-preview.x`. `EnablePreviewFeatures` and `Features=runtime-async=on` are enabled for net11, with `CA2252`/`SYSLIB5007` suppressed through `NoWarn`. Avalonia 12.1.0 builds successfully on it. The following text is retained as a record of the decision at that time.

Original record: `11.0.100-preview.5` was installed in the environment alongside `10.0.301`; at that time, the repository was pinned to `net10.0` through `global.json`. Targeting **`net11.0`** requires:

1. Pointing `global.json`'s `sdk.version` to `11.0.100-preview.x` (while retaining `rollForward: latestFeature`);
2. Changing `<TargetFramework>net10.0</TargetFramework>` to `net11.0` in the 6 src `.csproj` files (and synchronizing the test projects);
3. Verifying that **Avalonia 12.1.0** builds and runs correctly under the net11 preview (Avalonia 12 targets net8+ and is usually compatible, but the preview SDK requires one real-device smoke run plus the full test suite).

> ⚠️ **Decision note**: net11 is currently a **preview**, while net10 is **LTS**. For production use or releases, keeping net10 (LTS) on the main branch and validating net11 on a separate branch is recommended. For a personal or cutting-edge project, moving directly to net11 is fine. The choice is yours; this blueprint defaults to net11 as requested while retaining “revert to net10” as a one-line change.

### 2.2 Technology Stack (Keep the Current Stack to Avoid Unnecessary Migration)

Avalonia 12.1.0 · ReactiveUI 23 / ReactiveUI.Avalonia 12 · **custom VelaDock docking** (`App/Docking/`, replaced Dock.Avalonia, see `docs/dock-replacement-plan.md`) · **Tmds.Ssh 0.23.0** (fully managed async-first SSH library, replaced SSH.NET; ProxyJump uses its native `SshProxy` chain) · Microsoft.Extensions.DependencyInjection · System.Text.Json · **SonnetDB.Core 3.0.1 (embedded multi-model database, the sole persistence engine)** · custom portable self-updater (`App/Services/Update/`, Velopack removed) · MSTest. **Do not add large dependencies**. Icons use **Avalonia's built-in `PathIcon` + Fluent/lucide geometry** (your #24).

---

## 3. Layering and Dependency Direction (After Consolidation)

```
                         ┌───────────────────────────────┐
                         │  VelaShell    (WinExe)        │  Composition root/DI, window shell, Dock,
                         │  Views(axaml) + App-level VM  │  overlay host, command bindings
                         └───────────────────────────────┘
             ┌───────────────┬───────────────┬───────────────┐
             ▼               ▼               ▼               ▼
   ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────────┐
   │ Presentation │ │  Controls    │ │  Terminal    │ │ Infrastructure   │
   │ Cross-layer  │ │ Custom       │ │ VT engine +  │ │ SSH/SFTP/tunnels/│
   │ VM/workflows │ │ controls +   │ │ rendering +  │ │ storage impl. +  │
   │              │ │ token/icons  │ │ input + pipe │ │ DI               │
   └──────────────┘ └──────────────┘ └──────────────┘ └──────────────────┘
             └───────────────┴───────┬───────┴───────────────┘
                                     ▼
                         ┌───────────────────────────────┐
                         │  VelaShell.Core (leaf)         │  Domain models, contract interfaces,
                         │  models/contracts/services/    │  pure logic services
                         │  i18n/persistence              │  (no Avalonia dependency)
                         └───────────────────────────────┘
```

**Hard rules**

- `Core` must not reference Avalonia or any upper layer (to remain testable and reusable).
- `Terminal` depends only on `Core` (not on Presentation/App).
- `Infrastructure` depends only on `Core` (`Tmds.Ssh` appears only here; library exceptions are translated in one place, `TmdsSshInterop`, into the `Core` `VelaSsh*Exception` family; upper layers recognize only neutral exception types).
  > Historical lesson (fixed on 2026-07-22): during migration, `MainWindowViewModel` continued dispatching based on old SSH.NET type **name strings** (`ex.GetType().Name == "SshAuthenticationException"`). The actual translated type was `VelaSshAuthenticationException`, so **no branch matched**. Authentication-failure retries did not trigger, and all error messages fell through to the fallback copy, without any compiler error. It now matches `VelaSsh*Exception` types directly. **Never use type-name strings to identify exceptions across layers**. The neutral exception family in Core exists for this purpose, and App can reference it directly.
- `Controls` depends only on Avalonia + `Core` (shared UI contracts), **not on App**. Runtime theme and color changes can therefore be handled by changing only the token dictionary.
- **All cross-layer interaction goes through interfaces defined in `Core`**. Implementations live in `Infrastructure`/`App` and are assembled through DI.

---

## 4. Cross-Cutting Core Subsystems (Engineering Focus)

The following 10 subsystems are the “meat” of this architecture. Each identifies its **owning project / addressed requirements / key interfaces / implementation details**.

### 4.1 Terminal Rendering and Output Pipeline ★Core (Requirements #5, #16, #10, #15)

Owner: `VelaShell.Terminal`. This is the key to eliminating lag, and it is divided into four pipeline stages:

```
ShellStream(background read)  →  [1 output channel/batching pump]  →  TerminalEmulator.Feed(batch)
                                                                      │ Updated
                                                                      ▼
                                         [2 frame scheduler, max 60 fps]  →  RequestFrame
                                                                      │
                                                                      ▼
                              [3 glyph/line cache + dirty-line rendering]  →  DrawingContext
                                                                      ▲
                                         [4 viewport/scroll model]  ── provides visible area and offset
```

**[1] Output batching pump (solves “redraw once per read”)**

- The `SshTerminalBridge` read loop **no longer** calls `InvokeAsync(Feed)` for every 4 KB block. Instead, it writes bytes to a `System.Threading.Channels.Channel<byte[]>` (or a ring buffer).
- A **batching pump** on the UI thread (`DispatcherTimer` or a self-pumping `Dispatcher.UIThread.Post`) concatenates **all accumulated blocks** in the channel into one `Feed` call at each frame boundary. In high-throughput scenarios (`apt upgrade`, `cat` of a large file, progress-bar output), this reduces “one cross-thread hop plus one full-screen redraw per 4 KB” to “one per frame”.
- Benefit: `\r` in-place progress-bar redraws from `apt`/`yum` (your #10) become naturally smooth after batching, and screen output can no longer saturate the UI thread.

**[2] Frame scheduler (solves “unlimited frames / jitter”)**

- Change `Updated → InvalidateVisual` to `Updated → mark dirty + request the next frame`. Use fixed throttling (60 fps by default, adjustable in settings) to merge multiple dirty signals. When idle, perform zero redraws.
- Cursor blinking uses a **separate low-frequency timer**. It redraws only the cursor row rather than the entire screen.

**[3] Glyph/line cache + dirty-line rendering (solves “new FormattedText per cell”, the biggest performance killer)**

- The current `Render` creates `new FormattedText(...)` for every cell. **Replace it** with `GlyphRun`/cached `TextLayout` instances built from `(glyph string, foreground, style)` segments, and cache layout results per line. Reuse the cached result when the line content has not changed.
- Add a **dirty flag** to `TerminalRow` (set when the emulator writes to it). `Render` relayouts and redraws only dirty lines; unchanged lines use cached bitmaps or geometry.
- Under the fixed-width assumption, go further with “style-segment run merging”: merge consecutive cells with the same foreground and flags into one run. The number of draw calls per line drops from O(column count) to O(style-segment count).
- CJK double-width handling is already implemented through `CharWidth`/`IsWideTrailing`; retain it. The cache key must include flags (bold/italic/underline).

**[4] Viewport/scroll model (solves the ineffective scrollbar in #15)**

- Add `TerminalViewport`, exposing `TotalRows` / `ViewportRows` / `ScrollOffset` (readable, writable, and bindable).
- `VelaTerminalControl` implements Avalonia `IScrollable` (or `TerminalTabView` hosts a real two-way-bound `ScrollBar`). Dragging the scrollbar must drive `ScrollOffset`, rather than merely displaying it as it does now.
- **Key fix**: `OnEmulatorUpdated` must no longer unconditionally set `_scrollOffset = 0`. Change it to “**follow the bottom only when already at the bottom**”. Background output must **not interrupt** users who have scrolled up to inspect history.
- Scrollback capacity: raise `MaxScrollback` from 10,000 to **a configurable value (50,000 recommended by default, adjustable in settings)**. Do not count alt-screen content as history; the current behavior is correct.

**Acceptance signals**: `cat` of a file with tens of thousands of lines does not drop frames; CPU usage remains low during persistent `htop`/`top` refreshes; output does not pull the user back to the bottom while viewing history; dragging the scrollbar browses all history.

### 4.2 Input Pipeline and IME (Requirements #14 Chinese Input, #20 Copy/Paste Keys, #8 Input Experience)

Owner: `VelaShell.Terminal` (control layer).

- **IME composition state (root cause of the #14 htop F3 crash)**: `VelaTerminalControl` implements Avalonia's `Avalonia.Input.TextInput.TextInputMethodClient` and exposes the cursor rectangle to the framework, positioning the candidate window at the terminal cursor. **Core fix**: maintain an `IsComposing` state. **While composition is active, `OnKeyDown` must not call `InputEncoder.Encode`**. Arrow keys, Enter, and ESC select candidates and must not leak to the PTY. Once composition is committed, `OnTextInput` sends the UTF-8 text in one operation. Chinese input in the htop search box will then no longer send ESC and cause htop to exit.
- **Copy/paste (#20)**: The current implementation already has `Ctrl+Shift+C/V`, right-click paste, and bracketed paste. **Add `Shift+Insert` paste** and **“copy on selection”**. When a selection is released in `OnPointerReleased`, automatically write it to the clipboard according to a setting, satisfying your #8 requirement for “copy on selection instead of Ctrl+C”. `Ctrl+C` continues to send the interrupt signal (SIGINT).
- **Multiline paste (#8)**: Bracketed paste mode is already supported. For non-bracketed mode, normalize `\r\n → \r` (already done), and show one confirmation prompt for very large pastes to prevent accidental insertion.
- **Consolidate the duplicate paths**: delete the duplicate input and selection logic from `TerminalTabView.axaml.cs` and route everything through the control. The code-behind only forwards **global shortcuts not consumed by the terminal** to the command system (§4.6).

### 4.3 Session Lifecycle and Connection Orchestration (Requirements #17 Non-Blocking Connections, #18 Instant Close, #19 Reconnect in the Original Tab)

Owner: contracts in `Core`, orchestration in `Presentation`/`App`. **Decouple “tab/VM/screen buffer” from “transport layer (`SshClient`/`ShellStream`/`Bridge`)”.**

Introduce a `TerminalSession` state machine (`Core.Models`):

```
Idle → Connecting → Connected → Disconnected → (Reconnecting → Connected)
                 ↘ Failed ↗
```

- **#17 Non-blocking connection**: after a double-click on `ConnectProfileAsync`, **create the tab immediately and enter the `Connecting` state**. The terminal area shows a “Connecting” overlay. The SSH connection and `CreateShellStream` run entirely on a **background thread**. Once successful, attach the `ShellStream` to the existing emulator and transition to `Connected`. The UI thread never blocks; a 10-second timeout affects only that tab's overlay.
- **#18 Instant close**: when closing a tab, synchronously do only “remove it from Dock/TabBar + make it visually disappear”. Put `Bridge.Dispose()`/`ShellStream.Dispose()` into an **`ISessionTeardownQueue` (background single-threaded queue)**. Remove `_readTask.Wait(2s)` from the current `Dispose()` implementation and replace it with a timed background wait. The user sees the tab close instantly while disconnection completes in the background.
- **#19 Reconnect in the original tab**: retain the emulator's screen buffer and the tab in the `Disconnected` state, and show a disconnected overlay (§7 `ZufZw`). Bind **Enter / `Ctrl+R`** to `ReconnectCommand`, reusing the same `TerminalSession`/emulator/tab and rebuilding only `ShellStream + SshTerminalBridge`. EOF caused by `exit`/`reboot` (`bytesRead==0` in the read loop) should become a `Disconnected` event rather than immediately destroying the tab. Support an exponential-backoff automatic-reconnect switch. The `ReconnectAttempts` field already exists; it only needs to be connected to the driver.

### 4.4 Themes and Accent Colors (Requirements #2 Dark/Light, #3 System Following + Real-Time Accent Colors)

Owner: `Core` (service) + `App` (Avalonia binding) + `Controls` (tokens).

- The token dictionaries already exist (`Controls/Themes/{VelaTokens,VelaShellTokens}.axaml` + `App/Themes/{Dark,Light}Theme.axaml`) and everything uses `DynamicResource`, so the foundation for runtime theme switching is in place.
- **Real-time system following (#3 gap)**: in `System` mode, have `ThemeService` subscribe to `TopLevel.PlatformSettings.ColorValuesChanged` (Avalonia provides OS light/dark change notifications). When the OS changes, set `Application.RequestedThemeVariant` to `Default` and let Fluent follow, with **no restart required**.
- **Runtime accent-color override (#3)**: add a separate “accent layer”. Extract `accent`/`accent-dim`/`accent-text` from the theme dictionary into **user-overridable resources**. When the user changes the color in settings, replace these keys in `Application.Resources`; every `DynamicResource` reference refreshes immediately. Synchronize the corresponding accent color in the terminal palette (`TerminalPalette`).
- The authoritative design-token values come from `VelaShell-zh.pen` (the token table in this document's appendix). **Do not hard-code colors** (your specification §1).

### 4.5 Internationalization (Requirements #4 Multilingual Support, Real-Time Switching)

Owner: `Core` (service/resources) + `App` (markup extension).

- Retain satellite resx assemblies (EN + zh-CN). `LocalizationService` already implements `SetLanguage` (sets `CurrentUICulture`), so **register it in DI first**. It is currently missing.
- **Real-time switching (gap)**: `{x:Static Strings.X}` is fixed when loaded and does not change with the language. Replace it with a **`LocalizeExtension` (markup extension)** bound to `ILocalizationService`'s `CurrentLanguage` (`INotifyPropertyChanged`/Observable). When the language changes, all bound text refreshes immediately without a restart.
- **Remove hard-coded strings**: scattered Chinese such as command palette categories “Sessions/Commands” and the window-button tooltip “Minimize” should move into resx (`Menu.Session`, `Tab.Close`, and similar keys), sharing the keys with the command registry (§4.6).

### 4.6 Command Registry (Unify Menu §4A ‖ Command Palette §8 ‖ Shortcuts §16)

Owner: `Presentation` (registry) + `App` (binding). Specification §4A.1 explicitly requires the “menu items / command palette / shortcuts” to share one command registry.

- Add `ICommandRegistry`: each command = `{ Id, localized title key, icon (PathIcon key), default shortcut, CanExecute, Execute, group, context (Global/Terminal/File) }`.
- **One source of truth** drives three consumers:
  - The **menu bar** (§4A.1 Sessions/Edit/Actions/Search/Tools/Help) renders groups from the registry;
  - The **command palette** (§8) performs fuzzy subsequence search over the registry (the existing `CommandPaletteViewModel` is changed to read from the registry);
  - **Shortcuts** (§16) are mapped to command IDs by the existing `KeyboardShortcutService`, which must be added to DI.
- The terminal toolbar's no-op commands (`Search/Copy/Split/Broadcast/Tunnel/QuickCommands`) should point to **registry commands**, so one implementation works everywhere.

### 4.7 Overlay / Floating-Panel Manager (Specification §17 Shared Constraints)

Owner: `App` (host) + `Presentation` (VM). Specification §17 defines shared rules for modality, singletons, boundary avoidance, position memory, and theme coupling. A central service is required to enforce them, rather than having every panel implement its own version.

Add `IOverlayService`:

- **Three hierarchy levels**: modal dialogs (settings/new connection/password, centered with backdrop), quasi-modal overlays (command palette, backdrop plus Esc), and non-modal floating panels (file transfer/tunnels/resource monitor, no backdrop and able to coexist with the main interface).
- **Singleton focus**: command palette, tunnels, resource monitor, and file-transfer components are global singletons. Repeated activation focuses the existing instance.
- **Boundary avoidance**: overlays following the mouse or an anchor (resource monitor §11, tunnel §10) automatically flip direction when they would leave the screen.
- **Position memory**: persist the dragged positions of the file-transfer component and tunnel panel in `AppState`.
- **Theme coupling**: overlays follow the global light/dark theme because they all use `DynamicResource`, satisfying this requirement naturally.
- **Hover timer** (§4B.4/§11): hovering over a tab name for more than 400 ms opens the resource monitor, and a 150 ms debounce controls fade-out. Implement this centrally to avoid duplication.

### 4.8 SFTP and File Transfer (Requirement #22, Specification §6 File Browser / §9 Transfer Component)

Owner: service in `Core` (**already implemented**) + transfer orchestration in `Presentation` + UI in `App`/`Controls`.

- **Wiring (✅ complete)**: `SftpService` is registered in DI, and both `FileBrowserViewModel`/`FileTransferViewModel` receive an instance. The SFTP channel reuses the credentials from the connected SSH session.
- **Conflict policy (✅ bidirectional implementation complete)**: Settings → File Transfer → “When a file already exists” (ask/overwrite/skip/rename) applies in both directions. Before downloading, check for a same-named local file; before uploading, use `ExistsAsync` to stat a same-named remote file. “Ask” opens a confirmation dialog (overwrite or skip this file), and “Rename” selects the first available name in the form `file (1).txt`. Under “Overwrite”, uploads skip the extra stat and rely directly on SFTP overwrite semantics, saving one round trip. Saving through the built-in or external editor intentionally overwrites and does not use conflict checking.
- **File browser (§6)**: a collapsible lower panel in the right area, with breadcrumb path, columns (filename/size/permissions/time), sorting, context menu (download/upload/rename/delete/chmod/copy path/new), and double-click navigation into directories or downloads. The first row, `..`, returns to the parent directory.
- **Transfer queue (§9)**: `TransferManager` already supports a concurrency limit plus progress/speed/ETA. The UI adds a **floating transfer component in the upper-right**. It appears only while tasks are active, with a draggable handle in the header (using §4.7 position memory), vertically stacked multiple tasks, and a count badge. After all tasks finish, it **fades out automatically** after about 3 seconds. Failures are shown in red with retry.
- **Threads**: all transfers run on background threads (`TransferManager` already works this way), and progress callbacks are marshaled back to the UI through `IProgress<TransferProgress>`, satisfying your #5 requirement that the interface remain responsive during background transfers. When a session disconnects, transfers pause (§17.7).
- Drag-and-drop upload/download (§6): dropping a local file into the application uploads it; dragging a list item to the local system downloads it.

### 4.9 Custom Control System and Icons (Requirement #1 Many Custom Controls, #24 Icons, #25/#26 Strict Design-File Alignment)

Owner: `VelaShell.Controls` (turn it into a real design-system library). Specification §26 explicitly says the existing UI is an initial version and **must be rebuilt with custom controls based on the design file**, rather than using Avalonia's default controls.

- `Controls` currently contains only `LucideIcon` plus token/icon dictionaries (the early `StatusMetricChip` has been removed; the status bar is composed by `StatusBarView` inside App). **Expand it into a control catalog** using templated `TemplatedControl` types, with `Themes/*.axaml` holding `ControlTemplate` definitions. Every control must match the design-file tokens, dimensions, corner radii, and spacing exactly (specification §1.3). See §6 for the control list.
- **Icons (#24)**: standardize on `PathIcon` plus an `Icons.axaml` resource dictionary containing `StreamGeometry` values. The design uses **lucide**. Put the used lucide icon paths into the dictionary with keys such as `Icon.Plus`, `Icon.Route`, and `Icon.Search`, then use `<PathIcon Data="{StaticResource Icon.Route}"/>` in controls. Sizes are 11–16 (specification §1.3).
- **Fonts**: `font-mono = JetBrains Mono` (terminal/hostname/port/path/shortcut/tab name), `font-ui = Inter` (menus/buttons/descriptions). JetBrains Mono should be **embedded** and configured with CJK fallbacks (`Microsoft YaHei`/`Noto Sans CJK`) to fix the issue of an unspecified Chinese fallback font (plan.md §10).
- **Dark first**: specification §25 says dark mode is the reference and light mode is only approximate. Polish dark-mode control templates first, then let light mode follow through token overrides.

### 4.10 Semantic Highlighting / Link and Error Recognition (Requirement #9)

Owner: `VelaShell.Terminal` (rendering augmentation layer).

- Add `ISemanticMatcher`: run a set of regular expressions/rules against **committed line text** (not individual bytes) and produce “span + type (URL/error/warning/path/IP)”. Default rules: URLs (`https?://…`), error keywords (`error/failed/fatal/panic`), and warnings (`warn`).
- The rendering layer overlays **semantic styles** on top of the line cache from §4.1[3] (underline URLs and use `info` blue, `error` red, and so on; colors come from tokens). URLs are clickable. Existing `OSC 8` hyperlinks are already parsed, while rule matching serves as a fallback.
- Rules are **configurable** (settings toggle/custom regex), remain decoupled from the terminal buffer, and do not pollute VT state.

### 4.11 Persistence and Security

- **Use SonnetDB (embedded) throughout** (https://github.com/IoTSharp/SonnetDB, `SonnetDB.Core`, data directory `%LocalAppData%/VelaShell/sonnetdb`), replacing the former JSON/LiteDB approach. Legacy JSON files are imported once on first run. Interfaces live in `Core`, implementations in `Infrastructure/Persistence/SonnetDb*`, and the shared singleton `SonnetDbEngine` flushes the WAL on exit through `Dispose`.
  - **Document collections** (JSON documents, business/configuration data): `session_groups`, `session_profiles` (`$.groupId` index), `app_config` (settings/state/sync documents, where sync is Gist cloud-sync configuration and tokens are encrypted through `ISecretProtector`), `known_hosts`, `ui_config` (UI configuration), `quick_commands`, `tunnels` (one tunnel configuration document per server), and `recordings` (session-recording metadata).
  - **Time-series measurements** (time-related data): `conn_history` (connection history, tags: profile_id/host/username, fields: name/group_name/port/success/duration_ms, supporting the sidebar's “Recent Connections”), `audit_log` (audit, tags: category/action/profile_id, fields: detail), and `session_recording_chunks` (**session recording is implemented**, 2026-07-12: tag `recording_id`, fields `offset_ms/data-Base64`, time = recording start + offset; the replay center replays along the offset timeline).
- **Persist Dock layout**: store VelaDock's layout tree (`DockWorkspace.Root`, a pure INPC model that is naturally serializable) in the `app_config`/`ui_config` document collections, restoring split panes/tabs after restart (plan.md task).
- **Password encryption (implemented)**: `ISecretProtector` (`Core` interface) + `AesSecretProtector` (AES-256-GCM, key file `%LocalAppData%/VelaShell/secret.key`). Session passwords/private-key passphrases are encrypted before being written to SonnetDB. Legacy plaintext is supported on read and automatically encrypted on the next save. Credentials are not persisted when “Remember password” is unchecked.

---

## 5. Defect Root Cause → Architecture Fix Matrix (Your #14–#21, Item by Item)

| # | Symptom | Root cause (located in the code) | Architecture-level fix | Subsystem |
| --- | ------------------------------------- | --------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- | ----------------- |
| 14a | Entering Chinese with F3 in htop causes htop to exit | IME composition state is not handled; during composition `OnKeyDown` still encodes ESC/arrow keys/Enter and sends them to the PTY | Implement `TextInputMethodClient`; suppress key encoding while `IsComposing` | §4.2 |
| 14b | After closing nano, the cursor remains on old content instead of returning to the end | Cursor DECRC restoration / viewport-offset reset is incorrect when leaving alt-screen (DECSET 1049) | Audit alt-screen switching + DECSC/DECRC in `TerminalScreen`/`TerminalEmulator`, plus `_scrollOffset=0` on exit | §4.1 / targeted engine fix |
| 15 | Scrollbar does nothing; history is inaccessible | XAML `ScrollBar` is read-only display; `Updated` unconditionally sets `_scrollOffset=0` | Two-way viewport/scroll model + `IScrollable`; follow the bottom only when already at the bottom | §4.1[4] |
| 15b | The history buffer should be larger | `MaxScrollback` defaults to 10,000 | Make it configurable (50,000 by default), with a settings item | §4.1[4] |
| 16 | Low frame rate and lag | One cross-thread `InvokeAsync` per read + one `new FormattedText` per cell + full-screen redraw | Batching pump + frame-limit scheduler + glyph/dirty-line cache | §4.1[1][2][3] |
| 17 | After a fast double-click, the connection remains “Connecting” for a long time and looks hung | Connection/stream creation is not decoupled from tab creation; the UI is not visible until the connection succeeds | Create the tab immediately in `Connecting`, with the connection entirely on a background thread | §4.3 |
| 18 | Closing a tab lags | `Bridge.Dispose()` calls `_readTask.Wait(2s)` on the UI thread and then synchronously calls `ShellStream.Dispose()` | Remove the view synchronously and put teardown on a background queue | §4.3 |
| 19 | Cannot reconnect in the original tab after `exit`/`reboot` | The tab is destroyed immediately on disconnect, with no reconnect path | Decouple the session state machine from the transport layer; rebuild the stream through Enter/`Ctrl+R` while reusing the tab | §4.3 |
| 20 | Need `Ctrl+Shift+C/V` + `Shift+Insert` | `Ctrl+Shift+C/V` exists; `Shift+Insert` and copy-on-selection are missing | Add `Shift+Insert` paste and automatic copy on selection release in the control | §4.2 |
| 21 | Font-size/font-family controls and settings are incomplete | `FontSize/FontFamily` can be set but are not connected through the settings or Ctrl+wheel zoom flow | Add font/family to the settings service for real-time application + Ctrl+wheel zoom | §4.9 / Settings |

---

## 6. Control Catalog: Design Frames → Custom-Control Mapping (Specification §26 Rebuild Basis)

> Put each control in `VelaShell.Controls`, using design tokens strictly in its template. Put VMs in `Presentation`/`App`. Use `PathIcon` for icons.

| Design frame | Control (Controls) | Description |
| ---------------------------------------- | ---------------------------------------------------------------------- | ---------------------------------------------------------- |
| Main interface `CsTjc` | `MainShellView` (App) | Under the native title bar: menu bar → (sidebar ‖ right area) → status bar |
| Sidebar `aMaSq` | `SessionTreePanel` / `SessionTreeItem` / `QuickConnectBox` / `UserBar` | Group tree + quick connect + bottom user bar (§3) |
| Menu bar `TSiDh` | `MenuBar` + `MenuBarActions` (`GQQwj`) | Text menus (registry-driven) + global function buttons on the right (§4A) |
| Tab bar `nunbT` | `TerminalTabStrip` / `TerminalTab` / `TabOverflowControls` (`pZGS4`) | Overflow scrolling `◀▶`, `▾` dropdown, drag reordering, hover-triggered resource panel (§4B) |
| Terminal toolbar `BdPtF` | `TerminalInfoBar` | Read-only session information (root@host, uptime, latency) |
| Terminal canvas `QzoMC` | `VelaTerminalControl` (Terminal, already exists, to be revised per §4.1) | Custom-rendered VT |
| Disconnected state `ZufZw` | `DisconnectedOverlay` | Disconnection message + reconnect button + automatic reconnect switch (§4.3/§7) |
| File area `dyuii` | `SftpBrowserPanel` / `FileRow` / `PathBreadcrumb` | SFTP browser (§6/§4.8) |
| Status bar `gzmsb` | `StatusBarView` (App, already exists; the early `StatusMetricChip` control has been removed) | Connection/latency/size/encoding/lightweight metrics (§7) |
| Command palette `FN5dM` | `CommandPalette` (already exists, to be registry-driven) | Ctrl+P/K (§8) |
| File transfer `9Ralg` | `TransferToast` / `TransferRow` | Floating upper-right, draggable, auto-dismissed (§9/§4.7) |
| Tunnel panel `fuXS7` | `TunnelPanel` / `TunnelRow` / `TunnelForm` | Local/remote/dynamic forwarding (§10) |
| Resource monitor `EP3Gd` | `ResourceMonitorFlyout` | Opens after 400 ms hover, with boundary avoidance (§11/§4.7) |
| Session context menu `e6klM` | `SessionContextMenu` | §12 |
| New connection `oAHna` | `NewConnectionDialog` (SSH/SFTP/Telnet/serial tabs) | §13.1 |
| Password verification `oNZIM`/`twD13` | `AuthDialog` (two-step + host-fingerprint confirmation) | §13.2 |
| Settings pages | `SettingsWindow` + left navigation + individual pages | General/Appearance/Terminal/Shortcuts/File Transfer/Keys/Audit/Snippets/About (§14) |
| Advanced panels `bR5c4`/`gPWeC`/`NceE6`/`RGXg1` | Operations orchestration / host trust / recording replay / connection diagnostics | §15 (P2) |

---

## 7. Threading Model Overview (Requirement #5)

| Thread/context | Responsibility | Prohibited |
| --------------------------------------------- | -------------------------------------------------- | ---------------------------------------------------------------------------- |
| **UI thread (Dispatcher)** | Rendering, input, layout, VM binding, `emulator.Feed` (after batching) | ❌ Any synchronous network/disk wait; ❌ `Task.Wait`/`.Result`; ❌ `ShellStream.Dispose` |
| **SSH read thread** (one `Task` per session) | `ShellStream.ReadAsync` → write to Channel | ❌ Directly touching Avalonia objects |
| **SSH write** (fire-and-forget async) | User-input `WriteAsync` | — |
| **Transfer thread pool** (`TransferManager`, concurrency limit) | SFTP upload/download, with progress marshaled back to UI through `IProgress` | — |
| **Teardown queue** (background single thread) | Dispose Bridge/ShellStream/Client (§4.3 #18) | ❌ Blocking the UI |
| **Batching pump/frame scheduler** (UI-thread timer) | Accumulate blocks, merge Feed calls, and limit redraws | — |

Principle: **the UI thread performs only “fast and deterministic” work**. Anything that might block (connection, disconnection, transfer, DNS, handshake) runs in the background, while the UI reflects progress through state machines and overlays.

---

## 8. Migration and Refactoring Roadmap (Requirement #23, Do Not Break Existing Features)

Strictly follow “small steps + green tests throughout”, running `dotnet test` at every step (currently 359 passing).

**P0, directly eliminate the lag/crashes you reported (highest return)**

1. Rendering performance: glyph/dirty-line cache + batching pump + frame-limit scheduler (§4.1[1][2][3]). → Fixes #16, #10.
2. Viewport/scroll model + real scrollbar + no unconditional follow-bottom (§4.1[4]). → Fixes #15.
3. Decouple session lifecycle: create the tab immediately + background connection + background teardown queue (§4.3). → Fixes #17, #18.
4. IME composition-state handling (§4.2). → Fixes #14a.
5. Reconnect using the same tab + Enter/`Ctrl+R` (§4.3). → Fixes #19.
6. Complete input: Shift+Insert, copy on selection, consolidate duplicate paths (§4.2). → Fixes #20.
7. Targeted alt-screen/DECRC audit to fix the nano cursor (§4.1). → Fixes #14b.

**P1, wire completed capabilities and rebuild the UI from the design file**

8. DI wiring: register `SftpService`/`TransferManager`/`LocalizationService`/`HostKeyService`/`KeyboardShortcutService`/`UpdateService` (remove null injection).
9. Command registry (§4.6) + overlay manager (§4.7).
10. Expand `VelaShell.Controls` into a control library + `Icons.axaml` (§4.9). Rebuild frame by frame according to §6: menu bar §4A / tab bar §4B / sidebar §3 / status bar §7.
11. SFTP file browser §6 + floating transfer component §9 (§4.8).
12. Theme “real-time system following + runtime accent-color override” (§4.4); real-time i18n switching through `LocalizeExtension` (§4.5).
13. Complete font-size/font-family settings end to end + Ctrl+wheel zoom (§4.9).

**P2, advanced capabilities**

14. Full settings experience (Appearance/Shortcuts/File Transfer/About/Audit/Keys/Snippets, §14).
15. Complete the tunnel-panel §10 UI + resource-monitor hover panel §11.
16. Semantic highlighting/URL recognition (§4.10, #9).
17. Advanced panels §15 (✅ session recording and replay `NceE6` completed on 2026-07-12 using SonnetDB time-series measurements; ✅ host trust implemented under “Security Audit → Trusted Hosts”; operations orchestration/connection diagnostics remain); Dock layout persistence (password encryption `ISecretProtector` completed).
18. ✅ net10→net11 switch completed (§2.1); a full cross-platform smoke pass on real devices is still required.

---

## 9. Target Directory/Project Structure (Incremental, No Rewrite)

```
src/
├─ VelaShell.Core/            # Leaf: models/contracts/pure logic services
│  ├─ Commands/               # ★New ICommandRegistry contract, CommandDescriptor
│  ├─ Localization/           # ILocalizationService (already exists, add to DI)
│  ├─ Security/               # ★New ISecretProtector contract
│  ├─ Models/ Data/ Ssh/ Sftp/ Tunnels/ Services/ Resources/  # Retain current structure
│
├─ VelaShell.Terminal/        # VT engine + rendering + input
│  ├─ Emulation/              # Engine (current; targeted alt-screen/DECRC fixes)
│  ├─ Rendering/              # VelaTerminalControl + ★GlyphCache/DirtyRows/Viewport
│  ├─ Input/                  # ★IME(TextInputMethodClient), InputEncoder (move here)
│  ├─ Pipeline/               # ★Batching pump + frame scheduler
│  └─ Semantics/              # ★ISemanticMatcher (URL/error recognition, #9)
│
├─ VelaShell.Controls/        # ★Expand into design-system control library
│  ├─ Controls/               # Frame-by-frame controls (see §6 mapping table)
│  └─ Themes/                 # tokens + ControlTemplate + Icons.axaml
│
├─ VelaShell.Presentation/    # Cross-layer VM + workflows
│  ├─ Commands/               # ★Command registry implementation + command handlers
│  ├─ Overlays/               # ★IOverlayService implementation + overlay VMs
│  ├─ Sessions/               # ★TerminalSession lifecycle orchestration + teardown queue
│  └─ ViewModels/ Services/   # Retain current structure (TabBar/Sidebar/StatusBar…)
│
├─ VelaShell.Infrastructure/  # SSH/SFTP/tunnel/storage/security implementations + DI
│  ├─ Ssh/ Tunnels/ DependencyInjection/  # Current structure
│  └─ Persistence/            # SonnetDbEngine + SonnetDb* repositories, AesSecretProtector(AES-256-GCM)
│
└─ VelaShell/                 # Composition root/window shell/Dock/overlay host/command bindings
   ├─ Docking/ Behaviors/ Logging/ Services/  # Current structure
   ├─ Views/ ViewModels/      # Gradually slim down: move reusable VMs into Presentation
   └─ App.axaml(.cs)          # DI assembly (complete all service registrations)
```

---

## 10. Key Decisions and Risks

| Decision point | Recommendation | Risk/Notes |
| ---------------- | ---------------------------------------------------------- | -------------------------------------------------------------------------- |
| .NET 11 vs 10 | ✅ Switched to **net11** (effective in one location through `Directory.Build.props`); production can revert to net10 (LTS, still a one-line change) | net11 still uses a preview SDK and has `EnablePreviewFeatures`/`runtime-async` enabled; a full real-device test is required |
| Rendering rewrite vs. tuning | **Rewrite the rendering hot path** (glyph cache/dirty lines/batching), rather than making small fixes | Leave the engine/screen buffer unchanged and modify only `Rendering/`; risk is controlled and test coverage already exists |
| IME | Implement `TextInputMethodClient` | IME behavior differs by platform; verify once each on Windows/macOS/Linux |
| Duplicate input paths | Consolidate into the control and delete duplicate code-behind logic | Take care not to regress copy/selection behavior; tests provide the safety net |
| UI rebuild | Replace controls frame by frame from the design file; leave UI unchanged in P0 and fix only the core | Avoid a “big-bang rewrite” and advance the UI rebuild in parallel with core fixes |
| SFTP | Wire it first, then complete the UI (core is complete) | The `ISftpClientWrapper` mocking problem has been avoided through a wrapper |
| Password storage | ✅ `ISecretProtector` implemented (AES-256-GCM + local key file) | Legacy plaintext `sessions.json` is imported into SonnetDB and encrypted on first run; reads remain compatible with historical plaintext |
| Persistence engine | ✅ Fully adopt embedded SonnetDB (document collections + time series) | The singleton engine must `Dispose` on exit to flush the WAL; after one-time import, legacy JSON is renamed to `.migrated.bak` |

---

## 11. Explicit Non-Goals (Not Implemented in the Current Architecture, Corresponding Settings Options Removed)

| Feature | Reason |
| ------------------------- | ----------------------------------------------------------------------------------------------------- |
| Terminal ligatures | The custom renderer lays out one cell at a time; cross-character ligatures require shaping the entire line, which conflicts with grid alignment and has high cost |
| Adaptive window-title-bar color | The application uses the native system title bar, whose color is managed by the OS with the theme; the application layer has no stable control mechanism |
| System notifications (Windows Toast) | Requires AppUserModelID/notification framework; replaced with status-bar messages + system notification sounds (General page “Sound notifications”, Security Audit page “Notification sound”) |
| Custom shortcuts | Product decision (2026-07-12): the “Shortcuts” settings page is for reference display only; its entries are maintained against the real bindings |
| Redaction of recorded input | No longer needed: session recording captures only the terminal output stream, and password input has no echo by default (settings-audit R-12) |

> Still planned (since 2026-07-11, unimplemented items have been **hidden** from the settings UI rather than disabled; fields are retained): automatically check for updates/download at startup (the custom update pipeline is ready and only needs a background switch; the stable/preview update channels are integrated), master-password protection, resumable transfers/automatic resume/transfer retries/temporary-file cleanup, and automatic loading of keys into the Agent. Session recording was implemented on 2026-07-12 (§4.11 / §8-17).

---

### Appendix A: Design Tokens (dark / light, authoritative values from `.pen`)

Background `bg-page` #0A0E14/#F5F5F7 · `bg-sidebar` #0D1117/#FFFFFF · `bg-surface` #111620/#FFFFFF · `bg-terminal` #080C12/#1E1E2E · `bg-input` #151B26/#F0F0F2 · `bg-hover` #1A2233/#E8E8EC · `bg-active` #1C2A3F/#E0E7F0.
Border `border-primary` #1E2A3A/#E0E0E4 · `border-secondary` #253345/#D0D0D6.
Accent `accent` #00D4AA/#00B894 · `accent-dim` #00D4AA30/#00B89420 · `accent-text` #0A0E14/#FFFFFF.
Text `text-primary` #E0E6ED/#1A1A2E · `secondary` #8B9BB4/#6B6B80 · `tertiary` #5A6A80/#9999AA · `muted` #3D4F63/#BBBBCC.
Status (constant) `connected` #00D4AA · `connecting` #FDCB6E · `disconnected` #FF6B6B. `info` #74B9FF/#3498DB · `warning` #FDCB6E/#E67E22 · `error` #FF6B6B/#E74C3C.
Terminal ANSI `term-red` #FF6B6B · `green` #69FF94 · `yellow` #FDCB6E · `blue` #74B9FF · `magenta` #D980FA · `cyan` #00D4AA · `white` #E0E6ED.
Fonts `font-mono` = JetBrains Mono; `font-ui` = Inter.

### Appendix B: Relationship to Existing Documents

- `docs/interaction-and-ui-specs.md`: sole visual/interaction baseline (this document does not duplicate it and only cites section numbers).
- `docs/architecture.md`: early English blueprint (the layering direction is correct); this document is its **engineering v2** and takes precedence.
- `plan.md`: project progress and known-issues list; the root-cause locations in §5 of this document corroborate its §10.
