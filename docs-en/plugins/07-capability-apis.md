# 07 · Capability APIs

Everything a plugin can do goes through a capability domain. Each domain maps to an SDK proxy interface on the plugin side, an RPC method family (05), and a host capability service, where the permission `Demand` bridges to an existing subsystem. This document defines the apiLevel 1 domain list and key signatures, in C# from the SDK perspective.

Design discipline:

- Every method documents its required permission. A method without permission is absent from the list and therefore absent from the protocol.
- Everything is asynchronous and cancellable. Long-running operations may include `IProgress<T>`.
- Paths, sessions, and other identifiers use opaque IDs issued by the host. Plugins never access internal objects.
- Bridge implementations **depend only on neutral interfaces in `Core.*`**, such as `Core.Ssh.ISftpClient`, continuing the rule that “library types do not leave Infrastructure”.

## 1. vela.sessions, Sessions

Permissions: `sessions.observe.basic` (redacted list) / `sessions.read` (full) / `sessions.create` (start).

```csharp
public interface ISessions
{
    Task<SessionInfo[]> ListAsync(CancellationToken ct);          // Fields are redacted according to the permission level
    IAsyncEnumerable<SessionEvent> WatchAsync(CancellationToken ct); // Connect/disconnect/reconnect events
    Task<string> ConnectAsync(string savedProfileId, CancellationToken ct); // sessions.create
}
```

## 2. vela.remoteFs, Remote Files (Core Scenarios S1/S2/S7)

Permissions: `remote.files.read` / `remote.files.write`, with scope supporting sessions and path prefixes. The implementation bridges the existing SFTP wrapper. Note the existing semantics: `GetAttributes` returns null for a nonexistent path and does not use an exception to signal existence. The SDK's `StatAsync` likewise returns a nullable value.

```csharp
public interface IRemoteFs
{
    Task<RemoteEntry[]> ListAsync(string sessionId, string path, CancellationToken ct);
    Task<RemoteEntry?> StatAsync(string sessionId, string path, CancellationToken ct);
    Task<Stream> OpenReadAsync(string sessionId, string path, CancellationToken ct);
    Task<Stream> OpenWriteAsync(string sessionId, string path, WriteMode mode, CancellationToken ct);
    Task DownloadAsync(string sessionId, string remotePath, string localPath,   // localPath also requires fs.local.write
                       IProgress<TransferProgress>? progress, CancellationToken ct);
    Task UploadAsync(...);
    Task DeleteAsync/RenameAsync/MkdirAsync(...);
    IAsyncEnumerable<RemoteChange> PollWatchAsync(string sessionId, string path,
                       TimeSpan interval, CancellationToken ct);  // Polling watch, minimum interval 2s (protects the remote)
}
```

Host-side protections: a per-plugin limit on concurrent SFTP operations (4 by default), staggering of bandwidth use against the user's foreground transfers (plugin traffic enters a low-priority queue), and throttled progress callbacks (≥100 ms). This incorporates the historical lesson of large-transfer stalls: plugin traffic must not flood the UI scheduler.

## 3. vela.remoteExec, Remote Execution (S5/S6)

Permission: `remote.exec` (high sensitivity). Uses a **dedicated exec channel**, does not enter the user's terminal, and does not contaminate the user's shell history or environment.

```csharp
public interface IRemoteExec
{
    Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions opts, CancellationToken ct);
    // ExecOptions: timeout (30s by default, maximum 10min), stdin, environment variables, maximum output (4MB by default, with truncation marker)
    IAsyncEnumerable<ExecOutputChunk> StreamAsync(...);            // Streaming output for long-running commands
}
```

Auditing: the complete command text for every execution is written to the audit log (§06-4) and cannot be disabled.

## 4. vela.terminal, Terminal (S3/S4)

Permissions: `terminal.read` / `terminal.write` (high sensitivity).

```csharp
public interface ITerminal
{
    IAsyncEnumerable<TerminalOutput> SubscribeAsync(string sessionId, CancellationToken ct);
        // Host-side frame-coalescing throttle ≥16 ms; call Demand once when the subscription is established, and cut the stream on revocation
    Task<TerminalSnapshot> GetSnapshotAsync(string sessionId, CancellationToken ct);  // Screen + tail of scrollback (line limit)
    Task<string?> GetSelectionAsync(string sessionId, CancellationToken ct);
    Task WriteAsync(string sessionId, string input, CancellationToken ct);
        // Through the existing input serialization queue (do not write directly to the stream); ≤4 KB per call by default
    Task<string> CreateOutputChannelAsync(string title, CancellationToken ct);
    Task AppendToChannelAsync(string channelId, string text, CancellationToken ct);
        // Plugin-specific read-only output page (attached as a VelaDock document); requires no terminal permission, only ui.contributions
}
```

Additional `terminal.write` safeguards: injected input is briefly marked in the terminal with a superscript identifying the source plugin, which the user can disable in settings. Per-plugin write frequency is rate-limited (20 calls/min by default to prevent runaway screen spam); exceeding the limit trips a circuit breaker and notifies the user.

## 5. vela.localFs, Local Files

Permissions: `fs.local.read` / `fs.local.write` (scope = directory prefix). **Permission-free paths**: paths returned by `PickFileAsync` / `PickFolderAsync`, where the host displays the system picker, automatically receive temporary authorization. The user made the selection directly, following the Android SAF model. Plugins are encouraged to prefer the picker over requesting a broad directory scope.

```csharp
public interface ILocalFs
{
    Task<string?> PickFileAsync(FilePickerOptions opts, CancellationToken ct);
    Task<string?> PickFolderAsync(..., CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<Stream> OpenWriteAsync(string path, WriteMode mode, CancellationToken ct);
    Task<LocalEntry[]> ListAsync(string path, CancellationToken ct);
    // Regular Stat/Delete/Move/Copy…
}
```

