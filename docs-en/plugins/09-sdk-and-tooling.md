# 09 · SDK and Developer Tooling

Goal (G5): from `dotnet new` to a plugin running in VelaShell in ≤ 5 minutes;
F5 debugging works out of the box. Developer experience is the decisive factor
in the success of the plugin ecosystem, so this area is treated as a
"product."

## 1. Deliverables

| Deliverable | Form | Contents |
| --- | --- | --- |
| `VelaShell.PluginSdk` | NuGet package | Entry-point conventions, `IPluginContext` and all capability proxies, VelaUI builders, test doubles (`FakePluginContext`) |
| `VelaShell.PluginProtocol` | NuGet package (automatically included as an SDK dependency) | RPC contracts and DTOs; plugin projects generally do not reference it directly |
| `VelaShell.Plugin.Templates` | `dotnet new` template package | `velaplugin` (basic), `velaplugin-ui` (with a VelaUI panel), `velaplugin-automation` |
| `vela-plugin` | dotnet tool | `new/pack/validate/sign/install/dev` subcommands |
| Documentation site | docs site (repository Markdown first) | Quick start, API reference (generated from XML comments by DocFX), 3 scenario tutorials (corresponding to the three official samples) |
| `samples/plugins/` | Repository source | image-viewer / mp3-player / auto-runner, continuously compiled with the SDK; both living documentation and regression tests |

## 2. Project Template Shape

```text
dotnet new velaplugin -n MyPlugin --publisher acme
MyPlugin/
├── MyPlugin.csproj          # SDK-style; references PluginSdk; PackVpx target is wired up
├── plugin.json              # id/entry/minimum permissions prefilled, with annotated guidance
├── plugin.nls.json          # Default (English) text + empty templates for five languages
├── src/MyPluginMain.cs      # [VelaPlugin] entry point + one sample command
├── .vscode/ + Properties/launchSettings.json   # F5 = vela-plugin dev --wait-debugger
└── README.md
```

Key `.csproj` mechanisms (provided by the MSBuild targets in the SDK package):

- `PluginProtocol/PluginSdk/StreamJsonRpc` reference markers use `Private=false`
  (they are not copied to the output directory and are provided at runtime by
  PluginHost, see 03 §1);
- `dotnet build -t:PackVpx` produces a `.vpx` in one step;
- Compile-time validation: mismatches between the manifest and code (for example,
  an unregistered command id) produce an MSBuild warning.

## 3. vela-plugin CLI

| Command | Function |
| --- | --- |
| `vela-plugin validate` | Validate the manifest schema, NLS completeness (missing entries across five languages), permission-list validity, and package structure |
| `vela-plugin pack` | Build, remove shared assemblies, zip, and generate an unsigned `.vpx` |
| `vela-plugin sign --key <pfx>` | Developer signing (see 10) |
| `vela-plugin install <vpx>` | Install into the local VelaShell (using the same installation pipeline as the UI) |
| `vela-plugin dev` | Development mode: watch builds and hot-reload loading (see 04 §6) |
| `vela-plugin new keypair` | Generate a developer signing key pair |

## 4. Testing Support (Plugin Author Perspective)

- `FakePluginContext`: in-memory implementations of all capability interfaces
  (in-memory file system, scripted session responses, recorded and asserted UI
  trees), allowing plugin logic to run in ordinary unit tests without a
  VelaShell instance.
- `VelaUiAssert`: snapshot assertions against the virtual tree produced by Build
  (two dimensions: five languages and light/dark themes).
- Integration test runner (longer term): headless host mode that loads a real
  plugin and runs smoke tests.

## 5. Compatibility and Release Discipline (Host Team Self-Governance)

- SDK XML comments cover 100% of the public surface; every capability method
  documents its required permissions and error codes.
- PublicApiAnalyzer locks the public surface; breaking changes within an
  apiLevel make CI fail immediately.
- SDK and host versions are released independently; release notes are divided
  into "new capabilities / new contribution points / behavior changes" and
  written for plugin authors rather than host developers.

## 6. Development Plan (This Area)

| Task | Description | Dependencies | Estimate |
| --- | --- | --- | --- |
| S-1 | PluginSdk package skeleton: finalize entry-point conventions, context, and proxy-generation approach (handwritten vs. source-generated) | P-3 | 2d |
| S-2 | MSBuild targets: `Private=false` mechanism, PackVpx, compile-time validation | S-1 | 2d |
| S-3 | vela-plugin CLI: validate/pack/install (sign pending 10; dev pending H-7) | M-1 | 3d |
| S-4 | Three `dotnet new` templates + complete the launchSettings debugging path | S-2, H-7 | 3d |
| S-5 | FakePluginContext + VelaUiAssert | C-2, U-7 | 4d |
| S-6 | Quick-start documentation + three scenario tutorials (written alongside the sample plugins) | Sample plugins complete | 4d |
| S-7 | Official sample: image-viewer (acceptance S1) | C-3, U-3, U-9 | 3d |
| S-8 | Official sample: mp3-player (acceptance S2) | C-4, C-6, U-2, U-7 | 4d |
| S-9 | Official sample: auto-runner (acceptance S5, see 11 for details) | 11's T-* | 3d |

Acceptance: a newcomer (a colleague who did not participate in development) can
independently complete a "Hello command + one VelaUI panel" plugin by following
the quick-start documentation in ≤ 30 minutes without verbal assistance; the
three official samples compile and run smoke tests in CI with every SDK change.
