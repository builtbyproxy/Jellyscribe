## Why

This plugin syncs Jellyfin **film** watches to Letterboxd. It does nothing for
**TV**, which is most of what the maintainer (and most Jellyfin users) actually
watch. [Serializd](https://www.serializd.com) is "Letterboxd for TV": same social
tracking model, TMDb-native, and its private API is already reverse-engineered
and stable. A spike (see `SPIKE.md`) confirmed live that the core scrobble path
works and is *simpler* than Letterboxd's, because the Serializd API host is not
behind Cloudflare, so none of the Letterboxd client's cookie/backoff/re-auth
machinery is needed.

TV Time (a large TV-tracker) shuts down 2026-07-15, pushing users toward
Serializd right now. A Jellyfin plugin that already scrobbles TV to Serializd
lands into that demand, and it reuses this plugin's expensive, already-built
assets: the release pipeline, telemetry backend, landing site, multi-user
account model, encrypted-secret storage, config UI, and Jellyseerr integration.

We extend this plugin rather than fork a second one. Users think "scrobble my
Jellyfin watches", not "a Letterboxd plugin and a Serializd plugin" (cf. the
Trakt plugin: one plugin, films + TV). One repo amortizes the pipeline; the two
services are isolated internally (separate clients, per-service enable flags,
independent failure) so a Serializd API change never destabilizes Letterboxd.

Because the product now covers two services, the plugin is **rebranded** to a
service-neutral name at launch, with `letterboxdsync.dev` 301-redirecting to the
new domain. The plugin **GUID is unchanged** so existing installs keep
auto-updating through the rename.

## What Changes

### Phase 1 — Serializd TV scrobble (this change's committed scope)

- New `SerializdApiClient`: `Login` (email/password → bearer token),
  `ValidateToken`, `GetShow(tmdbId)`, `GetSeason(tmdbId, seasonNumber)`,
  `LogEpisodes` / `UnlogEpisodes` (`/episode_log/add|remove`), `LogSeasons` /
  `UnlogSeasons` (`/watched_v2`, `/watched/remove_v2`). Snake_case payloads
  (camelCase 500s). No Cloudflare handling.
- New per-user `SerializdAccount` (email, encrypted password, encrypted token,
  enabled flag), stored via the existing `SecretProtector` shadow-property
  pattern. Multi-account per Jellyfin user, mirroring `Account`.
- `PlaybackHandler` gains a TV branch: on `PlaybackStopped` played-to-completion
  for an `Episode`, resolve the **series** TMDb id + season number + episode
  number, map season number → Serializd `seasonId` via `GetShow`, and
  `LogEpisodes`. Film path unchanged.
- New `SerializdSyncTask` scheduled catch-up: scan recently-played episodes and
  log any missing, mirroring `SyncTask`. Shares a per-origin `SyncGate` so it
  serialises against nothing Letterboxd (different origin) but against itself.
- Config UI: a **Serializd tab** on the dashboard and a Serializd section on the
  per-user page (link account, enable/disable, "Sync now"), mirroring the
  Letterboxd tab. Sidebar injection unchanged.
- Telemetry: add Serializd-scoped event categories (auth_failure, log_success,
  log_failure) reusing the existing anonymous-ping contract. No new backend
  surface.

### Phase 2 — Rebrand + domain (this change, gated on Phase 1 shipping)

- Rename plugin display name and repo to a service-neutral name (candidate list
  in `design.md`). **GUID `c7a3e1b9-…` unchanged.** `AssemblyName`/namespace
  migration handled so existing plugin dirs still load.
- New landing domain; `letterboxdsync.dev` 301 → new domain. Keep the old domain
  registered indefinitely (existing README/Reddit links).
- Manifest URL: keep the current `raw.githubusercontent.com/.../manifest.json`
  working (GitHub redirects renamed repos; verified before relying on it) so the
  ~190-server fleet keeps updating.

### Deferred (follow-up change, blocked on the browser capture in `SPIKE.md`)

- Rating sync, written reviews, and **backdated** diary logs. The dated-diary
  create endpoint and whether `backdate` is writable are the two open questions
  in `SPIKE.md`; capture them before speccing this.

### New Capabilities

- `serializd-sync`: the Serializd account model, API client contract, the
  episode-scrobble real-time + scheduled paths, config UX, and failure isolation
  from the Letterboxd path.

### Modified Capabilities

- `playback-sync` (the real-time handler): now dispatches films to Letterboxd and
  episodes to Serializd, independently, with one service failing never blocking
  the other.

## Impact

- **Plugin**: new `SerializdApiClient`, `SerializdAccount`, `SerializdSyncTask`,
  `ISerializdService` + factory, a TV branch in `PlaybackHandler`, config-page
  Serializd tab + per-user section, telemetry categories. One or more releases.
- **No new backend/Cloudflare surface** in Phase 1 (telemetry reuses the existing
  Worker).
- **Rebrand**: repo rename, domain + 301, README/site copy. GUID + manifest URL
  preserved so the fleet is uninterrupted.
- **Risk**: Serializd's API is unofficial and may change without notice (the same
  risk that killed the plugin this one replaced). Mitigated by service isolation
  and by batching Serializd hotfixes rather than shipping fleet-wide waves per
  fix. A courtesy heads-up email to `hello@serializd.com` may earn advance notice
  of API changes.
- **Non-goals (Phase 1)**: ratings, reviews, backdated logs, watchlist→Jellyseerr
  for TV (deferred), importing an existing Serializd diary into Jellyfin.
