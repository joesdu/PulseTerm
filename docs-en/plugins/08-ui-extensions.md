# 08 · UI Extensions: Contribution Rendering, VelaUI, and Dedicated Surfaces

> **Implementation note (2026-08)**: By user decision, the VelaUI remote UI tree is **not implemented** (it was briefly implemented and then removed). Plugin UI directly uses **full Avalonia** (compile-time AXAML/code, its own styles and internationalization, and the ability to introduce third-party component packages). The only constraint is that the Avalonia version must match the host (the loader shares assemblies with the `Avalonia*` prefix).
> In-process plugin panels can be docked into the main window's tab area (true dock tabs, with draggable splits). Isolated-process plugins are rendered by the built-in Avalonia support in PluginHost as **independent card windows** (`PluginHostShellWindow`, using the same specification as the main program's resource-monitor window). Cross-process dock embedding (HWND adoption through SetParent) was implemented, but had fundamental tension with the dock's single-host reparenting model, where switching tabs repeatedly detaches and reattaches a cross-process window, causing stutter and windows drifting out. It has **been deprecated** and the host Win32 implementation removed. Only the `ui/embed` RPC protocol remains for future reuse. A stable cross-platform dock-embedding solution remains the shared-memory image surface in §4 of this document (longer term). For the current API, see dev-guide §5.9.

Premise (D6): the plugin process has zero Avalonia dependencies, and all UI is rendered by the host. There are three levels, in increasing order of complexity:

```text
L1 Declarative contributions   Commands/menus/status bar/sidebar placeholders/settings pages — static manifest declarations + a small amount of dynamic updating
L2 VelaUI                      Declarative control tree (virtual UI), plugin sends patches, host renders, events return — carries panels and document pages
L3 Dedicated surfaces          Image surfaces (shared-memory bitmap), audio output, terminal-output channels — high-bandwidth/specialized media
```

## 1. L1: Contribution Rendering

- **Commands**: appear in the command palette, with a plugin-name prefix and icon. On execution, if the plugin is not active, first trigger activation through `onCommand`, then call `ExecuteCommandAsync`. The plugin can dynamically change enabled state and visibility through the `ui/commandUpdate` notification.
- **Menu mount points** (apiLevel 1 frozen list): `commandPalette`, `sftp/item/context`, `localFiles/item/context`, `terminal/context`, `session/context`, `statusBar/item/context`, and `view/title`. The `when` expression determines visibility. The host evaluates it without waking the plugin.
- **Status-bar items**: text + icon + tooltip + click command. Updates are sent by notification and throttled to 4 Hz. High-frequency updates such as MP3 playback progress must be reduced by the plugin itself; the SDK assists.
- **Sidebar views**: a placeholder icon is added to the existing Sidebar, integrated with SidebarView and following its existing convention of showing the ToolTip on the right. The first expansion triggers `onView` activation, and the content area is a VelaUI surface.
- **Document pages**: plugin document types are registered in the VelaDock `DockDocument` model. They participate in draggable splits at the same level as terminal tabs. Content is a VelaUI surface or image surface. Closing a document notifies the plugin with `SurfaceClosed`.
- **Settings pages**: the host automatically generates forms from `manifest contributes.settings`, using the existing settings UI style. It supports string/number/boolean/enum/keybinding. Complex settings UI can set `"custom": true` to use a VelaUI surface instead.

## 2. L2: VelaUI Declarative UI Tree

### 2.1 Model

React-style one-way data flow, but the serialization boundary is the “element tree” rather than the DOM:

```text
Plugin side (SDK): state → Build() produces a virtual tree → diff against the previous tree → UiPatch[]
Host side:        apply patches to the surface's element tree → map/reuse Avalonia controls → render
Events:           user interaction → UiEvent(surfaceId, elementId, eventName, payload) → plugin handles it → new state → loop
```

### 2.2 Control Allowlist (apiLevel 1)

Layout: `StackPanel` `Grid` `WrapPanel` `ScrollViewer` `Border` `Expander` `TabControl`
Text: `TextBlock` (inline-style subset) `SelectableText`
Input: `Button` `ToggleButton` `TextBox` `PasswordBox` (value is not returned in plaintext, use with secrets)
      `CheckBox` `RadioGroup` `ComboBox` `Slider` `DatePicker`
Data: `ListView` (virtualization, incremental-data protocol) `TreeView` `ProgressBar` `Image` (small images, inline/resource ID)
      `Sparkline` (lightweight line chart for S6 dashboards)
Special: `ImageSurfaceHost` (embeds an L3 image surface) `Separator` `HyperlinkButton`

Styling: arbitrary styling is not exposed. Only **semantic tokens** are available (`accent`, `danger`, `muted`, spacing enums, and font-size tiers), ensuring that every plugin looks consistent with the host across five languages, light/dark themes, and DPI scaling. This is an explicit tradeoff: give up free customization in exchange for never breaking the visual system.

