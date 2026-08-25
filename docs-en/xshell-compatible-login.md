# Xshell-compatible login (external launch)

> Audience: anyone wiring VelaShell into a jump server, SSO portal or ops console
> Code: `src/VelaShell.Infrastructure/Startup/`, `src/VelaShell/Services/ExternalLaunchCoordinator.cs`, `src/VelaShell/Services/UrlProtocolRegistration.cs`

## 1. The problem

In most enterprises, logging into a server does not go through the terminal's own connection manager. A user clicks
"open in terminal" on a jump-server or SSO page, the page hands a short-lived credential to a local security client,
and that client launches a terminal against the target host. **The user never knows the password** — it is a one-time
secret minted by the jump server and discarded after use.

The catch is that those third-party clients only speak the Xshell (and SecureCRT / PuTTY) calling convention. For
VelaShell to sit behind that button, it has to be launchable **the way Xshell is launched**. This feature is that
compatibility layer.

## 2. Accepted invocation forms

| Form | Example | Notes |
|---|---|---|
| `-url` | `VelaShell.exe -url ssh://root:one-time@10.0.3.21:2222` | The common case; what jump-server clients emit by default |
| `-newtab` | `VelaShell.exe -newtab ssh://root@10.0.3.21` | Equivalent to `-url` (sessions always open as tabs here) |
| Bare URL | `VelaShell.exe ssh://root@10.0.3.21` | Some callers put the URL straight into the first argument |
| Session file | `VelaShell.exe -f C:\Temp\session.xsh` | Reads the UTF-16 `.xsh` for host / port / user / protocol |
| Explicit options | `-l <user>` `-p <port>` `-pw <password>` `-i <keyfile>` | Override the matching URL fields, same precedence as Xshell |

Supported schemes: `ssh`, `sftp`, `ftp`, `ftps`. `telnet` / `rlogin` are recognised and reported as unsupported —
deliberately *not* dropped silently, otherwise the user clicks on the web page and nothing whatsoever happens.

**Deliberately not implemented**: Xshell's `-e` / `-s` (run a command or script on connect). Letting the caller also
say *what to run* turns a URL into remote code execution — the one capability in this compatibility layer that would
genuinely amplify harm, and it buys nothing but a saved paste.

URL parsing is hand-written rather than delegated to `System.Uri`: one-time secrets routinely contain unescaped
`@ : / #`, which makes `Uri` reject the string or cut the host in the wrong place. The rule is "the *last* `@` in the
authority separates credentials from host; user and password split at the *first* `:`", with `%XX` decoding applied.

## 3. URL scheme registration (ssh:// / sftp://)

Settings → Security & audit → External login → **Handle ssh:// and sftp:// links** (off by default).

- Windows: writes only `HKCU\Software\Classes\{ssh,sftp}` — current user, no administrator needed. The command is
  `"…\VelaShell.exe" -url "%1"`.
- Linux: writes `~/.local/share/applications/velashell-url-handler.desktop` and calls `xdg-mime`.
- macOS: schemes can only be declared by the app bundle's `Info.plist` (a packaging-time decision), so the toggle is
  a silent no-op there.

Turning the toggle off removes **only the entries VelaShell created** (recognised by the `VelaShellManaged` marker
value) — an association written by Xshell, MobaXterm or anything else is left alone.

## 4. Single-instance forwarding

When the app is already running, that click on the web page starts a **second process**. Instead of showing
"already running" and quitting, it now:

1. fails to take the single-instance mutex, so an instance exists;
2. hands the request (one-time credential included) to the running instance over a named pipe and waits for a
   one-byte acknowledgement;
3. exits silently only once acknowledged — the "already running" dialog is the fallback for a genuine delivery failure.

Both ends use `PipeOptions.CurrentUserOnly`: the ACL is narrowed to the current user on Windows, the socket is 0700
on Unix, and the client verifies the server's owner. Another user on the same machine can neither connect nor stand
up a fake server to phish the secret.

