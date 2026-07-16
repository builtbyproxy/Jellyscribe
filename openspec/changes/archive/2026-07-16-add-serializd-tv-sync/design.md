## Context

The plugin is film-only (Letterboxd). We are adding TV (Serializd) as a second,
independent sync target inside the same plugin, then rebranding to a
service-neutral identity. All API facts below are from the live spike in
`SPIKE.md`.

## Goals / Non-Goals

**Goals (Phase 1):** real-time + scheduled episode scrobble to Serializd;
multi-user Serializd accounts with encrypted secrets; config parity with the
Letterboxd tab; complete failure isolation between the two services.

**Non-Goals (Phase 1):** ratings, written reviews, backdated diary logs (blocked
on the browser capture), TV watchlist→Jellyseerr, Serializd→Jellyfin import.

## Key decisions

### 1. Extend, don't fork
One plugin, two services. Rationale in `proposal.md`. Consequence: everything
service-specific is namespaced (`Serializd*` types, a `serializd` config
section) and the two clients share no code beyond generic helpers, so a Serializd
API break is a compile-isolated, runtime-isolated blast radius.

### 2. Client: plain bearer JSON, no Cloudflare layer
`SerializdApiClient` is a thin `HttpClient` wrapper. Fixed headers (`Origin`,
`Referer`, `X-Requested-With: serializd_vercel`), `Authorization: Bearer` after
login. **Do not** copy `LetterboxdHttpClient`'s cookie jar / 403-backoff /
re-auth loop, the API host (`serializd.onrender.com`) is not behind Cloudflare.
Token is cached per account and revalidated via `/validateauthtoken`; on 401,
re-login once with stored email+password and retry (much simpler than the
Letterboxd re-auth).

**Payload casing:** request bodies are **snake_case** (`show_id`, `season_ids`,
`episode_numbers`). This is load-bearing, camelCase returns 500. Encode with an
explicit snake_case JSON policy on the Serializd DTOs (do not reuse the default
Letterboxd serializer options).

### 3. Episode identity mapping (the one real subtlety)
Serializd logs by **TMDb show id + Serializd internal `seasonId` + episode
numbers**, not by episode TMDb id. Jellyfin gives us, on an `Episode` item:
- series TMDb id: `episode.Series?.GetProviderId(MetadataProvider.Tmdb)` (fall
  back to walking to the parent `Series`),
- season number: `episode.ParentIndexNumber`,
- episode number: `episode.IndexNumber`.

Resolution path: `GetShow(tmdbId)` → find the season whose `seasonNumber ==
ParentIndexNumber` → use its `id` as `season_id` → `LogEpisodes(show_id=tmdbId,
season_id, episode_numbers=[IndexNumber])`. Cache the show→season-id map per
series to avoid a `GetShow` call per episode during a binge (a lightweight
`SerializdSeasonCache`, analogous to `TmdbCache` but Serializd-keyed).

Edge cases to handle explicitly: multi-episode files (`IndexNumber` +
`IndexNumberEnd` → log the range), specials (`ParentIndexNumber == 0`, Serializd
season "Specials" exists but skip unless a season match is found), episodes with
no TMDb id on the series (log-and-skip, same as the film path does today),
absolute-numbered anime (season lookup by number will miss; log-and-skip in v1).

### 4. PlaybackHandler dispatch
Today `HandlePlaybackStoppedAsync` early-returns unless `e.Item.IsMovie()`.
Change to dispatch:
- `IsMovie()` → existing Letterboxd path (untouched),
- `e.Item is Episode` → new `HandleEpisodeAsync` → Serializd path.

Each branch is independently guarded and independently try/caught per account, so
a Serializd exception never touches the Letterboxd fan-out and vice versa. Reuse
the existing `PlayedToCompletion` + `e.Users` gating.