### 2.3 Protocol Details

- Element IDs are assigned by the SDK, with stable keys for diffing. Patch types: Insert/Remove/Replace/SetProps/ListSplice, the list-specific operation paired with virtualization.
- Event debouncing: TextBox text changes are debounced by 150 ms by default. Scrolling and pointer movement are **not sent back**. apiLevel 1 does not support frame-by-frame interaction, preventing IPC from being used as a game channel.
- Surface disconnect policy: when a plugin crashes or becomes unresponsive, gray out the surface and show a “Plugin unresponsive” banner with a restart button. After restart, the plugin is responsible for rebuilding the tree. The SDK state layer can automatically replay the last tree.
- Size limits: a maximum of 5,000 elements per surface and 512 KB per patch. Requests over the limit are rejected and counted as a plugin-quality signal. This prevents misuse; the ListView incremental protocol is the intended approach.

### 2.4 SDK Developer Experience

```csharp
surface.SetBody(ui => ui.StackPanel(spacing: Space.M)
    .Children(
        ui.TextBlock(track.Title).FontSize(FontSize.L),
        ui.Slider(value: state.Position, max: track.Duration)
          .OnChanged(v => player.SeekAsync(v)),
        ui.StackPanel(Orientation.Horizontal).Children(
            ui.Button(Icon.Previous).OnClick(player.PrevAsync),
            ui.Button(state.Playing ? Icon.Pause : Icon.Play).OnClick(player.ToggleAsync),
            ui.Button(Icon.Next).OnClick(player.NextAsync))));
```

The fluent builder plus `SetState` triggers rebuild/diff. A C# source generator for an XAML-like DSL may be evaluated later; it is not part of v1.

## 3. L3: Dedicated Surfaces

### 3.1 Image Surface (S1)

```text
Plugin: CreateImageSurfaceAsync(w, h, format) → host creates MemoryMappedFile (double-buffered)
Plugin: decode pixels into the back buffer → Present (sequence number) RPC → host swaps buffers → display on WriteableBitmap
Host: scaling/panning/fit-to-window gestures are handled entirely by the host (the plugin supplies pixels only);
      window-size-change events return to the plugin, which may choose to decode again at a higher resolution
```

- Format: BGRA8888, aligned with Avalonia WriteableBitmap for zero conversion. For large images (>8K on an edge), the plugin must downsample them itself.
- Shared-memory segments are allocated on demand and reclaimed when the surface closes. The host centrally reclaims them if the plugin crashes.

### 3.2 Audio Output

See 07 §9, the capability domain. On the UI side, only the status bar/panel is built by the plugin using L1/L2.

### 3.3 Terminal Output Channel

See 07 §4: a plugin-specific read-only pseudo-terminal document page reusing the existing terminal renderer, providing all existing ANSI color, selection, search, and other capabilities. Suitable for execution logs from automation plugins.

## 4. Themes and i18n

- Themes: the host resolves semantic tokens against the current theme. The `themeChanged` event is pushed, and the VelaUI tree does not need to be rebuilt because tokens are dereferenced on the host side.
- i18n: static contribution points use NLS (03 §6). VelaUI dynamic text is supplied by the plugin. The SDK provides `ctx.I18n.Locale` and change events. Official templates include a five-language resource structure to guide plugins in following the host language.

## 5. Development Plan (This Work Item)

| Task | Description | Dependency | Estimate |
| --- | --- | --- | --- |
| U-1 | L1: wire commands, command palette, and menu mount points, including `when` evaluation integration | M-4 | 4d |
| U-2 | L1: status-bar items + sidebar placeholder views + `onView` activation path | U-1 | 3d |
| U-3 | L1: integrate document-page types with VelaDock (DockDocument extension, closing/dragging semantics) | U-1 | 3d |
| U-4 | L1: settings-page form generator | U-1 | 3d |
| U-5 | L2: element-tree model + patch protocol + host renderer (allowlisted-control mapping, control reuse pool) | P-3 | 6d |
| U-6 | L2: event callbacks, debouncing, ListView incremental/virtualized data, quotas, and quality signals | U-5 | 4d |
| U-7 | L2: SDK builder + differ + state layer (replay after restart) | U-5 | 4d |
| U-8 | L2: disconnect overlay/restart banner (connects to H-4 unresponsive signal) | U-5, H-4 | 2d |
| U-9 | L3: image surface (MMF double buffering, Present, host-side gestures, reclamation) | P-6 | 4d |
| U-10 | Theme-token system + themeChanged; five-language integration validation | U-5 | 2d |

Acceptance: the image-viewer sample (L1 menu + L3 surface) and MP3-player sample (L1 status bar + L2 panel) run completely without changing a single line of host-specific code. A 1,000-row VelaUI ListView scrolls smoothly, with virtualization active and IPC traffic proportional to the visible area.
