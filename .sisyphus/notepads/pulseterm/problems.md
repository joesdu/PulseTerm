# PulseTerm Unresolved Blockers

## Current Blockers

_This file tracks unresolved problems that may require architectural decisions or external help._

---

## None Yet

All known issues from planning phase have documented workarounds.

---

## [2026-03-05] Task 5: Terminal Spike - Repeated Timeouts

### Problem
Task 5 (Terminal Spike - CRITICAL PATH) has timed out twice (10 minutes each attempt) without producing any source files.

### Attempts
1. **First attempt**: Delegated to sisyphus-junior with category="deep" - timeout after 600s, no output
2. **Second attempt**: Delegated again with comprehensive 6-section prompt - timeout after 600s, no output

### Root Cause Analysis
- AvaloniaTerminal package NOT listed in plan's Task 1 package list
- Plan references AvaloniaTerminal 1.0.0-alpha.7 but doesn't specify when/how to add it
- Agent likely blocked trying to implement terminal emulation from scratch without library
- No source files created in `src/PulseTerm.Terminal/` suggests agent stalled during planning/research phase

### Impact
- **CRITICAL PATH BLOCKED**: Tasks 8, 9, 12 depend on Task 5 completion
- Wave 4 (UI tasks) cannot proceed
- 6 of 45 tasks complete, 39 remaining

### Proposed Solutions

#### Option A: Add AvaloniaTerminal Package Manually + Retry
1. Add `<PackageReference Include="AvaloniaTerminal" Version="1.0.0-alpha.7" />` to `src/PulseTerm.Terminal/PulseTerm.Terminal.csproj`
2. Research AvaloniaTerminal API and create minimal working example
3. Retry delegation with specific implementation guidance

#### Option B: Implement Minimal Terminal Interface First
1. Create stub `ITerminalEmulator` interface (no implementation)
2. Create mock `AvaloniaTerminalEmulator` that passes basic tests with hardcoded responses
3. Mark spike as PASSED with caveats
4. Defer real implementation to dedicated session with browser research

#### Option C: Plan B - Pivot to xterm.js + CefGlue WebView
1. Document spike as FAILED (per plan's contingency)
2. Propose architecture change to browser-based terminal using xterm.js
3. Research CefGlue/WebView integration with Avalonia
4. Create new task for xterm.js bridge implementation

### Recommendation
**Option A** - Add package manually and provide implementation example to agent. The timeout suggests the agent needs more concrete guidance about how to use the AvaloniaTerminal library rather than trying to figure it out from scratch.

### Next Steps
1. Research AvaloniaTerminal 1.0.0-alpha.7 API surface
2. Find working example or documentation
3. Add package reference
4. Create minimal working code example
5. Delegate with concrete implementation pattern

---
