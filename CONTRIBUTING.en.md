# Contributing to VelaShell

Thanks for taking the time to improve VelaShell. This document records the conventions this repository **actually uses** — follow them and your PR should not get stuck on process.

[简体中文](CONTRIBUTING.md) · **English**

---

## Before you start

| What you want to do | Read first |
|---|---|
| Change any code | [`docs-en/architecture.md`](docs-en/architecture.md) — layering baseline and **dependency direction**, the hardest constraint in this repo |
| Change UI / interaction | [`docs-en/interaction-and-ui-specs.md`](docs-en/interaction-and-ui-specs.md) |
| Change the terminal engine | The Terminal Design Notes in `docs-en/architecture.md`, plus [`docs-en/terminal-input-ordering-analysis.md`](docs-en/terminal-input-ordering-analysis.md) |
| Change the plugin system | All 15 documents under [`docs/plugins/`](docs/plugins/), especially [12-security-threat-model.md](docs/plugins/12-security-threat-model.md) |
| Write a third-party plugin | You do not need to change this repository — see the [plugin development guide](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md) |

**Open an issue before you start coding.** Especially for anything touching architecture, new dependencies, the terminal engine or plugin contracts — the trade-offs there usually have history behind them (much of it written in code comments), and a short conversation saves both sides a lot of rework. Small fixes (typos, obvious bugs, docs) can go straight to a PR.

---

## Setting up

| Requirement | Notes |
|---|---|
| **.NET SDK** | Pinned in [`global.json`](global.json), currently a **preview** build (`allowPrerelease: true`). An older SDK will refuse to restore |
| **IDE** | Visual Studio 2026 / Rider / VS Code + C# Dev Kit all work; the repo ships `.editorconfig` and `VelaShell.sln.DotSettings` |
| **Docker** (optional) | Only needed for the real-SSH integration tests |

### Strong-name key: build Debug locally, not Release

`src/VelaShell.snk` **is not in the repository** (`.gitignore` excludes `*.snk`); the release pipeline injects it from a secret. Signing is only enabled when `Configuration=Release`, so:

- ✅ `dotnet build` (Debug by default) — fine
- ❌ `dotnet build -c Release` — will always fail locally, no key file

