# Route Tracing (Trace Route) Design

> Drafted on 2026-07-25. Goal: build an mtr-level link tracing panel into VelaShell that answers
> "From here to this server, which hop is slow, and which hop is dropping packets?" This document defines the design only; it has not been implemented.

## 1. Why Build It In

Users already know how to use `mtr` / `tracert`. The value of building this in is not repeating those tools, but doing three things they cannot:

1. **The target is already available**. The tracing target is the currently focused session configuration, so there is no need to copy an IP manually. When a jump host is configured (`SessionProfile.JumpHostProfileId`), tracing can be performed segment by segment. This topology is completely invisible to external tools.
2. **Both directions can be measured**. The client side (local machine → server) investigates "the network on my side"; the remote side (server → any target) investigates "the server's outbound connectivity". In operations work, eight out of ten incidents require comparing these two sets of data to locate the problematic segment.
3. **The conclusion can be archived**. Tracing results can be exported directly and pasted into a ticket, within the same interface system as session records and connection diagnostics.

Conversely, what this feature will not do is equally clear: no continuous background monitoring, no alerting, and no batch probing of multiple targets. Those belong to monitoring systems.

## 2. Key Premise: Packet Loss Can Mislead

This is the easiest part of the entire feature to get wrong, and the part most likely to mislead users. It must appear at the very beginning of the design.

Packet loss shown at an intermediate hop is **not a fault in the vast majority of cases**. Routers rate-limit the act of replying to "TTL expired, return an ICMP Time Exceeded" (`net.ipv4.icmp_ratelimit` by default on Linux, and similarly on commercial routers), or do not reply at all. The forwarding data plane is working normally; the control plane is simply not responding to you.

Verdict rules, which both the UI and exported reports must present consistently:

- **Only packet loss at the final hop, the target itself, represents end-to-end packet loss.**
- Packet loss at one hop with **no loss at subsequent hops** is classified as `ICMP rate limited`, shown in gray, and excluded from alerts.
- Packet loss that **continues from one hop all the way to the final hop** is classified as `suspected real packet loss` and shown in red.
- A hop with no response at all (`* * *`) but with responses from subsequent hops is classified as `node not responding`, not a broken link.

The same applies to latency: when one hop has a high RTT but subsequent hops are normal, that router's control plane is slow, not the link. **Latency should only be compared monotonically along the route**. A hop that is clearly higher than the preceding hop is the suspect.

If these three rules are not built into the verdict logic, this feature will produce a table that causes users to draw the wrong conclusions.

## 3. Data Model

Place it in `src/VelaShell.Core/Diagnostics/Trace/`.

```csharp
/// The result of a single probe.
public sealed record TraceProbe(
    int Ttl,
    IPAddress? Address,     // null = no response before timeout
    TimeSpan? Rtt,          // null = timeout
    ProbeStatus Status);    // TimeExceeded / Reached / TimedOut / Unreachable / Error

/// Cumulative statistics for one hop (TTL), aligned with mtr columns.
public sealed class TraceHop
{
    public int Ttl { get; init; }

    /// All addresses observed at the same TTL. Under ECMP multipath routing, a hop may return multiple IPs;
    /// keeping only the last one would make the route appear to jump.
    public IReadOnlyList<IPAddress> Addresses { get; }

    public string? HostName { get; }        // rDNS (PTR), null if unresolved or failed
    public string? AsnLabel { get; }        // "AS4134 CHINANET", not queried by default, see §8
    public int Sent { get; }
    public int Received { get; }
    public double LossPercent { get; }      // (Sent - Received) / Sent * 100
    public TimeSpan? Last { get; }
    public TimeSpan? Best { get; }
    public TimeSpan? Worst { get; }
    public TimeSpan? Average { get; }
    public double StdDevMs { get; }
    public double JitterMs { get; }         // mean absolute difference between adjacent RTTs
    public IReadOnlyList<TimeSpan?> Recent { get; } // latest N samples, for the mini line chart
    public HopVerdict Verdict { get; }      // Verdict from §2
}

public enum HopVerdict { Ok, NoResponse, IcmpRateLimited, SuspectedLoss, Unreachable }

/// The complete result of one trace.
public sealed class TraceResult
{
    public string Target { get; init; }         // hostname entered by the user
    public IPAddress? ResolvedAddress { get; init; }
    public TraceProtocol Protocol { get; init; }
    public TraceOrigin Origin { get; init; }    // Local / Remote
    public IReadOnlyList<TraceHop> Hops { get; }
    public bool TargetReached { get; }
    public int Rounds { get; }
    public DateTimeOffset StartedAt { get; init; }
}

public enum TraceProtocol { Icmp, TcpSyn, Udp }
public enum TraceOrigin { Local, Remote }
```

