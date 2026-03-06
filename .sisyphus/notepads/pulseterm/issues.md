# PulseTerm Issues and Gotchas

## Known Problems and Workarounds

_This file documents problems encountered, their root causes, and solutions applied._

---

## [2026-03-05] SSH.NET Known Issues

### Issue #1762: ShellStream Disposal on Large Output
**Source**: Research from bg_beb48750
**Problem**: ShellStream closes/disposes unexpectedly when receiving large responses
**Workaround**: Use `ShellStream.CanWrite` to check if stream is still open before operations
**Impact**: Terminal bridge (Task 5) must implement watchdog pattern

### Issue #890: Mocking and Testability — RESOLVED
**Source**: Research from bg_beb48750
**Status**: FIXED in SSH.NET 2025.1.0
**Resolution**: `ISshClient`/`ISftpClient` interfaces now include all methods (Connect, Disconnect, etc.)
**Impact**: 
- SshClient/SftpClient: Can use interfaces directly, no wrapper needed
- ShellStream: Still sealed, requires wrapper interface

---
