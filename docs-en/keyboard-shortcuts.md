# Keyboard Shortcuts

Every keyboard shortcut and mouse gesture VelaShell actually binds, grouped by where it applies.

## Maintenance rules (read this first)

**This document is not a hand-copied list — it is a projection of the code.**

- The single source of truth is `src/VelaShell/ViewModels/ShortcutCatalog.cs`. Both Settings → Shortcuts and this document read from it.
- **Every new or changed shortcut must be registered in `ShortcutCatalog` and added to the table below.** This is enforced, not left to memory:
  - `ShortcutCatalogTests.EveryMainWindowKeyBinding_IsListedInCatalog` scans every `KeyBinding` in `MainWindow.axaml` and fails if it is not in the catalog;
  - `ShortcutCatalogTests.Doc_ListsEveryCatalogEntry` compares the catalog against this document row by row, and prints ready-to-paste Markdown for anything missing;
  - `ShortcutCatalogTests.Catalog_HasNoUnresolvedResourceKeys` makes sure every string resolves to a translation instead of showing a raw `Sc_Xxx` key in the UI.
- New resource keys use the `Sc_` prefix and must be added to **all five `resx` files** (`Strings` / `zh-Hans` / `zh-Hant` / `ja` / `ko`), otherwise `AllCultures_HaveIdenticalKeySets` fails. Actions that also exist in the command palette reuse its `Cmd_` key so both places read identically.
- Only **real bindings** belong here. A key mentioned in a menu or tooltip but never bound must not be listed (this happened with `Ctrl+N` — hint only, no binding; see settings audit C-10).

## Where bindings live

Find the right place by scope, then come back and update the table:

| Scope | Implementation |
| --- | --- |
| Global (window level) | `Window.KeyBindings` in `src/VelaShell/Views/MainWindow.axaml` |
| Terminal context + platform differences | `src/VelaShell/Services/KeyboardShortcutService.cs` |
| Clipboard / paging / key encoding inside the terminal control | `src/VelaShell.Terminal/Input/TerminalKeyRouter.cs`, `src/VelaShell.Terminal/Emulation/InputEncoder.cs` |
| Terminal mouse gestures (selection, zoom, links) | `src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs` |
| Search bar, completion popup, disconnected-state keys | `src/VelaShell/Views/TerminalTabView.axaml.cs` |
| Command palette / file manager / process manager / editor / dialogs | each view's own `OnKeyDown` |
| Esc cancelling a dock drag | `src/VelaShell/Docking/Controls/DockDragController.cs` |
| AI assistant panel | `plugins/VelaShell.Plugin.Ai/Ui/ChatPanelView*.cs` |

## Platform differences

Global shortcuts are `Ctrl` on Windows, Linux and macOS alike — `Window.KeyBindings` has no per-platform variant.

The one difference is the **terminal context**: on macOS `KeyboardShortcutService` swaps the primary modifier to Command, so inside the terminal copy, paste, new tab, close tab and Settings also answer to `Cmd+C` / `Cmd+V` / `Cmd+T` / `Cmd+W` / `Cmd+,`. `Ctrl+C` keeps its "send interrupt" meaning on all three platforms and is never stolen by copy.

Tab switching (`Ctrl+Tab` / `Ctrl+Shift+Tab`) uses `Ctrl` everywhere, matching terminal convention.

## Reading the table

- An "Applies when" of `—` means the shortcut is unconditional.
- `Left Click` / `Right Click` / `Double Click` / `Drag` / `Wheel` / `Gutter` / `Title Bar` in the key column are mouse gestures, not keyboard keys.
- An action listed on several rows has several equivalent bindings, or variants under different conditions (scrollback paging, for instance, differs between the main and the alternate screen).

## Full table

### Global

| Action | Keys | Applies when |
| --- | --- | --- |
| New SSH Connection | `Ctrl+N` | — |
| New Tab (same as New Connection) | `Ctrl+T` | — |
| Clone Current Session | `Ctrl+Shift+N` | — |
| Open Settings | `Ctrl+,` | — |
| Command Palette | `Ctrl+K` | — |
| Command Palette (alternate) | `Ctrl+P` | — |

### Tabs &amp; Panels

| Action | Keys | Applies when |
| --- | --- | --- |
| Close Tab | `Ctrl+W` | — |
| Next Tab | `Ctrl+Tab` | — |
| Previous Tab | `Ctrl+Shift+Tab` | — |
| Toggle File Browser | `Ctrl+Shift+F` | — |
| Tunnel Manager | `Ctrl+Shift+T` | — |
| Toggle line number &amp; time gutter | `Ctrl+Shift+L` | — |

### Terminal

