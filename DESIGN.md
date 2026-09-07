# VelaShell Design System

> Single source of truth for all visual decisions. Every color, font, spacing, and component state references the DynamicResource tokens defined here. No literal colors in XAML or C#.

---

## 1. Atmosphere & Identity

VelaShell is a keyboard-first, high-density SSH/SFTP terminal client for ops and dev engineers. The surface is dark-primary (Dracula), with a light variant (Alucard) available at runtime. The visual tone is **precise, information-dense, and restrained** -- no decorative elements, no gradient fills, no illustrative imagery. Every pixel serves a functional purpose.

- **Platform**: .NET 11 + Avalonia 12.1, cross-platform desktop (Windows/Linux/macOS)
- **Window**: Self-drawn frameless (`WindowDecorations="None"`), 36px custom title bar via `TitleBarView`
- **Icon set**: Lucide (stroke weight 2, round caps, 24x24 viewBox scaled to 11-16px)
- **Fonts**: Cascadia Mono (terminal, data, paths, keys) + Inter (UI chrome, buttons, settings); both interface faces follow Settings → Appearance → UI Font
- **Theme switching**: Runtime dark/light/system, driven entirely by `DynamicResource` token swaps

---

## 2. Color Tokens

All colors are `SolidColorBrush` resources keyed by semantic name. XAML binds via `{DynamicResource VelaXxx}`. Never use hex literals.

### 2.0 Named themes

VelaShell ships **twelve named themes** — seven dark, five light. A theme is not a variant switch —
it is a full palette that replaces every `Vela*` token at runtime.

Themes carry the name of the palette they come from — only the two house themes are Vela-branded.

| Theme | Base | Paired terminal scheme |
|---|---|---|
| `VelaDark` (id `dark`, factory default; Dracula) | dark | `Dracula` |
| `One Dark` (`one-dark`) | dark | `One Dark` |
| `Tokyo Night` (`tokyo-night`) | dark | `Tokyo Night` |
| `Nord` (`nord`) | dark | `Nord` |
| `Everforest` (`everforest`) | dark | `Everforest Dark` |
| `Obsidian` (`obsidian`; neutral near-black, OLED) | dark | `Obsidian` |
| `Gruvbox` (`gruvbox`) | dark | `Gruvbox Bright` |
| `VelaLight` (`light`; Alucard) | light | `Alucard` |
| `One Light` (`one-light`) | light | `One Light` |
| `GitHub Light` (`github-light`) | light | `GitHub Light` |
| `Rosé Pine Dawn` (`rose-pine-dawn`) | light | `Rosé Pine Dawn` |
| `Sakura` (`sakura`; pink) | light | `Sakura` |

Plus the pseudo-theme `system`, which resolves to `VelaDark` / `VelaLight` by OS appearance.

