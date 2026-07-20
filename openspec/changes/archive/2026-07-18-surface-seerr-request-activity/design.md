## Context

Two watchlist sync runners can auto-request via Seerr today:

- `WatchlistSyncRunner.SyncOneUserAsync` (film, Letterboxd) loops
  `requestIds` (bare `int` TMDb ids, from `GetWatchlistTmdbIdsAsync`, which
  never carries a title) and calls `SeerrClient.RequestMovieAsync` per id.
- `SerializdWatchlistSyncRunner.SeerrIntegrationAsync` (TV, Serializd) loops
  `(Tmdb, Seasons)` pairs (from `SerializdWatchlistEntry`, which also only
  carries `ShowTmdbId`/`SeasonNumbers`, no title) and calls
  `SeerrClient.RequestSeriesAsync` per show.

Both loops today only tally counts (`requested`/`alreadyExists`/`failed`)
and emit one aggregate `_logger.LogInformation` line after the loop. Nothing
per-title is persisted. `SyncHistory` (`LetterboxdSync/SyncHistory.cs`)
already exists as the plugin's one persisted, dashboard-facing activity
stream, JSONL-backed, read via `/Letterboxd/History` and
`/Letterboxd/Stats`, and rendered in `userPage.html`/`statsPage.html` as a
filterable, badged table. `SyncEvent.Source` already exists specifically to
tag *why* an event was created without needing a new stream
(`SyncEventSources.DiaryImport` is the existing precedent: diary-import
completions reuse `SyncStatus.Success`/`Skipped` with a distinguishing
`Source`, rather than a separate table).

Crucially: the plugin never calls the TMDb API directly anywhere. All
existing TMDb ids come from Jellyfin's local library metadata
(`GetProviderId(MetadataProvider.Tmdb)`); there's no TMDb API key config,
no TMDb HTTP client. `SeerrClient.GetMovieInfoAsync` (called before every
`RequestMovieAsync`, for the pending/available pre-check) already hits
Jellyseerr's `GET /api/v1/movie/{tmdbId}`, which proxies full TMDb movie
details, title included, at the JSON root, just not parsed out today
(`SeerrClient.cs:107-154` only reads `mediaInfo.status` and
`mediaInfo.requests[].requestedBy.id`). `RequestSeriesAsync` has no
equivalent pre-check call today, it goes straight to the POST.

## Goals / Non-Goals

**Goals:**
- A successful Seerr auto-request (film or TV, from either watchlist
  runner) becomes a persisted, dashboard-visible entry with a real title,
  not a TMDb id.
- A failed Seerr auto-request becomes visible too, distinguishable from a
  failed diary sync, so a user (or the maintainer, when someone reports
  "my watchlist isn't requesting anything") can tell Seerr rejected/erroed
  on a specific title without reading server logs.
- No new external dependency, no new config surface, no new persisted
  storage format beyond what `SyncHistory` already provides.

**Non-Goals:** (see proposal.md's Non-goals for the full list)
- No Jellyfin native Activity Log (`IActivityManager`) integration.
- No backfill of historical Seerr requests.
- No visibility change for `AlreadyExists` outcomes.

## Decisions

### Reuse `SyncEvent`/`SyncHistory`, don't build a parallel activity stream

Add `SyncStatus.Requested` (new enum member, appended after `Rewatch` so
existing numeric values `0-3` are unchanged, see Risks) and two new
`SyncEventSources` constants (`SeerrAutoRequestFilm`, `SeerrAutoRequestTv`),
then call `SyncHistory.Record(...)` once per title inside each runner's
Seerr loop, success or failure, in place of (well, alongside, the aggregate
line stays for server-log-level debugging) the existing summary log.

Alternative considered: a dedicated "requests" history stream with its own
JSONL file, API endpoint, and dashboard card. Rejected: it's meaningfully
more surface (new file format, new controller action, new UI section) for
what the user actually asked for, which is "show it in the activity [I
already look at]". `Source` already exists precisely to let one stream
carry semantically different events without collapsing their meaning
together in the UI (the dashboard can, and should, render `Requested`
distinctly from `Success` regardless of them sharing a table).

