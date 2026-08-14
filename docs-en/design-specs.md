# VelaShell-zh.pen Frame-by-Frame Exact Specifications (Pencil Extraction, Sole Basis for UI Reconstruction)

> Extracted layer by layer from the design-file node tree by Pencil MCP. All colors are token names (see `docs/interaction-and-ui-specs.md` §1.1).
> Fonts: JetBrains Mono = mono, Inter = ui. Icon library: lucide (2px stroke, rounded endpoints, scaled to a 24×24 view box).
>
> ⚠️ **Implementation difference note**: This document is the original design-file extraction. In the implementation, the window is **self-drawn and borderless** (`WindowDecorations="None"`, not a native title bar),
> with `TitleBarView` drawing a 36px title bar: logo + product name on the left, and the action icon group + self-drawn minimize/maximize/close window buttons on the right;
> the 6 **text menu items (Session/Edit/Actions/Search/Tools/Help)** in the “menu bar” below have also been removed wholesale (they duplicated command-palette functionality, a product decision),
> the “Broadcast send” action in the right-side action icon group is implemented as the multi-terminal input-bar toggle; “Group sync link-2” remains an unimplemented disabled state (semi-transparent). The rest of the layout and tokens remain the basis for UI reconstruction.

## Menu Bar `TSiDh` (36px, Full Width)
- Container: `bg-sidebar` fill, 1px `border-primary` bottom edge, padding [0,12], space-between, vertically centered.
- Left `DaZfB` (gap 2): 6 menu items (Session/Edit/Actions/Search/Tools/Help).
  - Each item: frame padding [0,10], cornerRadius 3, stretches to full height; Inter 12 text, **weight 500**, `text-secondary`. Hover: `bg-hover`.
- Right `r3RUzf`→`GQQwj` (gap 4): 7 icon buttons, **24×22**, cornerRadius 3, **12px** icons:
  | Button | lucide | Default color | Notes |
  |---|---|---|---|
  | taSearch | search | text-muted | Terminal search |
  | taCopy | copy | text-muted | Copy |
  | taSplit | columns-2 | text-muted | Split |
  | taTunnel | route | text-muted | Tunnel |
  | taQuickCmd | zap | text-muted | Command palette |
  | taSyncGroup | link-2 | **accent**, container fill `bg-active` | Group-sync active-state example |
  | taBroadcast | send | text-tertiary | Broadcast input |

## Tab Bar `nunbT` (36px, `bg-page`, children bottom-aligned with alignItems:end)
- **Active tab `h0Nv0`**: height 32, `bg-terminal` fill, **2px `accent` top edge** (inner), padding [0,14], gap 8:
  - Status dot ellipse 7×7 `status-connected`; name JB Mono 11 **500** `text-primary`; x icon 12 `text-tertiary`.
- **Inactive tab**: height 32, `tab-inactive-bg` fill, no top line; name JB Mono 11 normal `text-tertiary`; x 12 `text-muted`.
- **tabAdd `9BYBb`**: 32×32 centered, plus 14 `text-tertiary`.
- spacer fill_container.
- **Overflow group `pZGS4`**: padding [0,6], gap 2, height 32; 3 buttons, 24×24, cornerRadius 3: chevron-left / chevron-right / chevron-down, each 14 `text-tertiary`.

## Status Bar `gzmsb` (24px, Full Width, `bg-sidebar`, 1px `border-primary` Top Edge, Padding [0,12], Space-Between)
- Left `1y2au` (gap 14): wifi 12 `status-connected`; `SSH • web-prod-01:22` JB Mono 10 `text-secondary`; 1×12 `border-secondary` divider; `Latency: 12ms` JB Mono 10 **accent**; divider; `↑ 2h 34m` JB Mono 10 `text-tertiary`.
- Right `4Cio1` (gap 12): `xterm-256color` JB Mono 10 `text-muted`; divider; `120×36` muted; divider; cpu icon 11 `text-tertiary` + `23%` JB Mono 10 `text-secondary` (gap 4); memory-stick 11 + `1.2G`; arrow-up-down 11 + `4.2 MB/s`; divider; `UTF-8` muted.

## Sidebar `aMaSq` (260px, `bg-sidebar`, 1px `border-primary` Right Edge)
1. **Toolbar `cnUAB`**, 36px, 1px bottom edge, padding [0,12], space-between:
   - “Resource Explorer”, JB Mono 11 500, letterSpacing 1, `text-secondary`;
   - right gap 4: two 24×24 cornerRadius 3 buttons: plus 13, ellipsis 13, both `text-tertiary`.