**Following vs. pinning.** `AppearanceOptions.TerminalColorsFollowTheme` is the *explicit* record of
which one the user chose; the terminal color-scheme dropdown's first entry is "follow theme", the
rest pin a scheme. Never re-derive "following" by comparing colors against a scheme — that is what
made picking `Dracula` under a non-Dracula theme a no-op (the comparison could not tell "picked
Dracula" from "following"). `null` on that field means a pre-1.5 config that predates it; it is
resolved once, by the old rule, in `TerminalColorScheme.FollowsTheme`.

**Where the values live.** Each theme is a ~25-colour **seed palette**
(`UiThemePalette` in `src/VelaShell.Core/Models/UiTheme.cs`); the sixty-odd `Vela*` tokens below
are *derived* from that seed by `ThemeTokenApplier` (`src/VelaShell/Services/`). It fills a
**detached** dictionary and swaps it into `Application.Resources.ThemeDictionaries` for the active
variant in one move, so every `{DynamicResource}` follows the switch with no restart. Those axaml
files remain the compile-time defaults for `VelaDark` / `VelaLight` (designer, headless tests, the
instant before the applier runs) and are pinned equal to the applier's output by
`ThemeTokenApplierTests`.

**Never write tokens one at a time into a live dictionary.** Each write walks the tree and
re-resolves every `DynamicResource` on it, so the cost scales with the number of writes: 64 tokens
measured 40–57 ms per switch against a 400-binding tree, versus 1.65 ms for the single swap. The
count (not the timing) is pinned by `ThemeTokenShadowingUiTests`. The same rule is why
`VelaTerminalControl.FontFamily`/`FontSize` return early when the value is unchanged — they drop
the glyph cache and relayout the grid.

Adding a theme therefore means adding one seed palette — never sixty hand-copied literals.
`UiThemeCatalog` and `ThemeTokenApplier` are the **only** places colour literals belong in C#.

Derivation rules worth knowing (the rest are 1:1):

- `VelaAccentDim` / `VelaBgActive`(light) / `VelaShell*Dim` / `VelaHeat*` = a seed colour at a fixed alpha.
  Alpha is `#AARRGGBB`; writing it last silently yields a different colour (issue #246), so these
  are derived rather than typed.
- `VelaGaugeCpu` = `Info`, `VelaGaugeMemory` = `Accent`, `VelaGaugeTrack` = `BorderSecondary`.
- `VelaTraceLineDim` = `Info` blended 50% into `BgPage`.
- Heat grid ladder = `TextTertiary → Info → Accent → Yellow → Error` (idle → full).
- `VelaShadowWindow` has exactly two forms, keyed on the theme's base (see §4.5).

Every theme is held to WCAG AA for body text on every surface, and 3:1 for status glyphs, by
`UiThemeCatalogTests`. The paired terminal scheme's background **must equal** `VelaBgTerminal` —
the terminal canvas and the UI around it are one plane; a mismatch shows up as a visible seam.

### 2.1 Backgrounds (`VelaShellTokens.axaml`)

| Token | VelaDark | VelaLight | Usage |
|---|---|---|---|
| `VelaBgPage` | `#191A21` | `#F2EDDA` | Window base layer |
| `VelaBgSidebar` | `#252734` | `#F8F4E4` | Sidebar, status bar |
| `VelaBgTerminal` | `#282A36` | `#FFFBEB` | Terminal canvas, SFTP panel background |
| `VelaBgSurface` | `#343746` | `#FFFBEB` | Floats, popups, cards, column headers |
| `VelaBgActive` | `#44475A` | `#644AC922` | Selected/active row highlight |
| `VelaBgHover` | `#363948` | `#EDE7D0` | Hover state |
| `VelaBgInput` | `#282A36` | `#F7F2DF` | Input fields, footers |
| `VelaTabActiveBg` | `#282A36` | `#FFFBEB` | Active tab background |
| `VelaTabInactiveBg` | `#191A21` | `#EBE5CC` | Inactive tab background |

### 2.2 Text & Icon (`VelaTokens.axaml`)

| Token | VelaDark | VelaLight | Usage |
|---|---|---|---|
| `VelaTextPrimary` | `#F8F8F2` | `#1F1F1F` | Primary text |
| `VelaTextSecondary` | `#B0B8D6` | `#4A4636` | Secondary text, session names |
| `VelaTextTertiary` | `#6272A4` | `#6C664B` | Tertiary text, default icons, file metadata |
| `VelaTextMuted` | `#545B76` | `#9A9377` | Placeholders, weak hints, column headers |

### 2.3 Borders (`VelaTokens.axaml`)

| Token | VelaDark | VelaLight | Usage |
|---|---|---|---|
| `VelaBorderPrimary` | `#3B3E51` | `#E3DCBF` | Standard dividers, row bottom borders |
| `VelaBorderSecondary` | `#44475A` | `#D3CBA9` | Float/tooltip borders |

### 2.4 Accent (`VelaTokens.axaml`)

| Token | VelaDark | VelaLight | Usage |
|---|---|---|---|
| `VelaAccent` | `#BD93F9` | `#644AC9` | Primary brand color (Dracula purple) |
| `VelaAccentDim` | `#BD93F930` | `#644AC91A` | Accent at low opacity (badge/tag backgrounds) |
| `VelaAccentForeground` | `#0A0E14` | `#FFFBEB` | Text/icon on accent-filled surfaces |
| `VelaAccentText` | `#BD93F9` | `#644AC9` | Accent-colored text emphasis |

### 2.5 Semantic Status (`DarkTheme.axaml` / `LightTheme.axaml`)

| Token | VelaDark | VelaLight | Usage |
|---|---|---|---|
| `VelaStatusConnected` | `#50FA7B` | `#14710A` | Connected state dot |
| `VelaStatusConnecting` | `#F1FA8C` | `#846E15` | Connecting state dot |
| `VelaStatusDisconnected` | `#FF5555` | `#CB3A2A` | Disconnected state dot |
| `VelaWarning` | `#FFB86C` | `#846E15` | Warning, folder icons, disconnect action |
| `VelaError` | `#FF5555` | `#CB3A2A` | Error, delete action, danger text |
| `VelaInfo` | `#8BE9FD` | `#036A96` | Information, directory names in file list |

### 2.6 File Browser Specific (`VelaTokens.axaml`)

| Token | VelaDark | VelaLight | Usage |
|---|---|---|---|
| `VelaFileFolderIcon` | `#FFB86C` | `#9E841A` | Folder icon fill |
| `VelaFileDirName` | `#8BE9FD` | `#036A96` | Directory name text |

### 2.7 Terminal ANSI Palette (`DarkTheme.axaml`)

These are **UI-side** markers (charts, badges, gutter glyphs) that borrow the terminal hues — they
are derived from the same seed as everything else. The terminal canvas itself does not read them:
it gets the theme's paired `TerminalColorScheme`, pushed down as `VelaTerminalControl.ThemePalette`
with the user's per-colour overrides layered on top.

| Token | VelaDark | Usage |
|---|---|---|
| `VelaShellWhite` | `#F8F8F2` | Default foreground |
| `VelaShellGreen` | `#50FA7B` | ANSI green |
| `VelaShellCyan` | `#8BE9FD` | ANSI cyan |
| `VelaShellBlue` | `#BD93F9` | ANSI blue |
| `VelaShellYellow` | `#F1FA8C` | ANSI yellow |
| `VelaShellRed` | `#FF5555` | ANSI red |
| `VelaShellMagenta` | `#FF79C6` | ANSI magenta |

---

## 3. Typography

Two font families, both loaded as Avalonia resources. Both are *interface* fonts: they
follow Settings → Appearance → UI Font, which overrides both at runtime. Terminal content
is not a token — `VelaTerminalControl` takes its font straight from Settings → Terminal.

| Token | Default stack | Usage |
|---|---|---|
| `VelaUiMonoFont` | Cascadia Mono, JetBrains Mono, Consolas, monospace | Column-aligned interface text: hostnames, IPs, ports, paths, keybindings, values, tab names, file browser cells |
| `VelaUiFont` | Inter, Segoe UI, Microsoft YaHei, sans-serif | Menus, buttons, settings labels, descriptive text, group headings |

### Size Scale (tokenized: `VelaFontSize<n>`)

Every size below is a resource token whose name is its pt value at the default UI font size
(13). Settings → Appearance → UI Font Size rescales the whole ladder by `base / 13`
(rounded, floor 6), so the hierarchy holds at any base. Never write `FontSize="11"` in a
view — a literal does not follow the setting.

| Size | Weight | Context |
|---|---|---|
| 9 | Medium | Status tags, transfer timestamps |
| 10 | Normal/Medium | Column headers (`letterSpacing:1`), status bar values, breadcrumb separators |
| 11 | Normal/Medium | File list cells, session names, toolbar labels, menu items, terminal info |
| 12 | Normal/Medium | Group names in session tree |
| 13 | Medium | Loading overlay text, icon buttons |
| 14 | Normal | Command palette input |

### Conventions

- Column headers: 10px, `VelaTextMuted`, `letterSpacing:1`, uppercase-capable
- Active session names: 11px, `VelaAccent`, `FontWeight:Medium`
- File directory names: 11px, `VelaFileDirName`
- File metadata (size, perms, time): 11px, `VelaTextTertiary`
- Parent directory (`..`) name: 11px, `VelaTextSecondary`

---

## 4. Spacing, Layout & Grid

### 4.1 Window Structure

```
TitleBar (36px, bg-sidebar)
├── Sidebar (260px, bg-sidebar)  ‖  RightArea
│   ├── Toolbar (36px)           │   ├── TabBar (36px, bg-page)
│   ├── SessionTree (fill)       │   ├── Terminal (fill, bg-terminal)
│   ├── QuickConnect (320px)     │   ├── FileBrowser (220px default, collapsible)
│   └── UserBar (40px)           │   └── BroadcastBar (optional)
StatusBar (24px, bg-sidebar)
```

### 4.2 Standard Row Heights

| Element | Height | Notes |
|---|---|---|
| Title bar | 36px | `bg-sidebar`, bottom 1px `VelaBorderPrimary` |
| Tab bar | 36px | `bg-page`, contains 32px tab items |
| Tab item | 32px | Active: `VelaTabActiveBg` + 2px top accent; inactive: `VelaTabInactiveBg` |
| Toolbar | 36px | Sidebar toolbar, file browser header |
| Column header | 26px | `VelaBgSurface`, bottom 1px `VelaBorderPrimary` |
| File row | 28px | `padding:[0,14]`, bottom 1px `VelaBorderPrimary` |
| Session group row | 30px | `padding:[0,12]` |
| Session host row | 28px | `padding:[0,12,0,36]` (child indent) or `[0,12]` (root) |
| Terminal info bar | 28px | Below tab bar |
| Status bar | 24px | `bg-sidebar`, top 1px `VelaBorderPrimary` |
| Sidebar user bar | 40px | Top 1px `VelaBorderPrimary` |

### 4.3 Padding & Gaps

| Context | Value |
|---|---|
| Standard horizontal padding | 12px or 14px (varies by panel) |
| Button/icon group gap | 4px |
| Inline element spacing | 6-8px |
| List item internal padding | `[0,14]` (file rows), `[0,12]` (session rows) |
| Float padding | 4px (context menus), 8-12px (panels) |

### 4.4 Corner Radii

| Element | Radius |
|---|---|
| Small float (tooltip, context menu, tab dropdown) | 6px |
| Large dialog (settings, new connection) | 8px |
| Button / input field | 3px |
| Badge / tag | 2px |
| Status dot | Circle (7x7) |

### 4.5 Shadows

One token, `VelaShadowWindow`, carries every float and self-drawn window card — the shadow
language must not vary between overlays. It is two layers: a near one that firms up the edge and
a far one that spreads.

| Theme | Token value |
|---|---|
| Dark | `0 1 3 0 #40000000, 0 4 12 0 #66000000` |
| Light | `0 1 3 0 #1A000000, 0 4 12 0 #33000000` |

Two rules the values are constrained by:

- **Reach (`offsetY + blur`) must stay ≤ 16**, matching the card margin. A self-drawn transparent
  window can only paint its shadow inside the window rectangle, so anything past the margin is cut
  off by the window edge — which reads as a smeared block, not a shadow. Change one, change both.
- **Opacity is per theme.** The same 50%-black under a light card turns into a dirty grey
  rectangle, so the light values are roughly 40% of the dark ones.

---

## 5. Components & States

### 5.1 Action Buttons (`Themes/ButtonThemes.axaml`)

Every window and dialog draws its action buttons from **two shared control themes**. They are
registered application-wide in `App.axaml`, so any view can reference them with no local setup.
Do **not** use the Fluent default button appearance, and do **not** re-declare an equivalent
button style inside a view — that is exactly how the same action ends up looking different in
two places (the upload dialog's Cancel button was a bare Fluent button while the file browser's
Upload button was an accent pill).

| Role | Theme | Appearance |
|---|---|---|
| Primary action — upload, create, confirm | `VelaAccentPillButtonTheme` | `VelaAccentDim` fill (translucent), `VelaAccent` foreground for icon *and* label, `CornerRadius:3`, no border until focused |
| Secondary action — cancel, close, browse | `VelaOutlineButtonTheme` | `Transparent` fill, 1px `VelaBorderPrimary`, `VelaTextSecondary` foreground, `CornerRadius:3`, `Height:28`, `Padding:[14,0]` |
| Destructive action | `VelaOutlineButtonTheme` + `VelaError` foreground/border | Never a solid red fill |

Rules:

- **Primary is a translucent pill, not a solid fill.** `VelaAccentDim` is semi-transparent so it
  blends with a user background image; a solid `VelaAccent` block reads as too loud and sits on
  the image like a sticker.
- **Both fills are transparent-friendly.** A row must not mix one button that blends into the
  background with another that sits on an opaque slab.
- **Foreground comes from the theme.** The pill theme sets `Foreground` to `VelaAccent`; content
  inherits it. Only bind `Foreground="{Binding $parent[Button].Foreground}"` on a `LucideIcon`,
  which does not inherit text foreground.
- **Same action, same icon.** If an action has an icon anywhere (e.g. `Icon.upload` on the file
  browser toolbar), the dialog that performs the same action carries that icon too.
- **Buttons in one action row share a height.** Set it on the primary; the outline theme already
  defaults to 28.
- Toolbar icon buttons are a separate role — see `.toolbar-btn` below — and may override
  `Height`/`Padding` to fit a 24px toolbar.

Guarded by `DialogButtonStyleTests`.

### 5.2 Reusable Primitives

#### LucideIcon (`pc:LucideIcon`)
- Custom Avalonia control rendering Lucide path data
- Properties: `Data="{StaticResource Icon.xxx}"`, `Width`, `Height`, `Foreground`
- Icon geometries defined in `Icons.axaml` as `StreamGeometry` with key pattern `Icon.<name>`

#### StatusDot (`Ellipse`)
- 7x7 circle, fill driven by connection state
- Classes: `.dot` (default=disconnected), `.dot.connected` (`VelaStatusConnected`), `.dot.connecting` (`VelaStatusConnecting`)

#### StatusTag (`Border`)
- `CornerRadius:2`, `Padding:[6,1]`, text 9px Medium
- Classes: `.tag` (accent), `.tag.connecting` (warning), `.tag.offline` (error)

#### ToolbarButton (`Button`)
- Class: `.toolbar-btn` -- `Background:Transparent`, `Padding:6`, `Cursor:Hand`
- Hover: `VelaBgHover` on `ContentPresenter`
- Sizing: 24x24 for icon-only, auto for pill buttons

#### BreadcrumbSegment (`Button`)
- Class: `.crumb` -- transparent bg, no border, `FontFamily:VelaUiMonoFont`, `FontSize:11`, `FontWeight:Medium`
- Hover: `VelaBgHover` background, `VelaAccent` foreground

### 5.3 File Browser (`FileBrowserView.axaml`)

The existing SFTP file browser is the primary reusable component for the dual-pane SFTP document.

**Structure:**
- Outer `Border`: `VelaBgTerminal` background, top 1px `VelaBorderPrimary`
- Header (36px): breadcrumb path + upload pill + toolbar icons
- Column header (26px, `VelaBgSurface`): resizable columns with splitter handles
- File list (`ListBox`): 28px rows, multi-select, context menu

**Row anatomy (28px):**
- Icon (13px): `VelaFileFolderIcon` for directories, `VelaTextTertiary` for files, `VelaTextTertiary` corner-left-up for `..`
- Name: `VelaUiMonoFont` 11px, `VelaFileDirName` for dirs, `VelaTextSecondary` for files, `VelaTextSecondary` for parent
- Metadata columns: `VelaUiMonoFont` 11px, `VelaTextTertiary`

**Selection:** `VelaBgHover` replaces default theme blue (style on `ListBoxItem:selected`)

**Parent row (`..`):** `VelaBgHover` background, no bottom border

**Hover:** `VelaBgHover` on `Border.file-row:pointerover` (scoped to avoid context menu bleed)

**Loading overlay:** `VelaBgSurface` at 0.8 opacity, centered spinner text + optional progress bar

**Error banner:** `VelaBgActive` background, `VelaAccent` text, shown above column header

### 5.4 Session Tree (`SessionTreeView.axaml`)

- Group row (30px): chevron + folder icon (color rotates: `VelaWarning`/`VelaInfo`/`VelaAccent`) + name (`VelaTextPrimary` 12px Medium) + count (`VelaTextTertiary` 10px)
- Session row (28px): status dot + name + optional status tag
- Selected: `VelaBgActive` background, name switches to `VelaAccent` Medium
- Hover: `VelaBgHover`

### 5.5 Context Menus (global style in `DockStyles.axaml`)

- `VelaBgSurface` background, `VelaBorderSecondary` 1px border, `CornerRadius:6`, `Padding:4`
- Item: `VelaUiMonoFont` 11px, `VelaTextSecondary`, `Padding:[10,5]`, `CornerRadius:4`
- Selected/hover: `VelaBgHover` background, `VelaTextPrimary` foreground
- Disabled: `VelaTextMuted` foreground, 0.6 opacity
- Separator: `VelaBorderPrimary` 1px, `Margin:[6,4]`
- Danger items: `VelaError` foreground + icon
- Primary action items: `VelaTextPrimary` Medium + `VelaAccent` icon

### 5.6 Tooltips (global style in `DockStyles.axaml`)

- `VelaBgSurface` background, `VelaBorderSecondary` 1px border, `CornerRadius:6`, `Padding:[8,4]`
- `VelaTextSecondary` foreground, 11px

### 5.7 Tab Navigation Buttons (`.tab-nav` in `DockStyles.axaml`)

- 24x24, `CornerRadius:3`, transparent background
- Hover: `VelaBgHover`
- Disabled: 0.35 opacity

### 5.8 Scrollbars (`Themes/ScrollBarThemes.axaml`, overrides Fluent's `{x:Type ScrollBar}`)

Windows Explorer semantics, both orientations, application-wide (ScrollViewer, lists, AvaloniaEdit, terminal scrollback):

- Lane: 16px, always reserved (`VelaScrollBarSize`) — expanding never shifts layout
- Idle: 2px thumb hugging the outer edge (`CornerRadius:1`), no track, no arrows — Fluent's resting look, kept as-is
- Expanded (pointer over the bar, or `AllowAutoHide=false`): `VelaScrollBarTrackFill` lane fades in, chevron buttons fade in at both ends, thumb becomes a 6px **centered** pill (`CornerRadius:3`) — *not* Fluent's full-width block
- Colors are sampled pixel-for-pixel from a Windows 11 dark scrollbar, not invented: the lane is **one or two steps darker** than the surface it covers (reference: content `#191919`, lane `#171717`), never a lighter band; thumb and arrows are neutral grey `#959595`. Hence `VelaScrollBarTrackFill` = `#232532` dark / `#EDE7D0` light
- Thumb fill: `VelaScrollBarThumbFill` → `…PointerOver` → `…Pressed`; arrows `VelaScrollBarArrowFill` with `…ButtonBackgroundPointerOver/Pressed` behind them
- Chevrons use this file's own `VelaScrollBarArrow{Up,Down,Left,Right}` geometries (8×6.4 units, ~1.4px stroke at the 7px render size). Fluent's chevron paths are hairlines drawn for large sizes and turn into a grey smudge below ~12px
- Transition: 0.1s on thumb padding/corner radius and on track/arrow opacity. Thickness is real layout (padding), never `RenderTransform` scaling — scaling would squash the round cap into an ellipse
- Timing: `ShowDelay:150ms`. Avalonia's default is 0.5s and `OnPointerEntered` only *schedules* the expansion — with the pointer already resting on the 2px line, that half second reads as "the scrollbar won't come out" (#378). Zero is the other failure: a pointer crossing the lane on its way somewhere else spends only tens of milliseconds on it and would trip the expansion. 150ms clears a deliberate hover without a wait and lets an incidental brush pass. Collapse keeps Avalonia's default 2s `HideDelay`, so leaving the bar doesn't make it snap shut either
- `AllowAutoHide=false` means **permanently expanded** (`ScrollBar.UpdateIsExpandedState`), not merely "visible". Don't reach for it to advertise that a pane scrolls — the idle 2px line already does that, and forcing it leaves one view wearing a fat track-and-chevrons bar that matches nothing else in the app
- The thumb control keeps the full 16px width, so the grab area does not shrink with the visible pill
- Isolated plugin processes (`VelaShell.PluginHost`) intentionally stay on stock Fluent — that host may not reference app assemblies

---

## 6. Motion & Interaction

### 6.1 Transitions

- **Theme switch**: Instant token swap, no animation (runtime `DynamicResource` re-evaluation)
- **Float appearance**: Fade-in (opacity 0 to 1, ~150ms)
- **Float dismissal**: Fade-out (~150ms) with debounce for hover-triggered panels
- **Tab scroll**: Instant (no smooth scroll), `ScrollIntoView` on activation
- **SSH/SFTP protocol tabs**: Selection remains synchronous; `Background`, `BorderBrush`, and `Foreground` settle with `CubicEaseOut` over 120ms. No layout properties animate and tab labels remain aligned at rest.
- **VelaDock tab selection**: Selection pseudo-classes and `ContentHost.Target` update synchronously. Tab chrome uses 120ms `CubicEaseOut` brush transitions without a permanent offset or opacity bias on inactive labels.
- **VelaDock content switching**: The cached view is reparented immediately, then the current `ReparentingHost` settles from opacity 0 / `translateY(2px)` to opacity 1 / `translateY(0px)` over 120ms `CubicEaseOut`. A generation guard makes rapid switching safe; stale queued settles cannot affect the latest target. There is no outgoing-content retention or delayed target assignment.
- **Reduced motion**: Avalonia 12.1 exposes no public reduced-motion preference in this repository, so these transitions are not currently suppressed from an OS preference. Motion remains limited to the scoped 120ms visual states above; no application-wide settings subsystem is introduced.

### 6.2 Hover Behavior

- All interactive elements: `VelaBgHover` background on pointer over
- Icon buttons: `VelaTextPrimary` foreground shift on hover (when semantically appropriate)
- Breadcrumb segments: `VelaAccent` foreground + `VelaBgHover` background on hover
- File rows: `VelaBgHover` background, scoped to `.file-row` class (avoids context menu bleed)
- Context menu items: `VelaBgHover` + `VelaTextPrimary`

### 6.3 Focus & Keyboard

- **Tab navigation**: Standard Avalonia focus traversal
- **Command palette**: `Ctrl+P`/`Ctrl+K` global, `Up/Down` navigate, `Enter` executes, `Esc` closes
- **Session tree**: `Enter`/double-click connects, right-click opens context menu
- **File browser**: Double-click enters directory or opens file, `Ctrl`/`Shift` multi-select
- **Column resize**: Pointer drag on splitter borders between column headers

### 6.4 Context Menu Triggers

- Session tree: right-click on session row
- File browser: right-click on file row or empty list area
- Column headers: right-click toggles column visibility
- Tab bar: right-click on tab (close, duplicate, sync input, etc.)

---

## 7. Depth & Surfaces

### 7.1 Surface Hierarchy (back to front)

| Layer | Token | Role |
|---|---|---|
| 0 - Base | `VelaBgPage` | Window background, visible only at edges |
| 1 - Chrome | `VelaBgSidebar` | Sidebar, status bar (slightly elevated from page) |
| 2 - Content | `VelaBgTerminal` | Terminal canvas, SFTP panel body |
| 3 - Elevated | `VelaBgSurface` | Floats, popups, cards, column headers |
| 4 - Interactive | `VelaBgHover` | Hover state overlay |
| 5 - Selected | `VelaBgActive` | Selected/active state |

### 7.2 Border Rules

- **Horizontal dividers**: 1px `VelaBorderPrimary` at top or bottom of fixed-height bars (title bar bottom, status bar top, toolbar bottom, column header bottom, file row bottom)
- **Panel separators**: 1px `VelaBorderPrimary` between sidebar and right area, between terminal and file browser
- **Float outlines**: 1px `VelaBorderSecondary` (slightly lighter, for visual lift)

### 7.3 Floats

- **Small floats** (context menus, tooltips, tab dropdowns, transfer panel, tunnel panel, message centre): `VelaBgSurface` + `VelaBorderSecondary` + `CornerRadius:6` + `BoxShadow:VelaShadowWindow` (§4.5)
- **Large dialogs** (settings, new connection, command palette): `VelaBgSurface`/`VelaBgPage` + `CornerRadius:8` + `BoxShadow:VelaShadowWindow` (§4.5) + optional modal overlay
- **Rounded corners do not clip on their own.** A card that scrolls or paints content to its own
  edge (a list, a full-height accent bar) needs an inner `Border` with `ClipToBounds:True` and the
  *inner* radius (outer − 1px stroke) wrapping that content; putting `ClipToBounds` on the card
  itself would clip away its own `BoxShadow`.
- **All floats**: Position-aware edge avoidance, theme-synced at runtime

---

## SFTP Dual-Pane Document Contract

### Component: `SftpDocumentView`

A standalone dock document hosting a side-by-side local + remote file browser for cross-machine file transfer. This is the primary new UI surface for SFTP work.

#### Layout

```
┌───────────────────────────────────────────────────────────────┐
│ SftpDocument Header (36px, VelaBgSidebar)                     │
│  Left: session identity badge + server name                   │
│  Center: transfer status / progress                           │
│  Right: view options, refresh all, close                      │
├────────────────────────────┬──┬───────────────────────────────┤
│ LocalFilePaneView          │▒▒│ Remote FileBrowserView        │
│ (left, ~50%)               │▒▒│ (right, ~50%)                 │
│                            │▒▒│                               │
│ ┌────────────────────────┐ │▒▒│ ┌────────────────────────────┐│
│ │ Header 36px            │ │▒▒│ │ Header 36px                ││
│ │ root selector + full   │ │▒▒│ │ remote path breadcrumb     ││
│ │ current path + Go Up   │ │▒▒│                               ││
│ ├────────────────────────┤ │▒▒│ ├────────────────────────────┤│
│ │ Column header 26px     │ │▒▒│ │ Column header 26px         ││
│ ├────────────────────────┤ │▒▒│ ├────────────────────────────┤│
│ │ File list (fill)       │ │▒▒│ │ File list (fill)           ││
│ │ 28px rows              │ │▒▒│ │ 28px rows                  ││
│ └────────────────────────┘ │▒▒│ └────────────────────────────┘│
│                            │▒▒│                               │
├────────────────────────────┴──┴───────────────────────────────┤
│ Transfer footer (optional, 32px, VelaBgInput)                 │
└───────────────────────────────────────────────────────────────┘
```

#### Geometry

- **Container**: `VelaBgTerminal` background, fills available dock space
- **Split**: `LocalFilePaneView` + 4px `GridSplitter` + remote `FileBrowserView`; each pane has an explicit `MinWidth=280px`
- **Splitter**: `GridSplitter` column, 4px wide, `VelaBorderPrimary` background, `Cursor:SizeWestEast`
- **Default split**: 50/50, resizable via splitter drag
- **Minimum pane width**: 280px each

#### Header (36px, `VelaBgSidebar`)

- **Left pane header**: `hard-drive` icon (14px, `VelaTextTertiary`) + root selector + full current path (`VelaUiMonoFont` 11px Medium `VelaTextPrimary`) + Go Up and local refresh buttons
- **Right pane header**: Reuses existing `FileBrowserView` header exactly (session badge + `folder-open` accent + remote breadcrumb + upload pill + toolbar)
- **Shared header** (top bar): Session identity (if applicable) and distinct local/remote refresh actions with accessible names

#### Column Headers (26px, `VelaBgSurface`)

- Identical 26px column-header treatment: `VelaTextMuted` 10px `letterSpacing:1`
- Local columns are Name / Size / Modified; directories have blank Size and the parent row has blank Size and Modified metadata
- Remote columns retain Name / Size / Permissions / Owner / Group / Type / Modified
- Column resize and visibility parity are future enhancements, not required for this contract

#### File Rows (28px)

- Local rows use icon (13px) + constrained Name (11px) + Size + Modified
- Folder icon: `VelaFileFolderIcon`
- Directory name: `VelaFileDirName`
- File name: `VelaTextSecondary`
- Metadata: `VelaTextTertiary`
- Parent `..` row: `VelaBgHover` background, no bottom border, `corner-left-up` icon

#### Transfer Interaction

- **Drag from local to remote**: Upload (triggers file transfer component)
- **Drag from remote to local**: Download
- **Double-click remote file**: Download and open with default program
- **Double-click remote directory**: Navigate into
- **Upload / Download / Delete**: Remain explicit primary SFTP actions, with tokenized hover/focus states and no global focus-adornment removal
- **Right-click**: Context menu per existing `FileBrowserView` pattern (refresh, upload, download, rename, move, copy name/path, new folder/file, delete, properties)

### Component: `LocalFilePaneView`

A local filesystem view paired with the remote `FileBrowserView`, sharing the same 36px header, 26px column header, 28px rows, selection, loading, error, and theme-token language.

#### Requirements

- Same visual structure as `FileBrowserView` (36px header, 26px column header, 28px rows)
- Root selector with full current path and Go Up navigation
- Local columns: Name, Size, Modified; directory Size is blank and the synthetic parent row has blank metadata
- A clickable segmented local breadcrumb and full column resize/visibility parity are future enhancements, not required here
- Same selection model (multi-select with Ctrl/Shift)
- Context menu and toolbar retain upload, download, delete, and refresh actions
- Same hover/selected/parent-row states
- Same loading overlay and error banner patterns
- `VelaBgTerminal` background (matches remote pane)

### Component: Remote `FileBrowserView` (reuse)

The existing `FileBrowserView` is reused as-is for the right (remote) pane. No visual changes needed. It already provides:

- Breadcrumb navigation with clickable segments
- Resizable, toggleable columns
- Multi-select file list with 28px rows
- Context menu with full file operations
- Loading overlay with progress
- Error banner
- Upload pill button with scoped tokenized focus styling and visible accessible focus state
- Session identity badge

### States

| State | Visual Treatment |
|---|---|
| **Loading** | `VelaBgSurface` overlay at 0.8 opacity, centered `VelaTextSecondary` 13px text, optional `VelaAccent` progress bar (height 6, corner 3) on `VelaBorderPrimary` track |
| **Empty directory** | Centered `VelaTextMuted` placeholder text ("No files" or localized equivalent) |
| **Error** | `VelaBgActive` banner above column header, `VelaAccent` 11px text, auto-dismisses on next successful navigation |
| **Disabled (disconnected)** | Terminal canvas dims, centered disconnect overlay with `VelaStatusDisconnected` status + "Connection lost" + reconnect button |
| **Selected row** | `VelaBgHover` background (replaces default blue) |
| **Hover row** | `VelaBgHover` background (scoped to `.file-row` class) |
| **Parent row (`..`)** | `VelaBgHover` background, no bottom border |
| **Focus** | Standard Avalonia focus ring (keyboard navigation) |
| **Context menu open** | Menu floats at pointer position, parent row retains hover state |

### Compiled Binding Requirement

All XAML views in this project use `x:DataType` for compiled bindings (`AvaloniaUseCompiledBindingsByDefault`). The dual-pane views must follow this pattern:

```xml
x:DataType="vm:SftpDocumentViewModel"
```

Bindings use `$parent[TypeName].((vm:XxxViewModel)DataContext).Property` syntax for cross-element DataContext access. Never use `ReflectionBinding` in new views.

### Keyboard Accessibility

- **Tab traversal**: Standard Avalonia focus order (header -> left pane -> splitter -> right pane)
- **Arrow keys**: Navigate file list rows
- **Enter**: Open directory or file
- **Delete**: Delete selected files (with confirmation)
- **F2**: Rename selected file
- **Ctrl+A**: Select all files in focused pane
- **Ctrl+C/V**: Copy/paste file paths
- **Ctrl+Shift+F**: Toggle SFTP panel visibility (existing binding)
- **Escape**: Close context menu or cancel operation

### Theme Accessibility

- All visual properties bind to `DynamicResource` tokens, never literal colors
- Theme switch at runtime updates all surfaces instantly
- Both dark (Dracula) and light (Alucard) themes fully supported
- Status colors (`VelaStatusConnected/Connecting/Disconnected`) are theme-aware
- File-specific colors (`VelaFileFolderIcon`, `VelaFileDirName`) are theme-aware
- Error/warning/info semantic colors (`VelaError`, `VelaWarning`, `VelaInfo`) are theme-aware

### Token Reference (for implementation)

All tokens used in the SFTP dual-pane document. Source files: `VelaTokens.axaml`, `VelaShellTokens.axaml`, `DarkTheme.axaml`, `LightTheme.axaml`.

**Backgrounds**: `VelaBgPage`, `VelaBgSidebar`, `VelaBgTerminal`, `VelaBgSurface`, `VelaBgActive`, `VelaBgHover`, `VelaBgInput`, `VelaTabActiveBg`, `VelaTabInactiveBg`

**Text**: `VelaTextPrimary`, `VelaTextSecondary`, `VelaTextTertiary`, `VelaTextMuted`

**Borders**: `VelaBorderPrimary`, `VelaBorderSecondary`

**Accent**: `VelaAccent`, `VelaAccentDim`, `VelaAccentForeground`, `VelaAccentText`

**Status**: `VelaStatusConnected`, `VelaStatusConnecting`, `VelaStatusDisconnected`

**Semantic**: `VelaWarning`, `VelaError`, `VelaInfo`

**File-specific**: `VelaFileFolderIcon`, `VelaFileDirName`

**Fonts**: `VelaUiMonoFont`, `VelaUiFont`

---

*This document is the implementation contract. All XAML views reference these tokens via `{DynamicResource VelaXxx}`. No literal hex colors, no web CSS variables, no invented tokens.*