### Title resolution goes through Jellyseerr, not TMDb directly

Extend `SeerrClient.GetMovieInfoAsync`'s existing JSON parse to also read
the root-level `"title"` field (already present in the response body it
already fetches, zero new HTTP calls for the film path). Add a new
`GetSeriesInfoAsync` (`GET /api/v1/tv/{tmdbId}`, parses root-level `"name"`,
TMDb TV objects use `name` not `title`) for the TV path, since
`RequestSeriesAsync` has no pre-check call to piggyback on, one new GET per
watchlisted show being requested. Both methods return the title as best
effort (`null` on any failure, matching `GetMovieInfoAsync`'s existing
graceful-degradation shape); a `null` title records the event with a
`"TMDb {id}"` fallback label rather than blocking the request or dropping
the event.

Alternative considered: call TMDb's API directly. Rejected outright, it
would introduce a TMDb API key config option, a new HTTP client, and a new
external failure mode (TMDb down/rate-limited) purely to fetch data
Jellyseerr already has cached from the same TMDb-backed metadata and that
the plugin already has a trusted, authenticated client for.

### Failure visibility reuses `SyncStatus.Failed`, tagged by `Source`

A failed Seerr request records `Status = SyncStatus.Failed`,
`Source = SeerrAutoRequestFilm`/`SeerrAutoRequestTv`, and `Error` set from
the same message already logged via `_logger.LogWarning`. No new failure
enum value, existing `Failed` styling/filtering in the dashboard applies
unchanged; `Source` is what lets a future reader (or a future UI tweak)
tell a failed Seerr request apart from a failed diary sync without
guessing from the film title alone.

## Risks / Trade-offs

- [Frontend hardcodes `SyncStatus` as an index array in two places:
  `userPage.html:393` (`['Success','Skipped','Failed','Rewatch'][e.Status]`
  fallback) and the inline `statusText` ternaries in `statsPage.html:155`
  and `statsPage.html:219`. A new enum value with no matching frontend
  branch renders as blank/`undefined` for `Requested` rows.] → Update all
  three sites in the same change; add an explicit task for each (tasks.md).
- [One new HTTP round trip per watchlisted TV show being requested, via the
  new `GetSeriesInfoAsync`.] → Proportional, not a new order of magnitude:
  `RequestSeriesAsync` itself is already one round trip per show in the
  same loop, this doubles that specific loop's request count, it does not
  add a loop.
- [A watchlist backfill run (`BackfillAvailableRequests`) against a large
  watchlist could write many `Requested` events in one sync, and grow the
  JSONL history file faster than diary syncs alone.] → `SyncHistory` already
  pages (`GetPage`) and the dashboard already caps recent-events queries;
  no functional risk, just faster growth of an already-unbounded-by-design
  append log (existing behavior, not introduced by this change).
- [Title lookup failure (Jellyseerr slow/down) must not block the request
  itself or silently drop the activity entry.] → Both info-lookup methods
  are best-effort by design (mirrors `GetMovieInfoAsync`'s existing
  catch-and-return-null shape); a failed lookup still produces a
  `Requested`/`Failed` event, just with a `"TMDb {id}"` fallback label
  instead of a real title.

## Migration Plan

Purely additive: new enum member, new `Source` constants, new event writes,
new frontend branches for an existing status-rendering switch. No existing
`SyncEvent` records need migration (JSONL is append-only; old records simply
never had `Status == Requested`, and nothing reads that value as anything
but "unknown" today outside the three sites called out above). Ships in
one release, no flag, no rollout sequencing, ordinary version bump per the
project's release process.

## Open Questions

- Should `AutoRequestWatchlist`'s aggregate log line (`"Seerr auto-request
  for {Username} ({Mode}): ..."`) stay as-is once per-title events exist, or
  get trimmed since the dashboard now covers what it summarized? Leaning
  keep, it's still the fastest signal in a live log tail (this change's own
  origin story), and per-title events don't replace that read path, they
  add a persisted/dashboard one. Not blocking, resolve during `tasks.md`/
  implementation.
