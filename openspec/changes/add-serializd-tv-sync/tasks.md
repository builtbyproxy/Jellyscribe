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
- [ ] 2.3 Config-page **Serializd tab** (inline `<script>`): link account
      (email/password), enable/disable, connection test, "Sync now".
- [ ] 2.4 Per-user page Serializd section + `POST …/Serializd/SyncNow`.
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
- [ ] 3.3 `SerializdSyncTask` scheduled catch-up over recently-played episodes;
      origin-scoped `SyncGate` so it never false-serialises against Letterboxd;
      never marks rewatch.
- [ ] 3.4 `ServiceRegistrator` wiring for the scheduled task (handler branch needs
      no new registration; `PlaybackHandler` is already hosted).
- [x] 3.5 Tests: episode→ref mapping incl. specials + multi-ep + missing-id
      (`SerializdEpisodeMapperTests`); handler routing, season-missing skip,
      no-account no-op, and Serializd-failure-doesn't-bubble
      (`SerializdPlaybackTests`). (Scheduled-path test pending task 3.3.)

## 4. Telemetry + docs (Phase 1)

- [ ] 4.1 Serializd-scoped telemetry categories (auth_failure, log_success,
      log_failure) on the existing anonymous-ping contract; no backend change.
- [ ] 4.2 README + `site/` features copy: add TV/Serializd section.
- [ ] 4.3 Version bump + `release-notes.ts` entry; `## Release notes` PR section.

## 5. Ship Phase 1

- [ ] 5.1 Build + sideload to the maintainer's Jellyfin (`./deploy.sh`), link a
      Serializd account, watch an episode to completion, confirm it appears in the
      Serializd watched list, then verify the scheduled task catches a manually
      un-logged one.
- [ ] 5.2 Open PR (Serializd inert for film-only users, so low-risk release).

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

## 7. Deferred (follow-up change, blocked on task 0.1)

- [ ] 7.1 Spec + build rating sync (Jellyfin rating → Serializd review rating).
- [ ] 7.2 Written reviews from the dashboard.
- [ ] 7.3 Backdated diary logs (only if 0.1 confirms `backdate` is writable).
- [ ] 7.4 TV watchlist → Jellyseerr (reuse `SeerrClient`, request TV).