Statistics use **incremental updates** (Welford variance) rather than retaining every historical sample. A trace can run for dozens of minutes, so each hop retains only the latest N=60 samples for charting.

## 4. Probe Method Selection

### 4.1 Client Side (P0)

Use `System.Net.NetworkInformation.Ping` + `PingOptions.Ttl` to send ICMP Echo probes with TTL=1..n. Intermediate routers return `TimeExceeded` (`IPStatus.TtlExpired`), and the target returns `EchoReply` (`IPStatus.Success`).
This is the classic traceroute approach. `Ping` is available on all three platforms, so raw sockets are not needed.

```csharp
using var ping = new Ping();
PingReply reply = await ping.SendPingAsync(
    address, timeout, buffer, new PingOptions(ttl, dontFragment: true));
// reply.Status: TtlExpired → intermediate hop, reply.Address is that hop's address
//               Success    → target reached
//               TimedOut   → no response from that hop
```

Permission reality, which must be stated honestly in the document and UI instead of making users guess at an empty table:

| Platform | ICMP + TTL | Description |
|---|---|---|
| Windows | No administrator required | Uses `IcmpSendEcho2`; a regular user is sufficient |
| Linux | Available on most distributions | .NET prefers `SOCK_DGRAM` ICMP, subject to `net.ipv4.ping_group_range`. If the user is not in an allowed group, it falls back to the system `ping`; **that path cannot provide TTL semantics**, so it must be detected and explained |
| macOS | No administrator required | `SOCK_DGRAM` ICMP is open by default |

The fallback path on Linux is a real trap: when `Ping` falls back internally to `/bin/ping`, `PingOptions.Ttl` has no effect, and the result becomes "every hop hits the target directly." At startup, first run a self-check (send TTL=1 to a public address and see whether it returns TtlExpired). If the self-check fails, switch to the external command mode in §4.3 and explain the reason in the UI instead of presenting a fake table.

### 4.2 TCP SYN Tracing (P2)

When ICMP is dropped by firewalls along the route, ICMP tracing turns into `*` all the way through even though SSH connects normally. In this case, the tool must switch to "send a TCP SYN with incrementing TTL to port 22 on the target." This more closely follows the path of real application traffic, and ECMP hashing also uses the five-tuple.

The cost is that constructing SYN packets and receiving ICMP replies requires raw sockets. On Windows this requires administrator privileges, and on Linux it requires `CAP_NET_RAW`.
Therefore this is P2, and the user is actively prompted to switch only when the entire ICMP trace times out.

### 4.3 Remote Side / External Command Mode (P1)

Run the remote tool directly over the existing SSH connection, reusing `ISshClientWrapper.RunCommandAsync` (the same channel as the task manager, with no new connection required):

1. Prefer `mtr --report --report-cycles N --json --no-dns <target>`. Its JSON output fields (`Loss%`/`Snt`/`Last`/`Avg`/`Best`/`Wrst`/`StDev`) map directly to `TraceHop`, with no parsing ambiguity.
2. Fall back to `traceroute -n -q 3 -w 1 -m 30 <target>` and parse the text with regular expressions.
3. Fall back again to `tracepath -n <target>` (included by default on Debian-based systems and requiring no root privileges).
4. If none are available, honestly report "mtr/traceroute/tracepath is not installed on the remote host" and provide installation commands. **Do not guess or fabricate data.**

Probe commands use the same segment marker convention as the task manager (`echo __MTR__; ...`). First determine which tools are available on the remote host in one pass, rather than trying all three on every round.

The same parser is also used by **local external command mode** (the Linux fallback path in §4.1). The only difference is whether the executor is a local process or an SSH channel, so one `ITraceCommandRunner` interface with two implementations is sufficient.

## 5. Collection Scheduling

