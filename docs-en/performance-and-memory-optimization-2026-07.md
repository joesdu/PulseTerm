# Performance and Memory Optimization (2026-07-23 Night Batch)

> Three parallel audits (rendering hot paths / memory and leaks / VM and service layer) → implemented item by item in order of benefit.
> All changes passed regression across 1,061 tests (Terminal 228 / Core 224 / App 609), with zero failures and zero warnings.

## I. Rendering Hot Paths (Full-Screen TUI Stutter and GC Pressure)

| Change | Location | Description |
| --- | --- | --- |
| Skip semantic highlighting on alternate screen | VelaTerminalControl.RenderLine | vim/htop/btop provide their own colors and line content changes every frame (the cache always MISSES); previously, 7 regexes scanned every line every frame — the top hotspot, now skipped directly |
| Reuse semantic-computation buffers | ComputeSemanticColumns | Reuse the StringBuilder / List / SemanticKind?[] trio as members, eliminating per-line, per-frame heap allocations |
| Remove LINQ from semantic matching | SemanticMatcher.Match | Replace chosen.Any (delegate) with a handwritten loop, producing zero delegate allocations on the rendering path |
| Cache gutter text shaping | GutterText (new) | Cache FormattedText for timestamps/line numbers/fold markers by text (high repetition between frames); invalidate on brush/font changes, capped at 512 |
| Batch output updates | OnEmulatorUpdated / ApplyOutputUpdate | Merge cross-thread Post calls into one per frame; when scroll geometry is unchanged (htop in-place redraw, progress bars), no longer notify ScrollChanged subscribers |
| Copy reflow segments | TerminalScreen.ReflowResize | Reuse the collected buffer across logical lines + copy row.Span segments instead of calling Add per cell; dragging to resize no longer creates an O(buffer) allocation storm |
| Cache fold-column pens | RenderFoldColumn | Replace new Pen with a cached PenFor |

**Confirmed healthy by the audit and intentionally left unchanged**: cursor-blink timer (stops when unfocused, no idle spinning across multiple tabs), zero-allocation Utf8Sink decoding,
amortized scrollback head-pointer trimming, and the main GlyphRun batching path. The two ToArray calls in FlushGlyphRun are **intentionally retained**:
Avalonia uses retained-mode rendering, and GlyphRun is held by the scene graph until composition; reusing the underlying array would corrupt already-recorded drawing,
so an independent array per run is a correctness requirement, not an oversight.

## II. Memory Usage

| Change | Location | Magnitude |
| --- | --- | --- |
| Lower default scrollback 50000 → 10000 | AppSettings.ScrollbackLines | A fully loaded single tab drops from 100–240MB → 20–48MB (5×); existing settings are not forcibly changed, so older users can lower them themselves |
| Remove managed references from TerminalCell | TerminalCell + CombiningPool (new) | Replace `string? Combining` with an intern-pool integer index: 24B → 20B per cell, and the entire scrollback buffer (which can reach millions of cells) is no longer scanned cell by cell by the GC; the `Combining` property signature is unchanged, so call sites require no changes |
| Memory-contract test | TerminalCellMemoryTests | Lock in `IsReferenceOrContainsReferences<TerminalCell>() == false` so the type remains blittable; regression-test combining-character behavior end to end |

Audit conclusion: all event-subscription/timer shutdown paths close correctly, with **no deterministic leaks**; every rendering cache has an upper bound;
recording playback is the only holder of a large byte[] peak (released when the window closes, documented and unchanged).

## III. VM and Service Layer

| Change | Location | Description |
| --- | --- | --- |
| ZModem progress throttling | ZModemTransferObserver | Add the same 100ms time slice as SFTP ProgressThrottle: a 100MB transfer drops from about 100,000 UI Posts + 700,000 string allocations → about 10 per second; the completion callback always includes a final 100% refresh |
| Notify transfer rows only on change | TransferItemViewModel.UpdateProgress | A tick with the same value no longer emits an empty InfoLine/ProgressText update (each previously involved string assembly + rebinding) |
| Debounce command suggestions | TerminalTabView | Query the provider only after 90ms of typing has stopped (previously, every printable byte filtered all 500 history entries + sorted); ghost text is still consumed immediately locally, so the feel is unchanged |
| Move startup sync I/O to the background | App.axaml.cs | Log cleanup (directory enumeration + deletion) now truly runs in Task.Run; the quick-command migration marker joins the automatic-sync background chain (preserving the “mark first, then sync” order), so GetResult() no longer blocks the UI |
| Debounce command-history persistence | CommandHistoryService | Instead of serializing all 500 entries for every command, merge changes after 1 second of silence; Clear remains immediate |
| Pause status polling when minimized | MainWindowViewModel + MainWindow | Stop per-second SSH exec probes and periodic ICMP when the window is minimized/hidden to the tray; restart immediately when restored |

## IV. Measurement Notes (Honest Record)

- Startup memory is dominated by window composition (Skia/GPU), about 280MB WorkingSet / 195MB Private, with **no difference** before and after optimization
  (a 78.6MB baseline measured once overnight was disproved as an environmental outlier by a “full rollback and retest”; do not cite it).
- The benefits of this batch appear in **steady state**: 5×↓ at full scrollback capacity + 17%↓ per cell + zero GC scanning surface; full-screen TUI per-frame heap allocations
  are nearly eliminated; UI dispatch during large-file ZModem transfers drops by 10⁴×. These do not appear in idle startup figures.
