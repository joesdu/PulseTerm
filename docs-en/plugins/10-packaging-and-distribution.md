# 10 · Packaging, Signing, and Distribution

> **The operational handbook is [publishing.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/publishing.md)** (build → pack → sign → submit).
> Marketplace: <http://market.easilynet.top> (the client has no built-in marketplace client yet;
> users download the `.vpx` and install it from the plugin manager page). This document is the
> long-term blueprint.
>
> **Implementation note (2026-08)**: **packaging and signing have shipped**; **distribution
> (registry / store) is still deferred**. Three places where the implementation departs from the
> blueprint below — the implementation wins:
>
> 1. **`.vpx` is a dedicated container**, not "a zip with a different extension": a 64-byte header
>    (magic `56 50 58 1A`, format version, flags, payload length, SHA-256, mask nonce, header CRC32)
>    followed by a masked zip payload and an optional signature block. See
>    `VelaShell.PluginSdk/Packaging/VpxContainer.cs` and [dev-guide.md §12](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs-en/dev-guide.md).
> 2. **The signature algorithm is ECDSA P-256 + SHA-256**, not the Ed25519 named below. Ed25519 is
>    not in the BCL, and taking a third-party dependency would break the rule that the contract
>    assembly — the only type source shared between host and plugins — has no heavyweight
>    dependencies. The signature covers the 64-byte header, which carries the payload length and
>    digest, so it is equivalent to signing the whole package.
> 3. **Signing is not mandatory**: for first-party and self-installed plugins, trust equals install
>    (consistent with the decision recorded in 06). Unsigned packages are allowed by default and
>    **an invalid signature is always rejected**; tighten this with
>    `PluginManagerOptions.RequireTrustedPackageSignature` plus `TrustedPackageKeys`. The three
>    trust tiers, TOFU, key continuity, and revocation lists below are future work for when a
>    third-party ecosystem opens up.

## 1. Signing and Source Trust

Given that v1 has no OS sandbox (see 06/12), "what the user installed and who
it came from" is the foundation of the security model, so signing is **required**
from the first release.

Plan: **Developer key signing + first-use trust (TOFU) + official-source endorsement**, in three tiers:

| Trust tier | Meaning | UI presentation |
| --- | --- | --- |
| Official plugin | Signed with the VelaShell team key | Blue "Official" badge |
| Verified publisher | Key registered with the official source and publisher verification completed (lightweight verification such as GitHub account binding) | Green "Verified: acme" badge |
| Unverified | Valid self-signature, but publisher is not registered | Yellow warning; installation requires secondary confirmation ("Install only when you trust the source") |

Mechanisms:

- Signature format: a SHA-256 manifest of all files in the package (`SIGNATURE`
  contains the manifest hash tree + Ed25519 signature + public key + optional
  counter-signature from the official source).
- Packages with no signature or a damaged signature are **rejected for
  installation** (except for dev-mode loading, which is explicitly marked DEV
  in the UI).
- **Upgrades of the same id must use the same public key** (key continuity to
  prevent update hijacking); key changes require an endorsement and transfer
  through the official source.
- The host contains the official source root public key; the revocation list is
  delivered with the source index (installed plugins from revoked publishers are
  marked red and disabled by default).

## 2. Installation and Update Pipeline

```text
Source (file/URL/source page) → download to temporary directory → secure zip extraction (prevent zip-slip)
 → signature verification → manifest validation (schema/engines/platform/permission list)
 → permission preview page (show each item, mark dangerous permissions in red; highlight "new permissions" during upgrades)
 → user confirmation → write to installed/<id>/<ver>/ → register contributions → complete (no restart)
```

- Update checks: compare versions against the source index in the background
  (daily by default; can be disabled); updates use the atomic version-pointer
  switch from 03 §7, with rollback on failure.
- Automatic updates are **off by default** (the conservative operational-tool
  stance: unexpected changes on a target machine are a source of incidents);
  provide an option to "automatically install security updates only" (source
  index entries marked with the `security` flag).
- Downgrade: the management page allows reverting to the previous version cached
  locally as an escape hatch for troubleshooting.

## 3. Plugin Sources (Registry)

Stage the rollout to avoid building a service too early:

**Stage A (v1), static index source**: a signed JSON index hosted on GitHub
(Pages/Release). The host can configure multiple sources (enterprises can host
the same format on their internal network to support internal distribution):

```jsonc
// index.json (signed as a whole with the official key)
{ "version": 1, "generatedAt": "...",
  "plugins": [ { "id": "acme.image-viewer", "latest": "1.2.0",
      "versions": [ { "version": "1.2.0", "engines": {"velaShell": ">=0.2.0", "apiLevel": 1},
          "url": "https://.../image-viewer-1.2.0.vpx", "sha256": "...",
          "publisherKey": "ed25519:...", "permissions": ["remote.files.read"],
          "security": false } ],
      "displayName": {"en": "...", "zh-Hans": "..."}, "description": {...},
      "icon": "https://...", "verifiedPublisher": true } ],
  "revokedPublishers": ["ed25519:..."] }
```

**Stage B, official marketplace**: a dynamic service (search, download counts,
ratings, publisher portal, and automated scanning), to be funded only once the
ecosystem reaches sufficient scale; the index format remains forward-compatible
with Stage A.

## 4. In-App Plugin Management Page

Add a top-level "Plugins" page to Settings:

- **Installed**: card list (icon/name/publisher trust badge/version/status color
  dot); inline actions: enable/disable/uninstall/restart/view logs; expanded
  details: permissions, recent activity (audit), resource usage (memory graph),
  and ungraceful-exit count.
- **Browse**: aggregate indexes from all configured sources, with search and
  categories; details page = rendered README + permission list + version history.
- **Visible status**: Faulted in red, unresponsive in yellow, running in green,
  inactive in gray, corresponding one-to-one with the state machine in 04.
- Five languages: page text follows the existing five-language resx parity
  process; plugin-provided text uses the multilingual fields in the source
  index, falling back to English when missing.

## 5. Development Plan (This Area)

| Task | Description | Dependencies | Estimate |
| --- | --- | --- | --- |
| D-1 | Finalize signature format + signing/verification library + `vela-plugin sign` / `new keypair` | S-3 | 3d |
| D-2 | Installation pipeline (secure extraction, validation chain, permission preview, transactional write) | M-5, D-1 | 3d |
| D-3 | Source client: index fetching/caching/multi-source merging/revocation handling; update checker | D-2 | 3d |
| D-4 | Plugin management page (installed view, including status/log/resource/audit entry points) | H-5, B-6 | 5d |
| D-5 | Browse and details pages + install/update/rollback from a source | D-3, D-4 | 4d |
| D-6 | Establish official source repository (GitHub), index-generation script, and publisher-verification process documentation | D-1 | 2d |

Acceptance: complete the full flow from "search in Browse → install official
image-viewer → authorize → use → upgrade → rollback → uninstall" without a
restart; tampered package, key-change upgrade, and revoked-publisher attack
cases must all be rejected with readable explanations.