- **Round model**: one round sends one probe for each TTL from 1..maxTtl. Probes within a round are sent concurrently, with one `Ping` instance per TTL. The wall-clock time for a round is approximately one individual timeout, not 30 × the timeout.
- **Default parameters**: maxTtl=30, single-probe timeout 1s, interval between rounds 1s, and 60 samples retained in `Recent`.
- **Two modes**:
  - *Continuous* (mtr style, default): run until the user stops it, with statistics continuously converging. This is required for identifying jitter and intermittent packet loss.
  - *Single run* (traceroute style): finish after three probes per hop and produce a static table, suitable for pasting into a ticket.
- **Convergence pruning**: after a TTL returns `Success` (target reached) for three consecutive rounds, stop probing larger TTLs and mark that hop as the endpoint.
- **Stop conditions**: target reached, maxTtl reached, user stop, or session closed.
- Cancellation is available throughout (`CancellationToken`). Closing the window cancels the operation, leaving no background thread continuing to generate network traffic.

## 6. Architectural Placement

Follow the repository's existing layering and introduce no new pattern:

| Layer | Types | Responsibility |
|---|---|---|
| `VelaShell.Core/Diagnostics/Trace` | `ITraceRouteService`, `TraceHop`, `TraceOptions`, `TraceReportParser` | Contracts + pure-function parser, suitable for unit testing |
| `VelaShell.Infrastructure/Diagnostics` | `PingTraceRouteService` | Client-side ICMP implementation |
| Same as above | `CommandTraceRouteService` | External command implementation, with local process and SSH channel runners |
| `VelaShell/ViewModels` | `TraceRouteViewModel`, `TraceHopViewModel` | Row reuse, sorting, incremental statistics, and export |
| `VelaShell/Views` | `TraceRouteView.axaml` | Independent modeless window |

Register the service as a singleton in `InfrastructureServiceCollectionExtensions`, alongside `IRemoteProcessService`.

The interface returns a streaming callback so the UI can refresh while the trace runs instead of waiting for the entire operation to finish:

```csharp
public interface ITraceRouteService
{
    IAsyncEnumerable<TraceUpdate> RunAsync(TraceOptions options, CancellationToken ct = default);
}
```

## 7. UI Design

**Entry points**: two locations, using the same registered command, `tools.trace`.

1. Add an icon to the title-bar function area (`Icon.route` is already occupied by tunnels, so add `Icon.milestone`). The target defaults to the currently focused session. It remains available under a local terminal tab, where the target can be entered manually; under an SFTP tab, it traces the host of the current session.
2. Add a "Link tracing" step to the connection diagnostics center (design RGXg1), alongside the existing four checks. When a connection cannot be established, this step can directly show which hop is broken.

**Window layout**: an independent modeless window, aligned with the task manager specification, with a custom-drawn title bar, resizable dimensions, and an opaque background:

```
┌ Link Tracing — Production Database ─────────────────── ✕ ┐
│ Target [10.0.3.17        ] Protocol [ICMP ▾] Source [Local ▾] │
│ [Start] [Stop]   Max hops 30  Timeout 1s  ☑ Resolve hostnames │
├───────────────────────────────────────────────────────────────┤
│  #  Host                 Loss%  Snt  Last  Avg  Best  Wrst  StDev  Recent │
│  1  192.168.1.1           0.0%   24   0.8  0.9   0.7   2.1    0.2  ▁▂▁▁ │
│  2  100.64.0.1           16.7%   24  12.4 13.1  11.8  40.2    5.6  ▂▃▂█ │  ← Gray: ICMP rate limited
│  3  * * *                  —     24    —    —     —     —        —        │
│  4  219.158.x.x           0.0%   24  31.2 32.0  30.9  55.1    3.1  ▂▂▃▂ │
│  5  10.0.3.17 (target)      2.1%   24  33.0 33.8  32.4  61.0    4.2  ▂▃▂▄ │  ← Red: real packet loss
├───────────────────────────────────────────────────────────────┤
│ Target reached · 5 hops · end-to-end 33.8ms · 24 rounds   [Export] [Copy] │
└───────────────────────────────────────────────────────────────┘
```

Details:

- **Row reuse**: as in the task manager, reuse row objects by TTL and update fields only. The route itself is stable, so rebuilding the entire table would make the scroll position and selected item disappear once per second.
- **Mini line chart**: show the RTT trend for the latest 60 samples, with timeouts drawn as gaps. Jitter is immediately visible and easier to understand than a StDev number.
- **Coloring**: follow `HopVerdict` from §2 strictly, rather than simply showing every packet loss in red.
- **Multiple addresses**: when a hop has multiple addresses, show the first in the main row and mark the right side with `+2`; expand on hover. This is normal for ECMP, so it should be visible without taking over the interface.
- **Context menu**: copy this hop's IP, copy the entire row, continuously ping this hop alone, or look up the ASN for this IP. The ASN action requires explicit enablement; see §8.
- **Export**: plain text, using mtr `--report` style formatting for direct pasting into tickets, and JSON.

## 8. Privacy

rDNS uses the system DNS resolver for PTR queries. This is ordinary network behavior, enabled by default, and can be disabled.

**ASN and geolocation are disabled by default, and no online lookup is built in.** Enabling them means sending the IP of every hop along the route to a third-party API. These IPs reveal the user's network path and approximate region. The user must explicitly check the option, and the option must state which service will receive the data.
If this is implemented later, prefer an offline database file supplied by the user instead of connecting to the internet by default.

No telemetry is reported. Trace results remain on the user's local machine, and the user decides whether to export them.

## 8.5 Implementation Progress (2026-07-25)

**Delivered (P0)**

- `Core/Diagnostics/TraceRoute.cs`: incremental `TraceHop` statistics (Loss/Last/Best/Worst/Avg/StdDev/Jitter, capped at the latest 60 samples), and `TraceAnalysis.ApplyVerdicts` implementing the three verdict rules from §2.
- `Infrastructure/Diagnostics/PingTraceRouteService.cs`: ICMP + incrementing TTL, an in-round concurrency limit of 8, convergence pruning after the target is reached, PTR reverse lookup with a 2s timeout and in-process cache, and a first-round TTL effectiveness self-check, the Linux fallback trap from §4.1.
- UI: a `map-pinned` title-bar button, next to the SFTP file manager with the same enablement conditions. The panel and file manager **share the bottom panel area and are mutually exclusive**. When opened, the target is filled automatically with the current session host.
- Eight verdict/statistics unit tests plus real-device end-to-end verification. rDNS, rate-limited hops, and packet-loss hop verdicts are all correct.

**Windows verification**: TTL probing works without administrator privileges, and `Ping` + `PingOptions(ttl, dontFragment)` behaves as expected.

**Not implemented: world map.** Placing every hop on a map requires IP geolocation data. There are only two options: query a third party online, which would send the user's entire route of IPs and is exactly what §8 is intended to avoid, or bundle an offline database, which raises size and licensing issues. This is a product decision and should not be made implicitly by the implementation. Once the data source is selected, the map itself, a simplified world map with hop arcs, is straightforward drawing work.

## 9. Phasing

**P0, usable**
- `ITraceRouteService` + `PingTraceRouteService` (ICMP, continuous and single-run modes)
- Linux TTL self-check and fallback notification
- Independent window: table, basic statistics, start/stop
- The three verdict rules from §2

**P1, useful**
- Remote tracing (three-level fallback: mtr JSON / traceroute / tracepath)
- rDNS resolution, mini line chart, text and JSON export
- Add to the connection diagnostics center as the fifth step
- Segment-by-segment tracing through jump hosts, one segment per `JumpHostProfileId`

**P2, complete the edges**
- TCP SYN tracing when ICMP is filtered throughout
- ASN/geolocation, explicitly enabled, with offline database preferred
- Expanded view for multiple addresses at one hop

## 10. Test Strategy

- **Parser**: `TraceReportParser` is a pure function. Use real `mtr --json` / `traceroute` / `tracepath` output samples in table-driven tests, including malformed input with every intermediate hop as `*`, multiple addresses at one hop, and hostnames containing parentheses. Use the same style as `RemoteProcessProbeTests`.
- **Statistics**: given a sequence of RTTs, assert Loss/Avg/Best/Wrst/StDev/Jitter and the verdict from §2. A dedicated test is required for "intermediate hop loses packets but final hop does not → IcmpRateLimited", because it is the branch most likely to be implemented backwards.
- **Scheduler**: inject a fake probe sender and verify round advancement, convergence pruning, stop-on-reach, and stop-on-cancel without touching the real network.
- **UI**: reuse existing headless tests to verify row reuse and verdict coloring. Do not add end-to-end tests that require a real network.
