# Design: stream-ratings-to-letterboxd

## Context

Ratings flow into Jellyfin's `UserItemData.Rating` (1-10) from clients, the API, and other plugins. The plugin already reads that field in four places and writes it in two (`DiaryImportTask` with `UserDataSaveReason.Import`, the review-modal writeback with `UserDataSaveReason.UpdateUserRating`). The 10.11 SDK exposes `IUserDataManager.UserDataSaved` (verified present, with `UserDataSaveEventArgs` carrying user, item, save reason, and the saved `UserItemData`, but not the previous value). It is the same eventing pattern `PlaybackHandler` uses for `ISessionManager.PlaybackStopped`. `Helpers.MapRating` maps 1-10 to Letterboxd half-stars. The auth circuit breaker (PR #105) gates all authentication. On Letterboxd, a member's **film rating** is a film-relationship property independent of diary entries; diary entries snapshot a rating at logging time.

Which save reason carries a rating change, verified against the Jellyfin 10.11.0 source:

| Writer | Path | Save reason | Touches `Rating`? |
|---|---|---|---|
| Clients / API setting a numeric rating (the web UI has no rating control; its rating button is the favorite heart) | `POST /UserItems/{id}/UserData` (`ItemsController.UpdateItemUserData`) | `UpdateUserData` | yes |
| Favorite toggle | `UserLibraryController.MarkFavorite` | `UpdateUserRating` | no |
| Like / dislike | `POST`/`DELETE /UserItems/{id}/Rating` | `UpdateUserRating` | no (sets `Likes`) |
| Jellyfin Enhanced review mirror (12.10.0.0+) | in-process `SaveUserData` | `UpdateUserRating` | yes (stars x2, or cleared) |
| Jellyfin Enhanced watchlist add | in-process `SaveUserData` | `UpdateUserRating` | no (sets `Likes`) |
| Jellyscribe diary import | `DiaryImportTask` | `Import` | yes |
| Jellyscribe review-modal writeback | `WriteJellyfinRating` | `UpdateUserRating` (changes to `Import` here) | yes |

So the save reason alone cannot identify a rating change: the most common rating path uses `UpdateUserData` (which also carries playstate edits), and `UpdateUserRating` fires for favorites and likes that leave the rating untouched. An earlier draft of this design streamed only `UpdateUserRating`; it would have missed every client- or API-set rating and re-pushed on every favorite toggle.

## Goals / Non-Goals

**Goals:**
- A rating set anywhere in Jellyfin after (or before, or without) a watch reaches Letterboxd without user interaction. Checkable bound: a rating set at T appears in the dashboard activity feed by T+20s on a warm session (10s debounce plus the push).
- The plugin's own rating writes never echo back out; saves that leave the rating unchanged push nothing; a stars-fiddling user produces one push, not five.
- Failures are visible (sync history + logs) and respect the auth breaker.

**Non-Goals:**
- Diary-entry retro-editing, Serializd, rating removal, favorites (see proposal).

## Decisions

**1. A dedicated `RatingSyncHandler` hosted service, not an extension of `PlaybackHandler`.**
Subscribes to `IUserDataManager.UserDataSaved`. Filters, cheapest first: save reason is `UpdateUserData` or `UpdateUserRating` (everything else, including `Import` and the playback reasons, is dropped with one enum comparison); item is a movie with a TMDb id; the saved rating is non-null and positive; the user has at least one enabled account whose `SyncRatings` toggle is on (via the existing `Config.GetEnabledAccountsForUser`, the enumeration `PlaybackHandler` already uses; the toggle check sits above `ILetterboxdService`, like `LibraryExclusion.IsExcluded`). `PlaybackHandler` stays a playback-event handler; mixing two unrelated event sources in one class buys nothing. Registered in `ServiceRegistrator` beside it.

**2. Change detection: an in-memory baseline of Jellyfin's ratings, then a persisted last-pushed rating per account.**
Because the event carries no previous value and both accepted reasons fire for saves that leave the rating alone, the handler keeps every user's current movie ratings in memory, seeded from the library at startup (never overwriting a value observed meanwhile), and queues only a save whose rating differs from that baseline. Without it, the first unrelated save (a favorite toggle) of a film rated before this version would push the stale Jellyfin rating over a newer one on Letterboxd. Before the baseline finishes loading, an unknown film's rated save counts as a change, a startup window of seconds. Each queued push is then gated per account: the handler compares the mapped half-star value against the last value it successfully pushed for that (Jellyfin user, Letterboxd account, film), and pushes only when they differ. The store is persisted (append-only JSONL beside the existing history files, the `SerializdSyncHistory` pattern) so a restart does not turn the next favorite toggle on every rated film into a push. Comparing mapped half-stars, not raw 1-10 values, also absorbs edits that round to the same Letterboxd rating. A film with no stored value pushes once on its first qualifying save, which is idempotent on Letterboxd's side. Pinned details:
- **Key**: (Jellyfin user id, Letterboxd username, TMDb id). TMDb id is what every caller already resolves films by; the push side looks up slug and film id from it through the existing `LookupFilmByTmdbIdAsync`, so neither implementation's film identifier leaks into the store.
- **Order**: push, then record. A failed push leaves the store untouched, so the next save of the same value retries. A crash between push and record re-pushes the same value later, which is benign.
- **Growth**: one line per successful push (a rare, human-paced event). On load the file is compacted to the latest value per key and rewritten, so it is bounded by the number of rated films per account, never by event volume. Nothing is ever evicted, because eviction would turn the next no-op save on that film into a push.