- Remaining low-priority items (not implemented, see audit): replace “clear all when full” rendering-cache behavior with LRU, stream recording playback reads,
  targeted ResourceMonitor notifications, and generic devirtualization in VtParser.

---

# Additional Batch (2026-07-25): GPU Stack, Not Application Data, Dominates Baseline Memory

The previous batch recorded “startup ~280MB dominated by window composition” as an unoptimizable item. This time, dump and module tables were used to break down the number;
the conclusion is that **it is optimizable, and currently the largest item**.

## Measured Breakdown (Release, Idle with No Connection, Intel Integrated-Graphics Machine)

| Item | Value | Source |
| --- | --- | --- |
| WorkingSet | 396 MB | `Process.WorkingSet64` |
| Private | 312 MB | `Process.PrivateMemorySize64` |
| **Managed heap total** | **23 MB** (204,408 objects) | `dotnet-dump analyze` → `dumpheap -stat` |
| Total loaded module images | 286 MB / 177 modules | `Process.Modules` |
| └ `igc64.dll` (Intel graphics shader compiler) | **82.5 MB** | Same as above |
| └ `igd10um64xe.dll` | 17.1 MB | Same as above |
| └ `libSkiaSharp` + `av_libGLESv2` + `d3dcompiler_47` + `igdgmm64` | 27 MB total | Same as above |

**Managed objects account for only 8%**. The largest category on the heap is `System.Byte[]` at 7.5MB; everything else is Avalonia property storage,
style instances, and compositor nodes, with nothing abnormal. In other words, optimizations targeting C# data structures (cell layout, zero-copy,
object pools) cannot move this baseline — their battleground is **steady-state growth**, not idle residency.

The jump timing also matches: a one-time 187MB increase occurs in the third second after process startup, exactly when the GPU backend initializes and driver modules are mapped in.

## Implementation: Hardware-Acceleration Toggle (Settings → Appearance → Rendering)

`Win32PlatformOptions.RenderingMode` is determined by `Appearance.HardwareAcceleration`. Measurements on the same machine:

| Mode | WorkingSet | Private | Modules |
| --- | --- | --- | --- |
| Hardware acceleration (default) | 396 MB | 312 MB | 286 MB |
| Software rendering | **213 MB** | **124 MB** | 163 MB |

**Savings: 183 MB WorkingSet / 188 MB Private.** The cost is handing drawing to the CPU; because the terminal is primarily text,
most machines will not notice, but systems with a strong GPU and plenty of memory remain smoother with the default. It is therefore a toggle rather than disabled by default.

Three implementation constraints must not be broken during changes:

1. **The rendering backend must be selected before Avalonia initialization**, when DI and SonnetDB are not running yet and settings cannot be read.
   Therefore `SaveSettingsAsync` additionally mirrors this setting to the one-line file `%LocalAppData%/VelaShell/render.mode`;
   `Program.ResolveRenderingMode()` performs only one `File.ReadAllText`, adding no database-initialization overhead.
2. **The software-rendering fallback must always remain at the end of the GPU-mode list** (`[AngleEgl, Software]`), so remote desktops or driver failures do not prevent startup.
3. This setting is not included in Gist sync (machine-specific; see `GistSyncService.ScrubDeviceLocalFields`).

The `VELASHELL_SOFTWARE_RENDER=1` environment variable can force software rendering for measurement and troubleshooting, with higher priority than the setting.

## Still to Watch: Scrollback Line Count Does Not Follow the Lowered Default

When `ScrollbackLines` was last lowered from 50000 to 10000, **stored settings were not forcibly changed**; older users still have 50000 today.
Based on the current cell layout (48B row object + 24B array header + 20B × columns + 8B List slot), the per-row cost is:

| Column width | Per row | 10000 rows | 50000 rows |
| --- | --- | --- | --- |
| 80 | 1,680 B | 16.0 MB | **80 MB** |
| 120 | 2,480 B | 23.7 MB | **118 MB** |
| 200 | 4,080 B | 38.9 MB | **194 MB** |

This is the **per-tab** limit, and is reached only after the buffer is genuinely full — the 23MB managed heap at idle shows that it is far from full under normal conditions.

## Next Structural Optimization (Evaluated, Not Implemented)

**Trim trailing whitespace**: `TerminalRow` allocates `TerminalCell[columns]` regardless of content length, while most rows retired into scrollback use only 20–30% of the column width. When a row retires, trim the array to the last non-whitespace cell; typical shell output could save 3–5× scrollback memory, independently of the user-configured line count.

The obstacle is on the rendering side: `VelaTerminalControl.Render` passes **screen.Columns** (:1301) to `RenderLine`,
not `line.Columns` — shortened rows would go out of bounds. To implement this, `RenderLine` and the column loops for selection/search/copy must be changed together,
so out-of-range columns use the default background. This is the hottest and most correctness-sensitive code in the entire application, **requiring a separate change round + visual verification with real sessions**; it is not suitable for a quick side change.

## Clarification: “Zero-Copy” on the Read Path

`SshTerminalBridge.ReadLoopAsync` does `new byte[bytesRead]` + `Array.Copy` (:243) on every read. Changing it to `ArrayPool` would indeed eliminate this Gen0 churn, but **it saves GC time, not resident memory** — these blocks are consumed by the next frame's `FlushPending` and never survive to Gen2. With only 23MB in the managed heap overall, ranking it after the GPU toggle and scrollback is the correct priority. If implemented, note that `DataReceived` subscribers (session logging/recording)
receive the same array, so it must be confirmed that nobody still holds it before returning it to the pool.
