## Context

`PlaybackHandler.HandleEpisodeAsync` logs a finished episode to every enabled Serializd account of the user: `LogEpisodesAsync`, then one dated diary log per episode number. `isRewatch` is true when `SerializdSyncHistory` already holds a `KindLog` row for that episode. Jellyfin 12.4 raised `PlaybackStopped` twice for one finish in #200 (log bundle LBX-NJBAN1), so the second event saw the first event's row. `SerializdSyncHistory` stores no timestamps, so it cannot tell "just now" from "last month". The film path is protected by `FilmSyncLock` plus same-day diary and history checks.

## Goals / Non-Goals

**Goals:** one finish produces one Serializd diary entry, without a rewatch flag; real rewatches still count.

**Non-Goals:** changing the history file format, the catch-up runner or the film path; cleaning up existing duplicates.

## Decisions

- **In-memory recent-finish map, 5 minute window.** A new `EpisodeFinishGuard` keeps the UTC time of the last logged finish per (user, account, show TMDb id, season, episode range). A completed event inside the window is skipped. Alternatives: adding timestamps to `SerializdSyncHistory` (a data migration for a transient problem), or a same-day rule like films (would swallow a deliberate same-day rewatch of a short episode). Nobody finishes the same episode twice inside 5 minutes; the map only has to survive the second or so between the duplicate events, so losing it on restart costs nothing. Entries older than the window are pruned on each call.
- **Serialise with the existing lock machinery.** `FilmSyncLock` gains a key-based `AcquireAsync(string key)` used by both the film overload and the new guard, so the two events cannot race. The episode key is prefixed so it can never collide with a film key.
- **Record the finish only after Serializd accepted it.** If the first attempt throws, the repeat is allowed to retry, matching today's behaviour; it is not a rewatch because `KindLog` is recorded only after the diary log succeeds.
- **Key on the whole episode range** (first and last episode number) so a multi-episode file's two events match each other.
- **Clock is `internal static Func<DateTime> UtcNow`**, the convention in `AuthBreaker` and `LetterboxdServiceFactory`, so tests can step past the window.

## Risks / Trade-offs

- [A user skips to the end of an episode twice within 5 minutes] → the second is ignored; acceptable, and it was never a real rewatch.
- [Jellyfin sends the repeat more than 5 minutes later] → still logged as a rewatch. Nothing suggests such delays; the report shows about one second.
- [Plugin restart between the two events] → map lost and the old behaviour returns for that one finish; vanishingly unlikely within a second.