| Action | Keys | Applies when |
| --- | --- | --- |
| Copy | `Ctrl+Shift+C` | — |
| Paste | `Ctrl+Shift+V` | — |
| Paste (classic X11 binding) | `Shift+Insert` | — |
| Send Interrupt ^C | `Ctrl+C` | Copies instead when the selection-copy option is on and text is selected |
| Search Terminal Content | `Ctrl+F` | — |
| Jump to the next match | `Enter` | Only while the search bar is open |
| Jump to the previous match | `Shift+Enter` | Only while the search bar is open |
| Close the search bar | `Esc` | Only while the search bar is open |
| Scroll back one page | `PageUp` | Main screen with scrollback only |
| Scroll forward one page | `PageDown` | Main screen with scrollback only |
| Scroll back one page | `Shift+PageUp` | Works on the alternate screen too |
| Scroll forward one page | `Shift+PageDown` | Works on the alternate screen too |
| Delete Previous Word | `Ctrl+Backspace` | — |
| Move the cursor to the line start | `Shift+Home` | — |
| Move the cursor to the line end | `Shift+End` | — |
| Reconnect After Disconnect | `Enter` | Only while the session is disconnected |
| Reconnect After Disconnect (alternate) | `Ctrl+R` | Only while the session is disconnected |
| Close a disconnected tab | `Esc` | Only while the session is disconnected |

### Command Completion

| Action | Keys | Applies when |
| --- | --- | --- |
| Show Command Completion | `Alt+Enter` | — |
| Select the next suggestion | `Down` | Only while the suggestion popup is open |
| Select the previous suggestion | `Up` | Only while the suggestion popup is open |
| Insert the selected suggestion | `Enter` | Only while the suggestion popup is open |
| Dismiss the suggestion popup | `Esc` | Only while the suggestion popup is open |
| Fall through to the shell native completion | `Tab` | — |
| Accept the inline (ghost) suggestion | `Right` | — |
| Accept the inline (ghost) suggestion | `End` | — |

### Terminal Mouse Gestures

| Action | Keys | Applies when |
| --- | --- | --- |
| Open the link under the pointer | `Ctrl+Left Click` | — |
| Select the word | `Double Click` | Requires the matching option in Settings |
| Add another word to the selection | `Ctrl+Shift+Double Click` | — |
| Extend the selection from its anchor | `Shift+Left Click` | — |
| Rectangular (block) selection | `Alt+Drag` | — |
| Add a separate selection range | `Ctrl+Shift+Drag` | — |
| Add a separate block selection | `Ctrl+Shift+Alt+Drag` | — |
| Select text despite mouse reporting | `Shift+Drag` | Needed when the app enables mouse reporting |
| Paste with the right button | `Right Click` | Can be turned off in Settings |
| Zoom the terminal font | `Ctrl+Wheel` | — |
| Scroll the buffer five times faster | `Alt+Wheel` | — |
| Open the gutter settings menu | `Gutter+Right Click` | — |
| Collapse or expand an output block | `Gutter+Left Click` | — |

### Command Palette

| Action | Keys | Applies when |
| --- | --- | --- |
| Select the next command | `Down` | — |
| Select the previous command | `Up` | — |
| Run the selected command | `Enter` | — |
| Close the command palette | `Esc` | — |

### SFTP File Manager

| Action | Keys | Applies when |
| --- | --- | --- |
| Edit the current path | `Ctrl+L` | — |
| Go to the typed path | `Enter` | — |
| Cancel path editing | `Esc` | — |
| Open the selected file or folder | `Double Click` | — |

### File Operations

| Action | Keys | Applies when |
| --- | --- | --- |
| Save in Remote Editor | `Ctrl+S` | — |
| Close the editor | `Esc` | Remote file editor |

### Task Manager

| Action | Keys | Applies when |
| --- | --- | --- |
| Refresh the process list | `F5` | — |
| End the selected process | `Delete` | Only while the list has focus |
| Close the window | `Esc` | — |

### Dialogs and Windows

| Action | Keys | Applies when |
| --- | --- | --- |
| Cancel and close the current dialog | `Esc` | Applies to every dialog and secondary window |
| Trigger the default button | `Enter` | — |
| Maximize or restore the window | `Title Bar+Double Click` | — |
| Cancel the dock drag in progress | `Esc` | Only while a dock drag is in progress |
| Paste into a password field | `Ctrl+V` | — |
| Copy is blocked in password fields | `Ctrl+C` | Ctrl+X is blocked as well |

### AI Assistant Plugin

| Action | Keys | Applies when |
| --- | --- | --- |
| Send the message | `Enter` | — |
| Insert a line break | `Shift+Enter` | — |
| Recall the previous input | `Up` | Only when the caret is on the first line |
| Recall the next input | `Down` | Only when the caret is on the last line |
| Select the next file candidate | `Down` | Only while the @ file picker is open |
| Select the previous file candidate | `Up` | Only while the @ file picker is open |
| Insert the file reference | `Enter` | Only while the @ file picker is open |
| Insert the file reference | `Tab` | Only while the @ file picker is open |
| Close the file picker | `Esc` | Only while the @ file picker is open |
| Delete a whole file reference | `Backspace` | — |
| Commit the session rename | `Enter` | — |
| Cancel the session rename | `Esc` | — |
| Close the AI secondary window | `Esc` | — |