There is a knock-on effect: `InternalsVisibleTo("VelaShell.Tests")` is declared **only in unsigned builds** (a signed assembly's friend declaration needs the public key). That means **tests only run against Debug**. This is not optional.

---

## Build, run, test

```bash
# Build the whole solution
dotnet build VelaShell.slnx

# Run it
dotnet run --project src/VelaShell/VelaShell.csproj

# All tests (seven test projects)
dotnet test VelaShell.slnx

# A single project
dotnet test tests/VelaShell.Tests/
```

`tests/velashell.runsettings` is applied automatically by `tests/Directory.Build.targets`, so **you do not need `--settings` on the command line**. It sets a 60-second per-test timeout; the reason is below.

### Real-SSH integration tests

```bash
docker compose -f docker-compose.test.yml up -d   # openssh-server on localhost:2222
dotnet test tests/VelaShell.Tests/
```

Without the container these cases skip themselves; they will not fail.

### Two hard rules for headless UI tests

Every UI test in `VelaShell.Tests` shares **one** headless UI thread, and work items run in order on it. Therefore:

1. **Test bodies must return synchronously** — write `return Task.CompletedTask;`, **do not write `async`**. An `async` body binds the wrong overload, so none of the assertions execute **and the test still goes green** — worse than failing.
2. **Close your window before returning.** One work item that never returns occupies that thread forever, and every later `Dispatch` queues indefinitely — the symptom is "passes alone in a second, hangs forever with the suite", and which test hangs depends only on execution order.

The 60-second timeout exists for exactly this: it downgrades "the whole run hangs" to "that one test failed", and points at the blockage. If something does hang, use `--blame-hang-timeout`.

**Also: never construct an `App` instance inside a test.** That was done once and deadlocked the entire suite.

---

## Branches and commits

### Branches

`dev` is the integration branch, `main` is the release branch. **Branch off `dev` and target your PR at `dev`.**

Branch naming follows existing practice — `<type>/<issue>-<short-slug>`:

```
feat/227-terminal-padding
fix/226-file-browser-manual-path
```

### Commit messages

This repository does **not** use Conventional Commits. The existing style is a single-line summary — historically in Chinese, but English is fine — describing what the commit does:

```
Fix cursor glitch when dragging the custom title bar
Rebuild the shortcut reference from a single ShortcutCatalog
```

Start with a verb, state the outcome rather than the process, keep it to one line. Longer background belongs in the body — or better, in a code comment (see below).

---

## Pull requests

> **There is no PR gate CI in this repository.** `.github/workflows/` contains only the release pipeline, which triggers when a Release is published. **Nothing automated will catch your mistakes** — correctness before merge rests entirely on what you ran locally.

Before opening a PR, verify yourself:

- [ ] `dotnet build VelaShell.slnx` — zero warnings, zero errors (the repo is currently clean; do not bring warnings in)
- [ ] `dotnet test VelaShell.slnx` — all green
- [ ] New or changed behaviour has tests
- [ ] User-facing strings go through localization (see below), nothing hard-coded
- [ ] Affected documentation is updated (see below)

Write **why** in the PR description. The diff already shows what changed; only you know why.

---

## Code conventions

### Formatting

`.editorconfig` is authoritative and your IDE applies it. A few easy ones to miss:

- **LF** line endings, **UTF-8**, final newline
- 4 spaces for C#; **2 spaces for XAML / JSON / XML**
- `Nullable` and `ImplicitUsings` are enabled repo-wide; `LangVersion` is `preview` (latest syntax such as the `field` keyword is available)

### Doc comments are enforced at compile time

`GenerateDocumentationFile=true` is on everywhere, so **a public member without an XML doc comment produces a warning** — and we require zero warnings. Existing comments are written in Chinese; match the surrounding file.

### Comments explain *why*, not *what*

This is the most distinctive convention in the repo and the one we most want you to keep. The code already says what it does; a comment should say **why it was written this way and what breaks otherwise**:

```csharp
// Keys consumed by IME composition (picking a CJK candidate, say) must never be encoded:
// stray ESC / arrows / Enter would be sent to the PTY. (Real incident: typing Chinese into
// htop's F3 search used to kill htop.)
```

Above all, when code **looks simplifiable but is not**, leave a sentence saying so — otherwise the next person (possibly you, three months later) will "optimize" it away.

### Dependency direction is one-way

```
App(VelaShell) → Presentation / Controls / Infrastructure / Terminal / Core
Presentation   → Core, Terminal
Infrastructure → Core, Terminal (only if an adapter truly belongs there)
Terminal       → Core
Controls       → Core (optional, shared UI contracts only)
```

`Core` depends on nothing. PRs that reverse an arrow (say, `Core` referencing `Infrastructure`) will not be merged — use an interface plus dependency injection when you need data to flow the other way. All DI registration lives in the single composition root at `src/VelaShell/App.axaml.cs`; each layer contributes through its own `*ServiceCollectionExtensions`.

---

## Localization: all five resx files, every time

The UI ships in Simplified Chinese, English, Traditional Chinese, Japanese and Korean. Resources live in `src/VelaShell.Core/Resources/`:

```
Strings.resx          ← English, the neutral (baseline) resource
Strings.zh-Hans.resx
Strings.zh-Hant.resx
Strings.ja.resx
Strings.ko.resx
```

**Adding one key means adding it five times**; miss one and tests go red. Three tests watch this:

| Test | What it catches |
|---|---|
| `AllCultures_HaveIdenticalKeySets` | The five key sets must match exactly (both missing translations and orphan keys fail) |
| `LocalizedKeyUsageTests` | Keys referenced from code/XAML must exist — otherwise the UI shows the raw key name **in every language** |
| `UnusedLocalizedKeyTests` | No dead keys left behind in the resources |

Every user-visible string must go through resources: `{loc:Localize SomeKey}` in XAML, `Strings.Get("SomeKey")` in code. **Those two forms are what the scanners match** — passing a key through a variable is invisible to them and therefore unprotected.

---

## Documentation

`docs/` (Chinese) and `docs-en/` (English) are **mirrors of each other**. Change one and you change the other; filenames correspond one to one:

```
docs/交互与界面规格.md  ↔  docs-en/interaction-and-ui-specs.md
docs/architecture.md    ↔  docs-en/architecture.md
```

The same applies to `README.md` ↔ `README.en.md`.

---

## Adding a keyboard shortcut? Update the catalog

Shortcuts have a single source of truth: [`src/VelaShell/ViewModels/ShortcutCatalog.cs`](src/VelaShell/ViewModels/ShortcutCatalog.cs). Both Settings → Shortcuts and [`docs-en/keyboard-shortcuts.md`](docs-en/keyboard-shortcuts.md) read from it.

Add a binding without registering it and `ShortcutCatalogTests` fails, printing **ready-to-paste Markdown rows**. Full rules are in the maintenance section of [`docs-en/keyboard-shortcuts.md`](docs-en/keyboard-shortcuts.md).

---

## Do not touch these

| Leave alone | Why |
|---|---|
| `Version` in `Directory.Build.props` | Overridden from the Release tag via `-p:Version` at publish time; editing it fights the pipeline |
| Any `*.snk` | Keys are not committed; CI injects them |
| `VelaPluginsBundleVersion` | A Release pin for another repository, with specific pitfalls documented in the comment — do not casually bump it |
| The artifact layout in `.github/workflows/release.yml` | The macOS `tar.gz` / `dmg` split is tightly coupled to `latest.json`; getting it wrong silently breaks self-update |

Dependency bumps are Dependabot's job (configured for daily NuGet and GitHub Actions checks) — no manual bump PRs needed.

---

## Do not report security issues as issues

Do not open a public issue for a vulnerability. Follow the private process in [`SECURITY.md`](SECURITY.md).

---

## Licence and contributor grant

This project is dual-licensed: [AGPL-3.0](LICENSE) / [commercial](LICENSE-COMMERCIAL.md).

**By submitting a contribution you agree that:**

1. Your contribution is licensed under **AGPL-3.0**; and
2. You grant the copyright holder the right to **sublicense** that contribution under the commercial licence.

This is a lightweight CLA — without it, dual licensing cannot cover community contributions. See [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md) §3.

Please also confirm that the code you submit is your own, or that you have the right to contribute it under those terms. **Do not paste third-party code of unclear origin, or code incompatible with AGPL-3.0.**

---

If anything about the process is unclear, just open an issue and ask — the question itself usually means this document needs work, and we will fix both.
