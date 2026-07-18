## Why

When a Letterboxd or Serializd watchlist sync auto-requests a missing film or
show via Seerr, the only record of that outcome is a server log line
(`LetterboxdSync.WatchlistSyncRunner` / `LetterboxdSync.Serializd.SerializdWatchlistSyncRunner`:
"Seerr auto-request for {Username}: {Requested} new, ..."). Nothing about it
reaches the dashboard's sync history, the one place users actually look. A
user watching their own watchlist sync run (2026-07-16 session) had to have
an agent tail raw Docker logs to confirm 3 Seerr requests had gone out
successfully; there was no way to see that from the plugin's own UI. This
erodes the "it just works" trust the sync pipeline otherwise earns, and turns
every "did my watchlist actually request anything?" question into a
log-diving exercise.

## What Changes

- Every successful (and failed) Seerr auto-request triggered by a watchlist
  sync, film via `WatchlistSyncRunner` or TV via `SerializdWatchlistSyncRunner`,
  is recorded as a persisted, per-title event instead of only an aggregate
  log line.
- A new `Requested` outcome is added alongside the existing `Success` /
  `Skipped` / `Failed` / `Rewatch` sync statuses, and rendered as its own
  badge in the dashboard's sync history table (`userPage.html`,
  `statsPage.html`) and counted in `/Letterboxd/Stats`.
- Since watchlist entries only carry a TMDb id (no title) for items not yet
  in the Jellyfin library, a title-resolution step is added so a "Requested"
  entry reads as a title, not a bare TMDb id. Design.md covers where that
  lookup comes from.
- `/Letterboxd/History` (and the equivalent Serializd read path) surfaces
  these new events alongside existing diary-sync events, newest first, same
  as today.

## Capabilities

### New Capabilities
- `watchlist-request-activity`: recording and surfacing the outcome of
  Seerr auto-requests originating from a Letterboxd or Serializd watchlist
  sync, so a successful (or failed) request is visible in the dashboard
  without reading server logs.

### Modified Capabilities
(none, no existing `openspec/specs/` capability currently covers watchlist
sync or Seerr integration; `serializd-sync` is scoped to account linking,
real-time scrobble, and scheduled catch-up only.)

## Impact

- `LetterboxdSync/WatchlistSyncRunner.cs`: per-title event recording inside
  the Seerr auto-request loop (`SyncOneUserAsync`), instead of only the
  aggregate `_logger.LogInformation` summary.
- `LetterboxdSync/Serializd/SerializdWatchlistSyncRunner.cs`: same, inside
  `SeerrIntegrationAsync`.
- `LetterboxdSync/SyncHistory.cs`: new `SyncStatus.Requested` value (or
  equivalent), `SyncEventSources` constant(s) for the two origins.
- `LetterboxdSync/Api/LetterboxdController.cs`: `GetStats`/`GetHistory`
  response shapes gain the new count/status.
- `LetterboxdSync/Web/userPage.html`, `LetterboxdSync/Web/statsPage.html`:
  new badge/pill styling and filter chip for `Requested`.
- Likely a new small TMDb-title-lookup helper (exact shape is a design.md
  decision) since neither `WatchlistSyncRunner` nor
  `SerializdWatchlistSyncRunner` currently have title data for items not
  already in the Jellyfin library.
- `LetterboxdSync.Tests`: new coverage for the recording path in both
  runners and the dashboard-facing stats/history shape.

## Non-goals

- Not integrating with Jellyfin's native Activity Log (`IActivityManager`).
  The plugin doesn't use it anywhere today, and it would be a second,
  parallel activity surface alongside the dashboard's existing sync-history
  table that users already check (see the 2026-07-16 session this change
  originated from). Worth reconsidering later if there's a concrete reason
  to want server-wide/notification-bell visibility, but out of scope here.
- Not backfilling history for Seerr requests made before this change ships;
  those only exist as log lines with no title data to reconstruct from.
- Not changing `AlreadyExists` outcomes (already silent today, correctly,
  since "nothing happened" isn't activity worth surfacing).
