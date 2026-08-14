# Dock.Avalonia Self-Developed Replacement Plan (VelaDock)

> ✅ **Completed and merged** (2026-07, PR #3 `replacedock`). VelaDock now carries all docking/splitting
> functionality on the main branch. All `Dock.Avalonia` packages and license entries have been removed. The following is the original replacement plan, retained as an implementation record and design reference.
> The resulting architecture is documented in `docs/architecture.md` "Docking (VelaDock)" and `plan.md` §5.

> Branch: `replacedock`. Goal: replace `Dock.Avalonia` with a lightweight, fully self-developed Dock implementation
> (`Dock.Avalonia.Themes.Fluent` / `Dock.Model.ReactiveUI` 12.0.0.2),
> **without changing any existing functionality or UI interaction logic**, while removing the third-party blocker to future Avalonia major-version upgrades.

## 1. Current-State Analysis: Actual Use of Dock.Avalonia

After a repository-wide audit, Dock's integration surface is **highly concentrated**, and only a small portion of its capabilities is used
(a single document area, with no floating windows, tool panels, Pin, or layout serialization):

### 1.1 Code Integration Points

| File | Purpose |
|---|---|
| `src/VelaShell/Docking/TerminalDockFactory.cs` | `Factory` subclass: single `DocumentDock` root layout; `AddTerminal` / `RemoveTerminal`; `DocumentClosed` event |
| `src/VelaShell/Docking/TerminalDocument.cs` | `Document` + `IDataTemplate` wrapper for `TerminalTabViewModel` (`CanFloat=false`, `CanPin=false`) |
| `src/VelaShell/ViewModels/MainWindowViewModel.cs` | Holds `Layout(IRootDock)`; subscribes to `ActiveDockableChanged` / `FocusedDockableChanged` / `DocumentClosed`; calls `AddTerminal` / `RemoveTerminal` when creating/removing tabs |
| `src/VelaShell/Views/MainWindow.axaml` | `<dock:DockControl Layout="{Binding Layout}">` + `ControlRecycling` (reuses already-created views when switching tabs, critical for smoothness) + `TerminalDocument→TerminalTabView` template |
| `src/VelaShell/Themes/DockStyles.axaml` | Heavy skinning: 36px tab bar specification (nunbT), Icon/Header/Close templates (status dot, connection color bar, resource-monitor hover, close button); **the same file also contains many global styles unrelated to Dock (TextBox/ToolTip/ContextMenu/MenuFlyout/tab-nav) that must be retained** |
| `src/VelaShell/Themes/DockTabStrip.axaml(.cs)` | Overrides the Dock tab-strip ScrollViewer theme: fixed 24px three-button control at the right edge on overflow (scroll left/scroll right/tab-list dropdown) |
| `src/VelaShell/Themes/DockContextMenu.axaml` | Overrides the tab context menu: close/others/all/left/right, horizontal/vertical split, tab position (top/left/right); removes floating and MDI |
| `src/VelaShell/App.axaml` | `DockFluentTheme`, Dock metric resources (`DockFontSizeNormal`, etc., 8 items), and mounting of the three theme files above |
| `src/VelaShell/Program.cs` + `Logging/FilteringLogSink.cs` | Exist specifically to filter noise from Dock's `DockCapability` bindings |
| `src/VelaShell/ViewModels/SettingsViewModel.cs` | About-page open-source license list contains a Dock.Avalonia entry |
| `src/VelaShell/Controls/ReparentingHost.cs` | Resolves the double-parenting issue where the same terminal control is instantiated twice during Dock splitting/dragging (the self-developed implementation still reuses the approach) |

### 1.2 Actual Runtime Behaviors (= Interaction Contracts That Must Be Reproduced)

1. **Tab strip**: 35px top area + 1px divider; active tab = tab-active bottom + 2px accent top line;
   7px status dot, 3×12 connection indicator color bar, resource panel shown after a 400ms title hover, 11px close button.
2. **Tab drag reordering** (within a group) and **cross-group dragging**.
3. **Drag to a pane edge to split** (horizontal/vertical, with a draggable proportional splitter).
4. **Context menu**: close / close others / close all / close left / close right, horizontal split, vertical split,
   tab position (top/left/right).
5. **Overflow controls**: when tabs are too wide, scroll left / scroll right / all-tabs dropdown appears at the right edge (active item highlighted with accent).
6. **Active/focus tracking**: clicking a tab or pane → `ActiveTerminalTab` / status bar / SFTP panel update together.
7. **Close semantics**: user closes a tab → `DocumentClosed` → disconnect SSH/SFTP/logging;
   program removes a tab (connection failure) → silently remove it without triggering the close chain.
8. **View retention**: each document's `TerminalTabView` is built only once; switching tabs does not rebuild it
   (previously provided by `ControlRecycling`; critical to smooth multi-tab switching).
9. **Product boundaries**: floating windows prohibited, Pin prohibited, MDI prohibited, no "+" new-tab button (new tabs use Ctrl+T/session tree).
10. **Empty-group collapse**: after the last tab in a split-off group is closed, automatically remove the group and promote its sibling; the primary group never disappears.

### 1.3 Known Defects (Corrected During Replacement; Not Behavior Changes)

- `Ctrl+Tab` / `Ctrl+W` go through `TabBarViewModel` (logical tab collection) and only change `ActiveTerminalTab`;
  **they do not synchronize back to Dock's visible document** — switching tabs with a shortcut does not switch the document area.
  The self-developed implementation connects this in both directions with `TabBar.ActiveTab → Workspace.ActivateDocument`.

## 2. Plan: Self-Developed VelaDock

### 2.1 Design Principles

- **Build only what is used**: document-style tab groups + binary/multi-way split tree + dragging. No floating windows, Pin,
  tool panels, or layout serialization (unused; extension points are sufficient).
- **Separate model and visuals**: pure `INotifyPropertyChanged` models (unit-testable, with no Avalonia dependency);
  the control layer renders from the model. Introduce no new framework and do not depend on ReactiveUI (existing use in the App layer continues).
- **View retention built in**: the workspace control internally maintains a `document → view` cache, naturally replacing
  `Dock.Controls.Recycling`, and reuses `ReparentingHost`'s "preemptive adoption" to avoid double parenting.
- **API compatibility first**: limit `MainWindowViewModel` changes to renaming and type replacement.

### 2.2 Code Layout (All Under App Project `src/VelaShell/Docking/`)

```text
Docking/
  Model/
    DockElement.cs        INPC base class
    DockDocument.cs       Document base: Id / Title / CanClose; IDockViewProvider(CreateView)
    DockNode.cs           Node base: Proportion / Parent
    DockGroup.cs          Tab group: Documents / ActiveDocument / TabsPosition / IsPrimary
    DockSplit.cs           Split pane: Orientation / Children
    DockWorkspace.cs      Workspace: Root, all structural operations, events
    DockPosition.cs       Enum: Center / Left / Top / Right / Bottom
    DockTabsPosition.cs   Enum: Top / Left / Right
  TerminalDocument.cs     Existing class re-based on DockDocument + view factory (no longer depends on Dock)
  TerminalWorkspace.cs     Replaces TerminalDockFactory: AddTerminal/RemoveTerminal/DocumentClosed
  Controls/
    DockWorkspaceControl.cs  Renders split tree (Grid+GridSplitter), view cache, drag-and-drop overlay host
    DockGroupControl.cs      Tab group: tab strip (ItemsControl+ScrollViewer) + content host
    DockTabItem.cs            Individual tab (visuals + pointer interaction entry point)
    DockDropOverlay.cs        Drag-and-drop indicator overlay (group center/four-edge highlighting)
    DockDragController.cs     Drag state machine (reorder/cross-group/split/Esc cancel)
  Themes/ (merged into existing Themes/DockStyles.axaml)
```

### 2.3 Model-Layer Semantics

- `DockWorkspace.Root : DockNode` — initially a single `DockGroup` (`IsPrimary=true`).
- **Structural operations** (all in the model layer and unit-testable):
  - `AddDocument(doc)`: add to the primary group and activate (same as the original Dock behavior: new terminals always enter the first group).
  - `RemoveDocument(doc)`: silently remove (connection-failure tab-removal path), then collapse empty groups.
  - `CloseDocument(doc)`: respect `CanClose` → remove → raise `DocumentClosed`.
  - `CloseOthers/All/Left/Right(doc)`: call `CloseDocument` one by one (ensures the complete SSH cleanup chain).
  - `SplitDocument(doc, orientation)`: create a new group beside the group containing the document and move the document into it (right/bottom side, 50% each).
    If the group contains only one document, split it the same way (consistent behavior for all groups), leaving the original group empty as a drag target.
  - `DockTo(doc, targetGroup, position)`: Center = merge into the group (with an optional index); edge = split beside the target group;
    dragging to the edge of its own group when it is the only tab = split semantics (leave the original group empty).
  - Empty-group collapse: a non-primary group emptied by “moving/closing a document” → remove it from the parent split; if a split has only 1 child → promote the child
    (inherit the proportion); if the promoted node is an empty secondary group left by a split (all siblings have been closed), reclaim it as well;
    the root split converges back to a single group. Empty groups left by splitting remain in place, with the empty pane displaying “Drag a tab here”.
- **Activation semantics**: each group has `ActiveDocument`; the workspace has a global `ActiveDocument`
  (the active document in the last-interacted group). Changes raise `ActiveDocumentChanged`
  — the two original events, `ActiveDockableChanged` + `FocusedDockableChanged`, become one.
- `TabsPosition` applies per group (the context-menu "tab position" applies to the owning group, matching Dock behavior).

### 2.4 Control-Layer Highlights

- `DockWorkspaceControl`: listen for model-tree changes and rebuild the visual tree — split = `Grid`
  (star sizes ↔ `Proportion`, `GridSplitter` writes the proportion back after dragging); group = `DockGroupControl`.
  Own a global `Dictionary<DockDocument, Control>` view cache; remove entries when documents close.
- `DockGroupControl`: `DockPanel`, dock the tab strip according to `TabsPosition` (vertical when left/right),
  and use `ReparentingHost` in the content area for cached views. Tab strip = `ScrollViewer` (hidden scrollbar) +
  three overflow buttons (reuse `WidthOverflowConverter` and `tab-nav` styles; scrolling moves to code-behind,
  removing Dock's `ScrollViewerLineCommand`/converter).
- Inline the existing tab visual specification directly on `DockTabItem` (no longer need the indirect Icon/Header/CloseTemplate three-template layer):
  status dot, accent bar, title + resource hover, close button; selectors rewritten in `DockStyles.axaml` control the styles through classes, with no visual change.
- Rebuild the context menu on `DockTabItem`, with commands wired directly to `DockWorkspace` operations; menu items remain unchanged.

### 2.5 Drag Interaction (Reproducing the Dock Feel)

1. Press on a tab → record the position; move more than 4px → enter drag mode (capture the pointer).
2. While the pointer remains **inside the tab strip of the current group**: calculate the insertion position from each tab's center line and reorder in real time (browser-style).
3. When the pointer leaves the tab strip: show the overlay — when a group is hit, highlight its center/top/bottom/left/right five zones
   (center = merge into the group; four edges = split on that side at 50%); release outside any valid zone performs no operation.
4. `Esc` cancels and restores; dragging outside the window is prohibited (no floating).

### 2.6 Removal Checklist (After Replacement)

- `VelaShell.csproj`: remove `Dock.Avalonia.Themes.Fluent`, `Dock.Model.ReactiveUI`.
- `App.axaml`: remove `DockFluentTheme`, 8 Dock metric resources, and mounts for `DockContextMenu.axaml`
  and `DockTabStripResources`; internalize metrics as constants/styles.
- Delete files: `Themes/DockTabStrip.axaml(.cs)`, `Themes/DockContextMenu.axaml`,
  `Logging/FilteringLogSink.cs` (and the hook in `Program.cs`), `Docking/TerminalDockFactory.cs`.
- `Themes/DockStyles.axaml`: change `dc|*` selectors to selectors for the self-developed controls; retain unrelated global styles unchanged.
- `SettingsViewModel`: remove the Dock.Avalonia license entry.
- `docs/architecture-design.md` / `docs/architecture.md`: update the Dock description.

## 3. Implementation Steps

| Step | Content | Verification |
|---|---|---|
| 1 | Model layer + unit tests (structural operations, activation, close semantics, collapse/promotion) | `dotnet test` |
| 2 | Visual controls (tab strip/content/split/overflow/context menu), static integration that runs | Build + visual inspection on startup |
| 3 | Drag controller (reorder/cross-group/split/cancel) | Manual interaction verification |
| 4 | Integrate `MainWindowViewModel`/`MainWindow.axaml`, add bidirectional TabBar synchronization | Full-feature regression |
| 5 | Remove dependencies and residue (§2.6), update documentation | Zero repository-wide `Dock.` references, successful build |

## 4. Risks and Mitigations

- **Terminal control double-parenting crash** (a historical pitfall): all content hosts use `ReparentingHost` consistently;
  view caching guarantees a single instance.
- **Regression in switching smoothness**: the workspace owns view retention directly, with a shorter path than ControlRecycling;
  specifically test rapid switching across many tabs during verification.
- **Different drag feel**: reproduce the state machine from §2.5; fixed 50% edge splits match Dock's default.
- **Context-menu command semantics**: every close operation must raise `DocumentClosed` per document
  (the SSH/SFTP/logging cleanup chain depends on it); cover this with unit tests.
- **Rollback plan**: the entire replacement is on the `replacedock` branch; at any stage it can be rolled back wholesale to `main`.