A second launch with no arguments (double-clicking the icon, or launching while hidden in the tray) is turned into a
"bring the window to the front" request.

## 5. Credential handling

The rule for one-time credentials is that they never touch disk:

- the profile built from a request has `RememberPassword = false` and is **never written to the session repository**
  (it does not show up in the session tree);
- the "recent connections" history only records host / port / user / duration — it never stored passwords;
- `ExternalLaunchRequest.ToString()` deliberately omits the password, because that string can end up in a `Trace` log
  at any time;
- when a request carries no credentials, a **saved profile for the same target** is reused, so your own stored
  password or key applies exactly as if you had clicked the sidebar entry.

## 6. Security analysis

This path differs from clicking "connect" in the UI in one fundamental way: **both the target and the credential come
from somebody else**. Risks and mitigations, item by item:

| Risk | Eliminable? | Where we stand |
|---|---|---|
| **Any local process can make the terminal connect anywhere**, including an `ssh://` link on a web page | No — a process running as you is equivalent to you | The **confirmation dialog**, on by default: target, source and whether credentials came with the request, with a "Connect" the user has to press. Targets can be trusted individually as `scheme://user@host:port` |
| **The password is visible on the command line** to other processes running as the same user (and to EDR / Sysmon process-creation logs) | No | The same exposure Xshell / SecureCRT / PuTTY have — it belongs to the calling convention itself. Mitigation lives at the source: have the jump server mint **one-time** secrets rather than long-lived passwords, and prefer `-f` session files or a local proxy port over `-pw` |
| A malicious page forging an `ssh://` link that points at an attacker-controlled host | Yes | The dialog shows the target verbatim, and scheme registration is off by default — without it, that entry point does not exist |
| Another user on the machine sniffing or hijacking the forwarding channel | Yes | `CurrentUserOnly` named pipe (Windows ACL / Unix 0700 plus owner check) |
| The one-time secret leaking into settings, logs or synced data | Yes | See the four guarantees above |
| External launches bypassing host-key verification | Yes | They do not — external launches reuse the same connection pipeline, so first-fingerprint confirmation and change blocking still apply |

**Recommendations for administrators**

1. Mint one-time secrets on the jump server and keep their lifetime to seconds; do not pass long-lived passwords with `-pw`.
2. Keep "Confirm before connecting" on. Turning it off means any local process can open a session silently — a real
   attack surface on shared terminals, demo machines, or anywhere users click links in a browser.
3. Only enable scheme registration if you actually need web pages to launch the app; otherwise point the jump server
   straight at the `VelaShell.exe` path and leave the system untouched.
4. The trusted-target list is clearable: Settings → Security & audit → External login → Clear.

**Bottom line**: being Xshell-compatible does not make VelaShell less safe than Xshell, because the risk comes from
the calling convention (password on the command line, any local process can initiate) rather than from the
implementation. The one place an implementation can differ is whether it asks first — this one does, by default.

## 7. Settings

| Setting (Security & audit → External login) | Default | Effect |
|---|---|---|
| Accept external login requests | On | Off means `-url`, `-f` and scheme handling are all ignored |
| Confirm before connecting | On | Show target and source, connect only on approval |
| Handle ssh:// and sftp:// links | Off | Writes / removes the current user's scheme association |
| Trusted external targets | Empty | Targets you chose to stop being asked about; clearable in one click |

## 8. Jump-server integration examples

```
# Point at the executable directly (recommended, zero system footprint)
"C:\Program Files\VelaShell\VelaShell.exe" -url ssh://%USER%:%ONETIME%@%HOST%:%PORT%

# Through the system scheme association (enable it in Settings first)
ssh://%USER%:%ONETIME%@%HOST%:%PORT%

# Drop a temporary session file (no credential on the command line; the user or a key authenticates)
"C:\Program Files\VelaShell\VelaShell.exe" -f "C:\Temp\jump-1234.xsh"
```
