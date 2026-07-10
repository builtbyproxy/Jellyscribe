## 0. Close the spike's open questions (only blocks Phase 3, do before speccing ratings)

- [ ] 0.1 In the maintainer's real Chrome (logged in, bypasses Cloudflare): open a
      show's `/show/{id}/add-review` page, submit a throwaway review with a rating
      and a **past** date, capture the exact request from the Network tab (method,
      path, JSON body), then delete the review. Record method + path + body +
      whether `backdate` was accepted in `SPIKE.md`.

## 1. Serializd API client (Phase 1)

- [x] 1.1 `SerializdApiConstants`: base `https://serializd.onrender.com/api`,
      fixed headers (`Origin`, `Referer`, `X-Requested-With: serializd_vercel`).
- [x] 1.2 `SerializdApiClient.AuthenticateAsync(email, password)` →
      `{username, token}`; bearer-header injection; single re-login on 401.
      (Standalone `ValidateTokenAsync` deferred, not needed: we trust the cached
      token and re-login on 401.)
- [x] 1.3 `ResolveSeasonIdAsync(tmdbId, seasonNumber)` via `GET /show/{id}`
      (seasons with `id`+`seasonNumber`), with a forced refetch on a season miss.
      (Standalone `GetSeasonAsync` not needed for the scrobble path.)
- [x] 1.4 `LogEpisodesAsync` / `UnlogEpisodesAsync` (`/episode_log/add|remove`),
      **snake_case bodies**. (`LogSeasonsAsync`/`watched_v2` deferred, the episode
      path covers real-time scrobbling; whole-season logging is a later nicety.)
- [x] 1.5 `ISerializdService` + `SerializdServiceFactory.CreateAuthenticatedAsync`
      (static token cache), mirroring `LetterboxdServiceFactory`.
- [x] 1.6 Season cache: static per-show `seasonNumber→seasonId` map inside the
      client (avoids a `GetShow` per episode during a binge). Kept in-client rather
      than a separate `SerializdSeasonCache` class; promote later if reused.
- [x] 1.7 Client tests: login-once+cache reuse, bad-credentials throws,
      snake_case-on-wire assertion (+ camelCase-absent guard), season resolve
      known/specials/unknown, log→add, unlog→remove, 401→re-login+retry.

## 2. Account model + config (Phase 1)

- [x] 2.1 `SerializdAccount` (`UserJellyfinId`, `Email`, `[XmlIgnore] Password` +
      `SerializdPasswordProtected`, `Enabled`) using the `SecretProtector`
      shadow-property pattern; legacy plaintext self-upgrades on save.
      (Persisted encrypted `Token` deferred: the static in-process cache covers
      reuse; a re-login after restart is one cheap call.)
- [x] 2.2 `PluginConfiguration.SerializdAccounts[]` +
      `GetEnabledSerializdAccountsForUser(id)`.
- [x] 2.3 Config-page **Serializd tab** (inline `<script>`, generic tab switch,
      zero change to the Letterboxd UI): add/remove account (Jellyfin user + email +
      password + enabled), **Verify login** button → `POST /Serializd/Verify`
      (`SerializdController`), Save persists via the standard plugin config PUT.
      ("Sync now" deferred with the scheduled task, task 3.3.)
- [x] 2.4 `POST …/Serializd/SyncNow` (fires the catch-up for the calling user) +
      **Sync TV Now** button on the TV tab. (Per-user *page* section deferred; the
      dashboard tab covers linking + manual sync.)
- [ ] 2.5 Tests: secret round-trips encrypted + `[JsonIgnore]` doesn't echo
      ciphertext through the config-page get→mutate→put cycle.

## 3. Real-time + scheduled scrobble (Phase 1)

- [x] 3.1 `PlaybackHandler`: dispatch `IsMovie()`→Letterboxd (unchanged),
      `Episode`→new `HandleEpisodeAsync`. Independent per-branch try/catch.
- [x] 3.2 `HandleEpisodeAsync`: read series TMDb id (`SeriesTmdbIdReader` seam),
      `ParentIndexNumber` + `IndexNumber` via `SerializdEpisodeMapper`, resolve
      season→seasonId (cache), fan out across enabled Serializd accounts,
      `LogEpisodes`. Handles multi-ep files (`IndexNumberEnd`), specials (season 0),
      missing-TMDb (log-and-skip).
- [x] 3.3 `SerializdSyncTask` (auto-discovered `IScheduledTask`, daily) →
      `SerializdSyncRunner`; own `SerializdSyncGate` so it never false-serialises
      against Letterboxd; never marks rewatch. Dedups via `SerializdSyncHistory`
      (append-only JSONL), which the real-time path also writes to. Pure
      `GroupNewEpisodes` for the queue logic.
