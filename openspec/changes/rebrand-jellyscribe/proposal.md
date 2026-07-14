## Why

`add-serializd-tv-sync`'s Phase 1 (TV sync to Serializd) made the current name
and visual identity actively wrong: "LetterboxdSync" describes one of the two
diary services the plugin now writes to, and the site's favicon is Letterboxd's
own three-circle logo mark in Letterboxd's own brand colors (`#00E054`,
`#40BCF4`, `#FF8000`), with the CSS variables literally named `--lb-green` and
`--jf-purple`. That was a reasonable identity for a film-only Letterboxd sync
tool; it is real trademark exposure for a tool that now also syncs to a second,
unrelated service, with a plausible third (Backloggd, games) down the road.

This change closes the two open decisions `add-serializd-tv-sync` section 6
left for "decide in tasks, not blocking Phase 1": the new name, and the new
visual identity. Both are decided here, with the research and rationale
recorded, so `add-serializd-tv-sync`'s task 6.1 can point at this change
instead of holding an open question.

**New name: Jellyscribe.** The naming direction went through three real
rejections before landing here, each closing off a whole branch rather than
just one candidate — recorded in full in `design.md` because the reasoning
matters as much as the answer:

1. An `-arr`-suffixed name (`Diaryarr`) was the first pick, on the theory that
   the self-hosted Jellyfin audience already trusts that convention. Rejected:
   this plugin isn't part of the Sonarr/Radarr/Prowlarr interoperating PVR
   family (it doesn't acquire or organize media), so borrowing that suffix's
   cachet for a tool outside that family has the same "riding on someone
   else's credibility" problem the rebrand exists to fix — just aimed at a
   different community instead of a different company.
2. A literal `Jellyfin`-in-the-name pick (`JellyfinSync`) was considered next.
   Rejected outright: Jellyfin's own branding guidelines discourage exactly
   this. `JellySync` (the shortened form) was also checked and separately
   rejected on a plain collision — it's a real, existing plugin
   (`SamVellaUK/JellySync`) doing multi-server sync, a different job.
3. A plain, unprefixed name (`Watchdiary`) was the safe fallback. Landed on
   `Jellyscribe` instead: Jellyfin's guideline against `Jelly[word]` names is
   phrased as a request, not an enforcement mechanism, and there's real
   precedent of it being tolerated for successful plugins (Jellyscrub,
   Jellystat) — the guideline is really aimed at client apps competing for
   app-store shelf space under a name that reads as officially blessed, which
   doesn't apply to a server plugin installed via a repository URL.

**New visual identity.** Retires the borrowed Letterboxd/Jellyfin colors for an
original palette (warm near-black ground, single brass accent, muted ledger
green for synced/success states only) and type system (Fraunces for display,
Public Sans for body, JetBrains Mono kept for data/code — continuity with the
existing admin-UI choice). Full rationale, hex values, and a rendered preview
in `design.md` and the linked artifact.

## What Changes

- Plugin display name (shown in Jellyfin's Dashboard > Plugins) changes to
  "Jellyscribe". `AssemblyName`/`FileVersion` identifiers change; the **GUID
  does not** (Jellyfin keys plugins by GUID, so this is what keeps existing
  installs auto-updating through the rename). The internal C# namespace stays
  `LetterboxdSync` for this release — a mechanical rename sweep is real,
  large-diff, zero-user-value work; deferred, not forgotten.
- GitHub repo renamed (pending the `raw.githubusercontent.com` redirect
  verification in tasks.md 0.1); site rebuilt on the new palette/type/mark; new
  domain live, `letterboxdsync.dev` 301s to it and stays registered.
- README, AI.md, in-app UI strings ("Letterboxd Sync" → "Jellyscribe" where the
  copy refers to the whole product, not the Letterboxd-specific feature),
  favicon, and OG/social image all updated to match.
- **The plugin repository URL end users already have configured
  (`https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json`) does
  NOT change.** This is the one continuity guarantee that actually matters:
  changing it would silently stop every existing install from auto-updating,
  which a cosmetic rename must never do. A prettier manifest URL on the new
  domain, if wanted, is an *additional* source for new installs, never a
  replacement.

## Non-goals

- No C# namespace rename (`LetterboxdSync` → `Jellyscribe` in code) this
  release; see above.
- No change to plugin GUID, no data migration, no change to sync behavior,
  history format, or any stored configuration shape.
- No third service (Backloggd/games) added. The name is chosen to leave room
  for one, not to imply one is coming in this change.
- Does not touch `feat/serializd-tv-scrobble`'s in-flight Phase 1 work. Per
  `add-serializd-tv-sync`'s own sequencing ("Phase 2 rebrand ships as one
  deliberate release once Phase 1 is stable"), this change does not start
  execution until that branch has shipped and settled on `main`.

## Sequencing

Blocked on `add-serializd-tv-sync` merging first. This change is planning only
until then: name and visual identity are decided and recorded, tasks are
sequenced, nothing here executes against the live repo, domain, or worker
config yet.
