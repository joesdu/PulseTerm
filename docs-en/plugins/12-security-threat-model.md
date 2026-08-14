# 12 · Security and Threat Model

## 1. Trust Model (Layered Statements, No Overclaiming)

| Asset | v1 protection strength | Basis |
| --- | --- | --- |
| SSH credentials (password/private key/passphrase) | **Strong**: physically present only in the host process and OS credential store; unreadable through any RPC | D5 |
| Session channels (SFTP/exec/terminal) | **Strong**: reachable only through host capability APIs; every call passes through the Broker; revocable and audited | 06 |
| Host UI and other plugins | **Strong**: process isolation + all UI rendered by the host; no channel between plugins | D1/D6 |
| Plugin-private data | **Medium**: directories partitioned by plugin + secrets namespaces isolated; no mandatory isolation under the same OS user | — |
| Local file system | **Weak (v1)**: access through APIs is controlled by the Broker, but the plugin process can bypass it with direct syscalls | See §3 |
| Network egress | **Weak (v1)**: declaration + SDK auditing; no physical interception | See §3 |

Conclusion: v1's security promise is "**plugins cannot take your credentials,
cannot touch your servers unless authorized, and cannot bring down your main
program**." The claim that "a malicious plugin cannot read your local disk" is
**not valid in v1**. Source trust (signature tiers, see 10) is the gate. This
table is presented honestly in user documentation and on the plugin-installation
confirmation page.

## 2. Threat List and Mitigations

| # | Threat | Mitigation | Status |
| --- | --- | --- | --- |
| T01 | A malicious plugin impersonates another plugin in an update (supply chain) | Same public key required for upgrades of the same id; key changes require official-source endorsement; revocation list | v1 |
| T02 | Path traversal inside a package (zip-slip) writes arbitrary files | Extractor allowlist validation; reject `..`, absolute paths, and symbolic links | v1 |
| T03 | Another local process impersonates a plugin connecting to the host channel | Random pipe name + one-time token + pipe ACL (user SID)/socket 0600 | v1 |
| T04 | A plugin impersonates another plugin when making calls | Identity-bound connection; protocol has no identity parameter (05 §4) | v1 |
| T05 | Malicious or corrupt RPC payload crashes the host | Strict MessagePack mode, size limits, protocol fuzzing (P-7), deserialization allowlist | v1 |
| T06 | A plugin bombards the user with authorization dialogs to trick them into clicking | Dialog fatigue prevention (06 §2), publisher trust badge on dialogs, cooldown after rejection | v1 |
| T07 | A plugin injects destructive commands through `terminal.write` | High-sensitivity double confirmation, source badge, rate circuit breaker, non-disableable auditing | v1 |
| T08 | A plugin sends terminal secrets outside the machine (network/AI) | AI gateway egress notice and master switch; network egress in v1 can only be audited by declaration, a **residual risk** documented for users | Partial |
| T09 | The permissions file is modified by a malicious local program to escalate privileges | HMAC (key in OS credential store) + fail-closed invalidation of all permissions | v1 |
| T10 | A plugin exhausts resources (memory/CPU/handles) and drags down the system | Process isolation + monitoring and remediation (04 §4) + hard memory limit through Win Job Object | v1 (Win hard limit) |
| T11 | A crash loop creates harassment | Backoff restarts + Faulted circuit breaker | v1 |
| T12 | A plugin reads host or other-plugin memory | Separate processes, OS boundary; under the same user, debugging APIs could theoretically allow it, addressed by the OS sandbox roadmap | v2 |
| T13 | A malicious plugin directly uses syscalls to access files/network | Accepted in v1 (see §1); OS sandbox in v2 | v2 |
| T14 | Source index poisoning | Whole-index signature + package sha256 + HTTPS | v1 |
| T15 | A dev-mode backdoor that skips signing is abused | Dev loading enabled only for debug host builds or behind an explicit developer switch + persistent DEV badge in the UI | v1 |

## 3. OS-Level Sandbox Roadmap (v2+, Research Item)

Goal: raise the two "weak" areas in §1 to "medium/strong" and make file-system
and network permissions physically enforceable. Ordered by platform feasibility:

| Platform | Mechanism | Initial assessment |
| --- | --- | --- |
| Linux | Landlock (file scope) + seccomp (narrow syscall surface) + cgroups v2 (resources) | Most feasible, kernel ≥5.13 |
| macOS | `sandbox_init` profile (Apple semi-private API, but used by Chromium and others) or an App Sandbox-based helper | Feasible, needs refinement |
| Windows | Restricted token + Job Object as the starting point; AppContainer as the end state (pipe ACLs must be explicitly allowed in tandem) | Largest workload |

Principle: the sandbox is a **capability reducer**, not a replacement for the
permission system. The Broker and authorization UX remain unchanged; the
sandbox turns a "compliance contract" into "physically impossible." Research
starts at M8, beginning with Linux.

## 4. Security Checklist During Implementation (For Review)

- [ ] All capability implementations inherit from the Demand base class (B-7 analyzer enforced)
- [ ] Cross-process exceptions do not carry host stack traces or paths (05 §5)
- [ ] Shared-memory segment ACLs are restricted to the current user; surface reclamation is tested on the plugin-crash path
- [ ] Authorization-dialog wording has passed security review (must not entice users to make "always allow" the default focus; default focus = reject)
- [ ] Audit-log redaction is enabled by default (retain the filename in paths, truncate directories)
- [ ] Permissions files, source cache, and log-directory permissions are restricted (user read/write; no group/other access)
- [ ] Before release, run one "malicious modification" red-team exercise against each of the three official samples (at least one attack script for each T01–T11)

## 5. Development Plan (This Area)

Security work is distributed across the other areas (the corresponding tasks are
listed in the "Mitigation" column above). This area has these independent tasks:

| Task | Description | Dependencies | Estimate |
| --- | --- | --- | --- |
| X-1 | Two security-review gates: before design freeze (walk through this document) + before the M5 release (red-team exercise) | — | 2d each |
| X-2 | User-facing security documentation ("What plugins can and cannot do" page, five languages) | B-4 | 2d |
| X-3 | v2 sandbox research: Linux Landlock PoC (regress all capabilities after PluginHost self-restriction) | M8 phase | 5d |