**3. Push the member film rating, not a diary edit.**
New `ILetterboxdService.SetFilmRatingAsync(filmSlug, filmId, rating)`, where `rating` is the Letterboxd half-star value already derived once above the seam by `Helpers.MapRating` in the handler. Neither implementation sees the raw Jellyfin value, so the two paths cannot disagree about what a 7/10 is; a parity test pins that both send the same value.
- `LetterboxdApiClient`: `PATCH /film/{lid}/me` with `{"rating": <half-stars>}` (verified live, see research.md). A response whose `messages` contains an `Error` (e.g. `InvalidRatingValue`, returned with HTTP 200) is a failed push, not a success.
- `ScrapingLetterboxdService`: form `POST /s/film:{numericId}/rate/` with `rating` = half-stars x 2 as a 0-10 integer and `__csrf` (documented, not yet verified live, see research.md). The x2 conversion lives inside this implementation only, pinned by the parity test, because the seam carries half-stars.
- `FilmResult.FilmId` is the LID on the API path and the numeric id on the scraping path. Each implementation only ever receives ids from its own `LookupFilmByTmdbIdAsync`, because the factory hands one instance per account, so the difference never crosses the seam.
Both implementations must be verified against a real account before the handler lands, because the factory can hand either one to any user. That fallback happens **only at authentication** (`LetterboxdServiceFactory.CreateAuthenticatedAsync` catches API login failure and returns the scraping service); a method that throws later is never retried on the other implementation. So "the API client throws NotSupported and the scraping path carries it" is not available. If the probe shows only one path can set a rating, the factory gains an explicit capability-aware selection resolved at construction (the handler asks for a service that can set film ratings, and the factory returns scraping directly when the API cannot), keeping every fallback decision inside the factory.

**4. Echo prevention: plugin-originated rating writes save with `Import`, and the handler ignores `Import`.**
- `DiaryImportTask` already saves with `Import`.
- The review-modal writeback (`WriteJellyfinRating`) currently saves with `UpdateUserRating`; this change switches it to `Import`, which is semantically accurate (the value originates from the Letterboxd side; the review post already carried it there). One pattern for every current and future plugin-originated rating write; a suppression handshake was considered and rejected as a second mechanism for the same concern.
- Change detection (decision 2) is a second, independent backstop: even if a plugin write slipped through with another reason, the value it writes is the one Letterboxd already holds, so after the first push it compares equal. Regression guard: a test pins the writeback's save reason so a future edit can't silently reopen the loop.

**5. Debounce: trailing-edge, per (user, item), 10 seconds, drained by one sweep loop.** Within a pass, one authenticated service is reused per account, consecutive pushes are spaced (2s), and an account that fails is skipped for the rest of the pass. A failed push is requeued with backoff (2 min x attempt) up to 3 attempts; a newer tap queued meanwhile wins. Auth failures are not retried, since repeated logins are what the breaker exists to stop. Clearing a rating removes any pending push for it.
Clients let users tap through star values; each tap fires a save. The event handler only writes `(rating, lastSeenUtc)` into a `ConcurrentDictionary<(Guid User, Guid Item), PendingRating>`, overwriting any earlier value; it does no I/O. One consumer loop, started in `StartAsync` and driven by a 1s `PeriodicTimer` with the `StopAsync` cancellation token, drains entries whose `lastSeenUtc` is at least 10s old (`TryRemove` only if the entry is still the one it read, so a tap that lands mid-drain survives to the next tick), then runs change detection and the push for each, awaited, with failures recorded. One loop instead of a timer per key: no `async void`, no callback racing a restart, a cancellation token on every await. The dictionary is capped (a named constant, e.g. 1000 keys); past the cap, new keys are logged and dropped, which only a pathological client could reach. A restart mid-window loses at most that window's pending values, the same in-memory trade-off as `SyncGate`, and the next change re-triggers. The 10s window is a named constant, not config.

