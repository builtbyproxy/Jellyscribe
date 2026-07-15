## 0. Close the spike's open questions (only blocks Phase 3, do before speccing ratings)

- [x] 0.1 In the maintainer's real Chrome (logged in, bypasses Cloudflare): open a
      show's `/show/{id}/add-review` page, submit a throwaway review with a rating
      and a **past** date, capture the exact request from the Network tab (method,
      path, JSON body), then delete the review. Record method + path + body +
      whether `backdate` was accepted in `SPIKE.md`. Done 2026-07-10 via the `_app`
      chunk capture; see SPIKE.md's "Phase 3 endpoints — RESOLVED" section
      (`POST /api/show/reviews/add`, `backdate` confirmed writable).

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
- [x] 2.5 Tests: secret round-trips encrypted + `[JsonIgnore]` doesn't echo
      ciphertext through the config-page get→mutate→put cycle. Mirrors the
      Letterboxd `Account` coverage in `SecretProtectorTests.cs` for
      `SerializdAccount.SerializdPasswordProtected`.

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

- [x] 6.1 Decide the name (design.md candidate list) and new domain. Decided:
      **Jellyscribe**, full research/rationale and rejected alternatives in
      `openspec/changes/rebrand-jellyscribe/design.md`. Execution (6.2-6.5,
      superseded by that change's own `tasks.md`) blocked on this change
      shipping and settling on `main` first — do not start until then.

## 7. Phase 3 — dated diary logs + ratings (endpoint recovered; see task 0.1 in SPIKE.md)

Endpoint cracked via the current `_app` chunk + a logged-in capture:
`POST /api/show/reviews/add` (`is_log`, `backdate`, `rating` all writable). Confirmed
reversibly (create → read → delete) at both show and episode level with a past backdate.

- [x] 7.1 **Rating sync**: `SerializdRating.FromJellyfin` (0..10 → 1..10, round/clamp,
      0⇒unrated) + `rating` on each dated log. Episode-level for now; series-level
      rating → show rating is 7.5 below.
- [x] 7.2 Written reviews from the dashboard (compose UI + `review_text`). Shipped:
      `configPage.html`/`userPage.html`'s review modal is source-aware and posts to
      `Serializd/Review`; `SerializdController.PostReview` fans out to enabled
      accounts, show- or episode-level (`SeasonNumber`/`EpisodeNumber`), and records
      a `Source="review"` activity row. `SerializdControllerTests` covers validation,
      show/episode routing, activity recording, and per-account failure isolation.
- [x] 7.3 **Backdated diary logs**: `ISerializdService.CreateEpisodeLogAsync` →
      `/show/reviews/add` with `is_log:true` + `backdate`. Real-time stamps *now*;
      the catch-up backdates each episode to its Jellyfin `LastPlayedDate`. Separate
      `SerializdSyncHistory` kind (`log` vs `watched`) so already-watched episodes
      still get backfilled as dated logs without re-marking watched. Watched-marking
      via `episode_log/add` retained. Tests: client body shape, rating omit/clamp,
      kind namespacing, handler creates a log.
- [x] 7.4 TV watchlist → Jellyseerr (reuse `SeerrClient`, request TV). Shipped:
      `SerializdWatchlistSyncRunner.SeerrIntegrationAsync` auto-requests missing/
      incomplete watchlisted seasons and optionally mirrors the watchlist into the
      Seerr user's own TV watchlist, primary-account-only. Gained a
      `SeerrClientFactoryOverride` test seam (mirroring `WatchlistSyncRunner`'s) and
      an end-to-end `SerializdWatchlistSyncRunnerTests` suite (mock-handler-driven
      auto-request, user-map failure, mirror add/remove, empty-watchlist guard,
      secondary-account isolation), matching the Letterboxd film analogue's coverage.
- [x] 7.5 Series-level rating → Serializd **show** rating + favorite → **like**, merged
      into one show-level entry (`SetShowMetaAsync` → `/show/reviews/add`,
      `is_log:false` confirmed to set a rating/like *without* a Diary row). Catch-up
      syncs rated/favorited series among the watched set, deduped by `showmeta` kind.
      Limitation: create-once (a later rating change won't re-sync until an update path).
- [x] 7.6 Serializd watchlist → Jellyfin **collection + playlist** (parity). A collection
      (BoxSet) of the watchlisted **shows** (browse) PLUS a playlist of the **episodes of the
      specific watchlisted seasons** (play-queue; season-accuracy lives here since playlists
      are episode-level). `GetWatchlistAsync` returns per-show season numbers (resolving
      `seasonIds`→numbers via the show map). Both reconcile add+remove. Verified live: 3 shows
      in the collection; playlist = The Bear S5 (8) + DTF St. Louis S1 (6) + Clarkson's S4 (2)
      = 16 eps, no strays; S5 of Clarkson's correctly absent (not in library yet). ~~(BoxSet, not a playlist: a Series
      added to a Video playlist expands into its episodes). `GetWatchlistShowTmdbIdsAsync`
      paginates `/user/{username}/watchlistpage_v2` (item `showId` = TMDb; username resolved
      via `/validateauthtoken`); `SerializdWatchlistSyncRunner` matches shows to library
      Series by TMDb and mirrors them into a "Serializd Watchlist" collection via
      `ICollectionManager`. Opt-in per account (`SyncWatchlist` + config checkbox); daily
      `SerializdWatchlistSyncTask` + runs on "Sync TV Now". Verified live: 3/3 shows →
      collection with 3 members. Limitations: add-only (dropped shows not removed yet);
      single collection name (multi-account-per-user would collide).
      **Bug fixed en route:** `/show/reviews/add` 500s if `rating` is omitted, so unrated
      logs/show-meta now always send `rating:0`.