### 5. Config + account model
`SerializdAccount` mirrors `Account`: `UserJellyfinId`, `Email`,
`[XmlIgnore] Password` + `PasswordProtected` shadow property, `[XmlIgnore] Token`
+ `TokenProtected` shadow, `Enabled`. Follow the exact `SecretProtector`
shadow-property pattern documented for `Account` (real prop plaintext in memory +
`[XmlIgnore]`; `*Protected` sibling carries `[XmlElement("...")]` + `[JsonIgnore]`
and does the encrypt/decrypt; legacy plaintext self-upgrades on save).
`PluginConfiguration` gains `SerializdAccounts[]` and
`GetEnabledSerializdAccountsForUser(id)`.

Config page: a **Serializd** tab beside the Letterboxd tab, same inline-`<script>`
constraint (Jellyfin 10.11 SPA doesn't run external plugin scripts). Per-user
page gets a Serializd section with link/enable/"Sync now"
(`POST /Jellyfin.Plugin.LetterboxdSync/Serializd/SyncNow` or a Serializd-scoped
controller). Keep the admin-page button kicking the scheduled task, per existing
pattern.

### 6. Scheduled catch-up + SyncGate
`SerializdSyncTask` scans episodes played since the last run and logs any not yet
on Serializd. It shares the static `SyncGate` only to serialise against itself
and any future Serializd-origin work; it does **not** contend with Letterboxd
(different origin), so consider a separate named gate instance keyed by origin to
avoid false serialisation between the two services. Scheduled path never sets
rewatch (matches the Letterboxd rule); only real-time playback would.

### 7. Rebrand mechanics (Phase 2)
- **GUID unchanged** (`c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35`). Jellyfin keys
  plugins by GUID; the display name and repo can change freely and installs keep
  updating.
- **Assembly/namespace:** renaming `AssemblyName` changes the plugin **directory
  name** Jellyfin expects (it deploys to `<AssemblyName>_<version>/`). Plan a
  clean version bump; the manifest install writes the new dir. Namespace rename is
  a mechanical `LetterboxdSync` → `<New>` sweep, but is large and churny, so it
  MAY be deferred: the internal namespace can stay `LetterboxdSync` while the
  user-facing name changes, avoiding risk. Decide in tasks; default is **keep the
  namespace, rename only the display name + repo + domain** for the first
  rebrand release.
- **Manifest URL:** stays `raw.githubusercontent.com/builtbyproxy/<repo>/main/manifest.json`.
  GitHub 301s renamed repos including raw; **verify with a curl before merge**. If
  raw redirects don't hold, keep the repo name and rename only display+domain.
- **Domain:** new domain, `letterboxdsync.dev` 301 → new. Old domain kept
  registered. `site/` copy + `worker/` telemetry `Origin` allowlist updated.

### 8. Naming
Service-neutral, survives adding a third service later (Backloggd/games is a
plausible future, the maintainer runs Romm). Bias to the diary/log/scrobble
space, not a brand reference. Candidates (decide in tasks, not blocking Phase 1):
"Scrobblarr", "Watchlog", "Bingesync", "Diaries for Jellyfin", "Jellyfin Watch
Diary". Dropping "Letterboxd" from the name also removes current trademark
exposure.

## Risks / tradeoffs

- **Unofficial API churn.** Same risk that killed the predecessor plugin.
  Mitigation: isolation (above) + batch Serializd fixes + optional courtesy email
  to `hello@serializd.com`.
- **snake_case footgun.** A single serializer-options slip silently 500s every
  write. Mitigation: dedicated Serializd DTOs with an explicit snake_case policy +
  a client-level test asserting the on-wire body is snake_case.
- **Season-id resolution cost.** Naive `GetShow` per episode hammers the API
  during a binge. Mitigation: per-series season-id cache.
- **Rebrand link rot.** Mitigation: GUID + manifest URL preserved, old domain
  301'd and kept.

## Migration / rollout

Phase 1 ships as normal feature releases (Serializd inert unless an account is
linked, so zero impact on existing film-only users). Phase 2 rebrand ships as one
deliberate release once Phase 1 is stable. No data migration: Serializd accounts
are additive to `PluginConfiguration`.

## Open questions (blocking the deferred rating/review/backdate change only)

Both from `SPIKE.md`, both closeable in one ~15-min authenticated-Chrome network
capture: (1) the dated-diary/review **create** endpoint (method + path + body),
(2) whether `backdate` is writable there. Not blocking Phase 1.
