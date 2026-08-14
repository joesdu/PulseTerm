# 05 · IPC Protocol

> **Implementation note (2026-08)**: The isolated-process mode has been implemented along the lines of this document, but one protocol-selection detail intentionally differs: StreamJsonRpc + MessagePack was not introduced (the dependency tree is too heavy and conflicts with this repository's zero-dependency discipline; both processes use the same library and version, and there is currently no need for cross-language negotiation). Instead, the implementation uses a custom **length-prefixed + lightweight bidirectional JSON RPC** (`plugin-sdk/VelaShell.PluginSdk/Rpc/`: three message types, req/res/evt, no request serialization, a unified error-code space, and typed exception restoration).
> The transport is a named pipe (.NET uses UDS underneath on macOS/Linux), with a random name + one-time token + `CurrentUserOnly`. The handshake, error model, and “credentials never leave the main process” behavior remain consistent with this document. Large data uses inline base64 (≤16 MB) and same-machine file paths as an intermediary. The streaming subchannel and shared-memory surfaces (§6-2/3) have not been implemented. If a non-.NET SDK or streaming channels are needed in the future, revisit the StreamJsonRpc/encoding negotiation evaluation. See `Rpc/PluginRpc.cs` for the method list.

## 1. Layering

```text
Application layer   IHostEndpoint / IPluginEndpoint (strongly typed interfaces, defined in PluginProtocol)
Protocol layer      JSON-RPC 2.0 (StreamJsonRpc), MessagePack binary encoding
Framing layer       StreamJsonRpc built-in length-prefixed framing
Transport layer     Windows: NamedPipeServerStream (one per plugin, bidirectional, asynchronous)
                    macOS/Linux: UnixDomainSocket (socket file in the user runtime directory, 0600)
Side channels       ① Streaming data channel (marshaled stream / chunked protocol on the same RPC connection)
                    ② Shared memory (MemoryMappedFile, only for high-bandwidth scenarios such as image surfaces, see 08)
```

Selection rationale, expanding on D3:

- StreamJsonRpc: bidirectional calls, notifications, `CancellationToken`, and `IProgress<T>` are automatically supported across processes, with exception serialization and a mature, stable implementation (used by the VS/VSCode family). MessagePack encoding avoids the expansion of JSON text.
- gRPC is not used because HTTP/2, TLS, and cross-machine communication are unnecessary, and we do not want protoc code generation in the plugin developer inner loop. The protocol itself remains language-neutral (JSON-RPC over a pipe); a future non-.NET SDK can implement the framing layer and encoding independently, with a JSON-encoding negotiation option provided at that time.

## 2. Connection Establishment and Authentication

```text
1. The host creates a pipe/socket (random name) → starts PluginHost (name and token passed through environment variables)
2. PluginHost connects; if it does not connect within 10s → the host considers startup failed
3. First frame (authentication, raw frame, not JSON-RPC): magic "VELA" + authToken
   Validation failure → disconnect immediately and record a security log
4. Attach JSON-RPC and enter the handshake
```

## 3. Handshake

```jsonc
// PluginHost → host  host/hello
{ "protocolVersion": 1,
  "apiLevels": [1],                  // Set of apiLevels supported by PluginHost
  "pluginId": "acme.image-viewer",
  "pluginVersion": "1.2.0",
  "sdkVersion": "1.3.0",
  "encodings": ["messagepack", "json"] }

// Host → PluginHost  response
{ "apiLevel": 1,                     // Negotiated result (highest in the intersection; empty set → reject and report both sides' versions)
  "encoding": "messagepack",
  "hostVersion": "0.3.0",
  "locale": "zh-Hans",
  "theme": "dark",
  "capabilities": ["remoteFs","terminal","ui","storage", ...] }  // Capability domains actually available from the host
```

Before the handshake completes, every call other than authentication and hello is rejected. Afterward, the host calls `plugin/activate(reason)` to enter the plugin lifecycle.

## 4. Endpoint Interfaces (Application Layer, Excerpt)

```csharp
// Exposed by the host to plugins (forwarded through PluginHost). Method name = "<domain>/<action>".
public interface IHostEndpoint
{
    // —— Capability-domain calls (full list in 07; the implicit plugin identity is the first parameter for all calls, bound to the connection and impossible to forge) ——
    Task<RemoteEntry[]> RemoteFsListAsync(string sessionId, string path, CancellationToken ct);
    Task<Stream> RemoteFsOpenReadAsync(string sessionId, string path, CancellationToken ct);
    Task TerminalWriteAsync(string sessionId, string input, CancellationToken ct);
    Task UiPatchAsync(string surfaceId, UiPatch[] patches, CancellationToken ct);
    Task<PermissionState> PermissionQueryAsync(string permissionId);
    // ... Notifications (host → plugin, no response): event delivery
}

// Exposed by the plugin to the host
public interface IPluginEndpoint
{
    Task ActivateAsync(ActivationReason reason, CancellationToken ct);
    Task DeactivateAsync(string reason, CancellationToken ct);
    Task ExecuteCommandAsync(string commandId, JsonElement? args, CancellationToken ct);
    Task UiEventAsync(string surfaceId, UiEvent e, CancellationToken ct);   // VelaUI event callback
    Task OnHostEventAsync(HostEvent e);                                     // Subscribed event (notification)
    Task<string> PingAsync();                                               // Heartbeat
    Task OnEnvironmentChangedAsync(EnvChange e);                            // Language/theme change
}
```

Discipline:

- Interfaces appear only in `PluginProtocol`. All DTOs are immutable records. MessagePack keys are explicitly annotated with integer keys (safe against renaming). **New fields may only be appended as optional fields**. This is the mechanical safeguard for the apiLevel compatibility promise, enforced by binary compatibility checks in CI.
- The plugin identity is bound to the connection. Capability implementations read the pluginId from connection metadata. **The protocol has no parameter for “call as a particular plugin”**, preventing impersonation at the root.

## 5. Error Model

Unified error-code space (`JSON-RPC error.code`):

| Range | Meaning | Example |
| --- | --- | --- |
| -32xxx | Reserved by JSON-RPC | Method not found, invalid parameters |
| 1xxx | General host error | 1001 HostShuttingDown, 1002 CapabilityUnavailable |
| 2xxx | Permission | 2001 PermissionDenied (including permissionId), 2002 PermissionPromptDismissed |
| 3xxx | Remote session | 3001 SessionNotFound, 3002 SessionDisconnected, 3003 SftpError (with embedded VelaSsh error classification) |
| 4xxx | File system | 4001 NotFound, 4002 AccessOutsideScope |
| 5xxx | UI | 5001 SurfaceClosed, 5002 InvalidPatch |

The SDK restores error codes as typed exceptions (`PermissionDeniedException`, etc.). Unknown codes fall back to the `VelaPluginException` base class. Internal host exception details (stacks and paths) are **not sent across processes**. Only the classification code and a safe message are provided, preventing information leakage.

## 6. Large Data and Streams

Three strategies are selected according to data size and shape:

1. **Small (≤256 KB)**: inline as a MessagePack `byte[]` in the response, such as when reading a small configuration file.
2. **Streaming (file transfer, terminal-output subscriptions)**: StreamJsonRpc stream marshaling, a multiplexed substream over the pipe, with natural backpressure. Cancellation immediately terminates the stream. Terminal-output subscriptions additionally use **host-side throttling and coalescing** (frame coalescing in windows of at least 16 ms), incorporating the lesson from the performance pass: high-frequency reporting must always be throttled so a plugin subscription cannot slow the terminal hot path.
3. **High-bandwidth surfaces (image frames)**: MemoryMappedFile shared memory, with RPC carrying only frame metadata (MMF name, dimensions, stride, and sequence number), see 08 §4. The shared-memory segment is created by the host and access is restricted to the current user.

## 7. Ordering, Concurrency, and Reentrancy

- Requests on the same connection execute concurrently (the StreamJsonRpc default). **There is no global serialization**. Domains requiring ordering semantics, such as `terminal/write`, queue requests by sessionId inside the host capability implementation, matching the existing rule that terminal input may only be enqueued and must not be written directly to the stream.
- All host → plugin calls include a timeout (5s by default, 2s for UI events). A slow plugin must not block any host path (fire-and-forget + timeout observation).
- Cancellation: `CancellationToken` propagates across processes. Disconnecting a connection is equivalent to cancelling all unfinished calls on that connection.

## 8. Development Plan (This Work Item)

| Task | Description | Dependency | Estimate |
| --- | --- | --- | --- |
| P-1 | Transport-layer wrapper: pipe/UDS implementations + authentication frame + connection lifecycle (disconnect events, half-open detection) | A-2 | 3d |
| P-2 | Handshake and apiLevel negotiation + encoding negotiation; readable errors for rejection paths | P-1 | 2d |
| P-3 | Initial finalization of endpoint interfaces and DTOs (activate/deactivate/command/ping/events); MessagePack integer-key discipline + CI binary compatibility checks | P-2 | 3d |
| P-4 | Error-code space + SDK exception mapping | P-3 | 1d |
| P-5 | Streaming channel: stream marshaling wrapper, terminal-subscription throttling and coalescing, backpressure tests | P-3 | 3d |
| P-6 | Shared-memory surface channel (creation/authorization/reclamation protocol) | P-3 | 2d |
| P-7 | Protocol fuzz testing: out-of-order frames, oversized frames, invalid MessagePack, calls during the handshake (with the H-8 chaos group) | P-1..P-4 | 2d |

Acceptance: two-process loopback benchmark, empty-call P95 < 1 ms and 1 MB stream throughput ≥ 500 MB/s (local pipe); protocol fuzz testing produces zero host crashes and zero hangs.