## 6. vela.storage / vela.secrets / vela.settings

```csharp
public interface IStorage      // Permission: storage.private (normal)
{
    Task<T?> GetAsync<T>(string key);   Task SetAsync<T>(string key, T value);
    Task RemoveAsync(string key);       string DataDirectory { get; }   // Direct file access is also restricted to this directory
}
public interface ISecrets      // Permission: secrets (dangerous); namespace forced to <pluginId>/
{
    Task<string?> GetAsync(string name); Task SetAsync(string name, string value); Task DeleteAsync(string name);
}   // Uses the OS credential store underneath (DPAPI/Keychain/libsecret), a separate namespace from host credentials and invisible to each other
public interface IPluginSettings   // Permission: settings.own (normal)
{
    T Get<T>(string key);  Task SetAsync<T>(string key, T value);
    IAsyncEnumerable<SettingChange> WatchAsync(CancellationToken ct);   // Pushed when the user changes a setting in the settings page
}
```

## 7. vela.ui, UI (See 08)

Permission: `ui.contributions` (normal). Includes commands/menu items/status-bar dynamic updates, VelaUI surfaces (panels and document pages), dialogs (Message/Confirm/Input/QuickPick), in-app notifications (rate limit: ≤6 per plugin per minute), and progress indicators (in the status bar or notifications).

## 8. vela.events, Host Event Subscriptions (S5)

Permission: most event payloads follow the permission for the corresponding domain, such as session events requiring `sessions.read`. `appStartup/appShutdown/themeChanged/localeChanged` require no permission.

```csharp
public interface IEvents
{
    IAsyncEnumerable<HostEvent> SubscribeAsync(EventFilter filter, CancellationToken ct);
    // HostEvent: SessionConnected/Disconnected, TransferCompleted, ThemeChanged,
    //            LocaleChanged, AppShutdown (gives the plugin a chance to flush), ScheduleFired (see 11)
}
```

## 9. vela.audio, Audio Output (S2)

Permission: `audio.playback`. Design choice: **the plugin decodes, the host outputs**. The host does not include codecs, avoiding codec dependency bloat and licensing issues. It only provides PCM mixing output. MP3 decoding is supplied by the plugin using a managed decoder, such as NLayer, as demonstrated by the official sample.

```csharp
public interface IAudio
{
    Task<IAudioTrack> CreateTrackAsync(AudioFormat fmt, CancellationToken ct); // fmt: sample rate/bit depth/channels
    // IAudioTrack: WriteAsync (PCM chunks, backpressure), pause/resume/stop, volume, position feedback
}
```

Host output backend: pending a selection spike, with candidates including a miniaudio binding, OpenAL Soft, and three platform-native implementations using WASAPI + CoreAudio + ALSA. The placeholder interface comes first, so selection does not block other domains. Global mixing applies at a given time. Plugin volume is constrained by the host master volume and mute switch. When a plugin is disabled or crashes, its audio track is muted and reclaimed immediately.

## 10. vela.net, Network (Declarative)

Permission: `network` + a domain-list declaration in the manifest. v1 provides no physical interception (see the premise in 06). The SDK provides `ctx.Http`, a convenience wrapper based on HttpClient that automatically includes the plugin UA. Requests made through the SDK wrapper are recorded through the host audit channel with the target domain. Directly constructing an HttpClient cannot be prohibited in v1. Compliance depends on review and signatures, and the documentation states this limitation plainly.

## 11. vela.clipboard / Notifications / i18n

- `clipboard.read/write`: read/write the system clipboard. Read is a dangerous permission because the clipboard often contains passwords.
- `notifications.system`: OS-level notifications. In-app notifications belong to vela.ui and do not require this permission.
- `i18n.read`: the current locale and translated common host strings, such as the “OK” and “Cancel” buttons, so plugin copy matches the host.

## 12. vela.ai, AI Gateway (Longer Term, See 11-automation-and-ai)

Permission: `ai.invoke`. Reserved in apiLevel 1, with the interface held for future use and the host able to declare the capability unavailable. Full implementation is covered in [11-automation-and-ai.md](11-automation-and-ai.md).

## 13. Development Plan (This Work Item)

Batch divisions correspond to the milestones in 14:

| Task | Content | Dependency | Estimate |
| --- | --- | --- | --- |
| C-1 | Capability-service foundation: registry, Demand base class, audit hooks, per-plugin rate limiter | B-2, P-3 | 3d |
| C-2 | First batch: storage / settings / secrets / events (foundation domains, minimum dependencies for sample plugins) | C-1 | 4d |
| C-3 | Second batch: sessions / remoteFs (bridge Core.Ssh, low-priority transfer queue, progress throttling) | C-1 | 5d |
| C-4 | Third batch: localFs (picker temporary-authorization model) + clipboard + notifications | C-1 | 3d |
| C-5 | Fourth batch: terminal (subscription frame coalescing, snapshots, write-rate circuit breaker) + remoteExec (auditing) | C-3 | 5d |
| C-6 | Audio-output spike (evaluation report for three candidates) → IAudio implementation | C-1 | 5d |
| C-7 | vela.net SDK wrapper + auditing | C-1 | 1d |
| C-8 | Contract test suite: for every domain, “permission matrix × normal/boundary/cancellation/disconnection” cases (connects to 13) | Each batch | Ongoing |

Acceptance: the domains required by S1 (C-2/C-3) support the image-viewer sample end to end. Once C-5 is complete, all domains for S3/S4/S5 are available.