- [x] 3.4 `ServiceRegistrator` registers `SerializdSyncRunner` (needed by both the
      scheduled task and the controller's Sync-Now). Handler branch needs no new
      registration; `PlaybackHandler` is already hosted.
- [x] 3.5 Tests: episode→ref mapping incl. specials + multi-ep + missing-id
      (`SerializdEpisodeMapperTests`); handler routing, season-missing skip,
      no-account no-op, and Serializd-failure-doesn't-bubble
      (`SerializdPlaybackTests`); scheduled-queue grouping/dedup
      (`SerializdSyncRunnerTests`); history persistence (`SerializdSyncHistoryTests`);
      controller verify + sync-now (`SerializdControllerTests`).

## 4. Telemetry + docs (Phase 1)

- [~] 4.1 **DEFERRED** to a follow-up change. The telemetry pipeline is fleet-wide
      and intricate (window counters, error-transition detection, the Cloudflare
      Worker schema); wiring a beta feature in risks the ~190-server telemetry for
      near-zero value while Serializd has one user. Revisit once adoption warrants it.
- [x] 4.2 README + `site/` features copy: TV/Serializd section added.
- [x] 4.3 Version bump (1.20.0.0 in `Directory.Build.props` + csproj) +
      `release-notes.ts` entry. `## Release notes` PR section pending the eventual PR.

## 5. Ship Phase 1

- [~] 5.1 Sideloaded to the maintainer's Jellyfin (checksum-verified, clean load,
      scheduled task auto-discovered). Real-time watch-an-episode confirmation is the
      maintainer's manual step. Backfill of "A Discovery of Witches" S1 done via the
      API directly (all 8 episodes + season marked watched).
- [ ] 5.2 Open PR (blocked: maintainer said do NOT push; commits are local only).

## 6. Rebrand + domain (Phase 2 — after Phase 1 is stable)

- [ ] 6.1 Decide the name (design.md candidate list) and new domain.
- [ ] 6.2 curl-verify `raw.githubusercontent.com` manifest URL survives a repo
      rename **before** renaming; if not, keep repo name, rename display+domain
      only.
- [ ] 6.3 Rename repo + plugin display name. **GUID unchanged.** Default: keep the
      `LetterboxdSync` namespace (rename display only) unless 6.2 forces more.
- [ ] 6.4 New domain live; `letterboxdsync.dev` 301 → new; keep old domain
      registered; update `worker/` Origin allowlist + `site/` copy.
- [ ] 6.5 One deliberate rebrand release; confirm the ~190-server fleet still
      auto-updates (download count ticks) post-rename.

## 7. Phase 3 — dated diary logs + ratings (endpoint recovered; see task 0.1 in SPIKE.md)

Endpoint cracked via the current `_app` chunk + a logged-in capture:
`POST /api/show/reviews/add` (`is_log`, `backdate`, `rating` all writable). Confirmed
reversibly (create → read → delete) at both show and episode level with a past backdate.

- [x] 7.1 **Rating sync**: `SerializdRating.FromJellyfin` (0..10 → 1..10, round/clamp,
      0⇒unrated) + `rating` on each dated log. Episode-level for now; series-level
      rating → show rating is 7.5 below.
- [ ] 7.2 Written reviews from the dashboard (compose UI + `review_text`). Endpoint
      ready (`/show/reviews/add|update|delete`); UI not built yet.
- [x] 7.3 **Backdated diary logs**: `ISerializdService.CreateEpisodeLogAsync` →
      `/show/reviews/add` with `is_log:true` + `backdate`. Real-time stamps *now*;
      the catch-up backdates each episode to its Jellyfin `LastPlayedDate`. Separate
      `SerializdSyncHistory` kind (`log` vs `watched`) so already-watched episodes
      still get backfilled as dated logs without re-marking watched. Watched-marking
      via `episode_log/add` retained. Tests: client body shape, rating omit/clamp,
      kind namespacing, handler creates a log.
- [ ] 7.4 TV watchlist → Jellyseerr (reuse `SeerrClient`, request TV).
- [x] 7.5 Series-level rating → Serializd **show** rating + favorite → **like**, merged
      into one show-level entry (`SetShowMetaAsync` → `/show/reviews/add`,
      `is_log:false` confirmed to set a rating/like *without* a Diary row). Catch-up
      syncs rated/favorited series among the watched set, deduped by `showmeta` kind.
      Limitation: create-once (a later rating change won't re-sync until an update path).
- [ ] 7.6 Serializd watchlist → Jellyfin playlist. **Blocked**: the maintainer's
      Serializd watchlist is empty and the item JSON shape isn't in the `_app` chunk,
      so the item→TMDb mapping can't be built/verified yet. Needs one show added to the
      Serializd watchlist to capture the shape (or a decision to build the push
      direction, Jellyfin/Jellyseerr → Serializd watchlist, instead).
