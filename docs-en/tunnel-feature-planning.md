# Tunnel Feature Planning (Plan)

> Updated 2026-07-12. The original items 1 through 4 under "To Implement" have all been delivered. Remaining iteration items are listed at the end.

## Implemented

- Three types are available: local forwarding (`-L`), remote forwarding (`-R`), and **dynamic forwarding (`-D` / SOCKS5)**. Choose the type from a dropdown at creation time. Dynamic forwarding automatically hides the target-host/port inputs.
- Select the remote host from saved SSH sessions in the explorer. Enter the service port manually.
- Inline tunnel actions: stop / start / delete. Deleting an active tunnel stops it automatically first.
- The form's "Cancel" action clears and resets it. Creation errors are shown inline.
- **Tunnel configuration persistence**: stored as a document per server in the SonnetDB `tunnels` collection (`docId = profileId`, through `IAppDataStore`). After an application restart, tunnels return as "stopped" and can be started with one click. Each server is restored only once per application run, and a restoration failure does not affect the panel.
- **Dedicated host session**: a tunnel's lifecycle is independent of terminal tabs. Creating or starting a tunnel while disconnected automatically establishes a background connection dedicated to tunnels. The background connection is automatically disconnected when the last tunnel for that server is removed.
- **Session-disconnect linkage, basic**: the status timer detects a dropped background session and marks its tunnel entries as "stopped."
- **Multi-device synchronization**: tunnel configuration is included in Gist cloud synchronization under the "connection configuration" scope (`plan.md` §13.C).

## To Implement (Future Iterations)

1. **Automatic reconnect recovery**: optionally recreate a session's tunnels after its host session reconnects. A disconnection currently only marks them as stopped and requires manual startup.
2. **Traffic statistics**: show the connection count / cumulative byte count for each tunnel inline. The design draft reserves a description row for this in fuXS7.
3. **Port-conflict precheck**: check whether the local port is occupied before creating local forwarding, and provide a clear error instead of a low-level exception.