**6. Gating order: toggle, then breaker, then auth.**
Skip silently when `SyncRatings` is off. Skip with an Information log when the breaker is open (no history spam per rating tap). On auth failure, `AuthBreaker.RecordFailure` + notify exactly as the other entry points do; this becomes one more guarded call site, same pattern. On success, update the last-pushed store and record a `SyncEvent` with source `SyncEventSources.Rating` and a new `SyncStatus.Rated`, titled "<film> · Rated 3.5 stars" (the dashboards render the part after " · " as a subline). Not `Success`: `SyncHistory.GetLastSuccessfulSyncDate`, the diary path's local duplicate backstop, takes the newest `Success` event, and a rating event has no viewing date, so a `Success` rating event would blank that backstop; stats and telemetry would also count ratings as diary syncs. Push failures are logged, not recorded as `Failed` events, because `GetConsecutiveFailureCount` feeds the diary runner's per-film abandon threshold and three failed rating pushes must not stop a film's diary sync. Auth failures still feed the breaker, whose paused badge is the dashboard signal. The handler also applies `LibraryExclusion.IsExcluded`, like every other export path.

**7. Diagnostics distinguish client-side from push-side failures, without logging playstate ticks.**
`UpdateUserData` also carries playstate saves, so logging every accepted save would put a line per progress tick in the log viewer. Instead the handler keeps the last rating it *observed* per (user, item) in memory and logs at Debug only when an accepted save's rating differs from it (item, user, reason, old and new value). A rating cleared to null on a film with a stored last-pushed value is logged the same way, noting that removal is not propagated. Each push logs at Information with the outcome. If a user reports "my client's ratings don't sync" and their logs show no such events while rating in that client, the client isn't writing to Jellyfin (as verified for Infuse on 2026-07-30), and the plugin can say so definitively. The in-dashboard log viewer already surfaces plugin-tagged lines.

## Risks / Trade-offs

- [Clients that never write ratings to Jellyfin, e.g. Infuse] → Out of plugin control; diagnostics (decision 7) make it provable, and the feature still works for clients that write ratings, the API, and Jellyfin Enhanced reviews.
- [Unknown official-API endpoint shape for rating] → Research task ordered first; if the official API can't set ratings, the scraping path becomes primary for this operation (factory consumers won't notice; the interface hides which one ran).
- [Per-tap logins under scraping could look bot-like] → Debounce collapses taps; change detection drops no-op saves; the breaker caps sustained failures; ratings are rare events compared to sync runs.
- [Event volume: UserDataSaved fires for playback progress constantly] → First filter is the save-reason enum comparison. `UpdateUserData` saves from playstate edits pass it but stop at an in-memory observed-rating comparison: no I/O and no log line unless the rating actually changed.
- [A rating marks the film watched on Letterboxd and removes it from the watchlist] → Letterboxd's own rule for any rating, verified live. Rating a film you have not seen is rare; when it happens the next watchlist import also drops the film from the Jellyfin watchlist playlist and the Seerr mirror, consistent with Letterboxd. Stated in the README note.
- [Rating removal is not propagated] → A rating cleared in Jellyfin stays on Letterboxd. Stated in the release notes and README, and logged (decision 7) so support can see it happened.
- [Last-pushed store drifts from Letterboxd, e.g. the user rates directly on Letterboxd later] → The store records what the plugin pushed, not Letterboxd's truth; a later Jellyfin rating that differs from the stored value still pushes. The only miss is setting Jellyfin back to the exact value the plugin last pushed after changing it on Letterboxd, which is acceptable for this change.
- [Multi-account fan-out doubles pushes] → Same fan-out semantics as review posting (every enabled account gets the rating); per-account toggle allows opting a secondary account out.

## Migration Plan

Additive. New `SyncRatings` account field defaults to true (XML deserialization of old configs leaves it true via default initializer; verify with a config round-trip test). The last-pushed store starts empty. Rollback: previous DLL ignores the field and the file.

## Open Questions

- Official API rating endpoint shape. HARD GATE, in order: probe first (task group 1), then the service surface, then the handler. Three outcomes:
  1. Both paths can set a rating: implement both, no factory change.
  2. Only one path can: implement that one, add the capability-aware factory selection from decision 3, and say in the release notes which login the feature needs.
  3. Neither can: the change stops. No service surface is written; the probe evidence is recorded in the change dir and the proposal is parked.

## Alternatives considered

- **Poll-based rating sync inside SyncTask** (plan-review suggestion): scan rated items each scheduled run and push un-pushed ratings. Rejected: latency becomes the sync interval (hours) for a feature whose whole point is "rate on the couch, see it on Letterboxd". It needs the same pushed-rating bookkeeping as decision 2 anyway, so that store now exists either way; revisit polling as a catch-up complement if event delivery proves unreliable.
- **Filter on save reason alone** (the earlier draft of this design): stream only `UpdateUserRating`. Rejected after checking the Jellyfin source: numeric ratings from clients and the API save with `UpdateUserData`, and `UpdateUserRating` fires for favorite and like toggles.