2. **Session tree `FrJPu`**, fill, padding [8,0], gap 2:
   - Group row, 30px, padding [0,12], gap 6: chevron-down 12 `text-tertiary` + folder 13 `warning` + name JB Mono 12 500 `text-primary` + count JB Mono 10 `text-tertiary`.
   - Host row, 28px, padding [0,12,0,36], gap 8: 7×7 status dot + name JB Mono 11 (active: **accent** 500 + row `bg-active` fill + “Active” badge: `accent-dim` fill, cornerRadius 2, padding [1,6], JB Mono 9 500 accent; normal: `text-secondary` normal).
3. **Quick-connect area `XcIor`**, 320px, 1px top edge:
   - Header `DdINU`, 36px, padding [0,12], 1px bottom edge: “Quick Connect”, JB Mono 11 500, ls1, `text-secondary` + history button 24×24 on the right.
   - Input `wIHgo`, 32px, `bg-input`, padding [0,12], gap 8: terminal icon 13 `text-tertiary` + placeholder “username@hostname:port”, JB Mono 11 `text-muted`.
   - “// Recent connections”, 24px, padding [0,12], JB Mono 10 `text-muted`.
   - 3 recent rows, 32px, padding [0,12], gap 8: timer 12 `text-tertiary` + vertical stack (gap 1): `root@192.168.1.100` JB Mono 11 `text-secondary` / `2 hours ago` JB Mono 9 `text-muted`.
4. **Bottom user bar `t6hT9`**, 40px, 1px top edge, padding [0,12], space-between:
   - left gap 8: 22×22 circular avatar (cornerRadius 11), `accent-dim`, with user 12 accent inside (at 5,5) + `root` JB Mono 11 `text-secondary`.
   - right gap 6: bell 13 / settings 13, 24×24 cornerRadius 3, `text-tertiary`.

## Terminal Area `QzoMC` (`bg-terminal`, Padding [10,0,0,0])
- **Toolbar `BdPtF`**, 28px, padding [0,14], space-between: left `IGfp7`, gap 12:
  - `root@web-prod-01:~` JB Mono 11 500 **accent**; `uptime: 42d 7h 23m` JB Mono 10 `text-muted`; `|` muted; `latency: 12ms` JB Mono 10 `status-connected`.

## File Area `dyuii` (220px, 1px Top Edge)
1. **Header `cKZr7`**, 36px, padding [0,14], 1px bottom edge, space-between:
   - left gap 8: folder-open 14 **accent** + `/var/www/html` JB Mono 11 500 `text-primary`.
   - right gap 4: upload button (`accent-dim` fill, cornerRadius 3, height 24, padding [0,8], gap 4: upload 12 accent + “Upload”, JB Mono 10 500 accent) + refresh 24×24 (refresh-cw icon).
2. **Column header `3vU7e`**, 26px, `bg-surface`, padding [0,14]: file name (280) / size (100) / permissions (120) / modified time, JB Mono 10 500, letterSpacing 1, `text-muted`.
3. **List row**, 28px, padding [0,14], 1px `border-primary` bottom edge (`..` row uses `bg-hover` with no bottom edge):
   - icon 13 (folder=`warning`, corner-left-up=`text-tertiary`) + name (preceded by two spaces), JB Mono 11 (directory `info`, `..` `text-secondary`), width 262 + size 11 `text-tertiary`, width 100 + permissions, width 120 + time.

## Window Structure (§2 Authoritative)
- **Self-drawn borderless window** (`WindowDecorations="None"`): self-drawn title bar 36 (left logo + name · right action icon group GQQwj + minimize/maximize/close) → (sidebar 260 ‖ right area) → status bar 24. **No separate menu-bar row** (the original “menu bar” text menus have been removed and the action icon group has been merged into the title bar).
- Right area: tab bar 36 → terminal (fill, with optional line-number/timestamp gutter) → file area 220.

## Overlays (Later)
- Command palette `FN5dM`, 560px; transfer component `9Ralg`, 280px; tunnel `fuXS7`, 320px; resource monitor `EP3Gd`, 280px (padding12 gap8); context menu `e6klM`, 200px (padding[4,0]).
- Small overlays: cornerRadius 6 + `bg-surface` + 1px `border-secondary` (outer) + shadow blur16 #00000060 y+4.
- Large dialogs: cornerRadius 8 + shadow blur32 #00000080 y+8 (Settings 720, New Connection 500, Password Verification 420).
