# Proposal: stream-ratings-to-letterboxd

## Why

Users rate films *after* watching them and expect those ratings to reach Letterboxd. Today they never do: the real-time sync fires at playback completion, before the rating exists, and the runner sends a rating only inside a new diary entry. On the next run the film is "already on Letterboxd diary for this date" and is skipped, so a rating added after the credits has no path to Letterboxd. v2.2.0's review-modal pre-fill only helps users who post reviews through Jellyscribe; a user who has to open a UI to rate would rather use Letterboxd directly. This was the explicitly deferred non-goal of the `prefill-review-rating` change.

Two concrete sources now hit this gap:

- **Any client that writes ratings through the user-data API, and the API itself.** (Jellyfin's own web UI has no numeric rating control; its rating button is the favorite heart.) (Infuse, the client in the original report, turned out not to write numeric ratings to Jellyfin at all; verified 2026-07-30. That part of the gap is client-side.)
- **Jellyfin Enhanced reviews.** Jellyfin Enhanced 12.10.0.0 (released 2026-10-03) added an opt-in `MirrorReviewRatingsToJellyfin` setting that writes a review's 1-5 stars into the user's Jellyfin rating (x2), for movies and series. A review is almost always written after watching, so without this change those ratings reach Jellyfin but stop there.

## What Changes

- **Rating change streaming**: the plugin subscribes to Jellyfin's user-data-saved event and, when a user's rating on a movie actually changes (from any client, the API, or another plugin), pushes the mapped half-star rating to Letterboxd as the member's film rating, for every enabled account of that Jellyfin user.
- **New service surface**: `ILetterboxdService` gains a set-film-rating operation, implemented in both the official-API client and the scraping fallback.
- **Echo/loop prevention**: rating writes that the plugin itself performs (diary import, review-modal writeback) save with the Import reason and are never re-streamed; saves that do not change the rating (favorite and like toggles, playstate updates) push nothing; rapid successive changes (a user adjusting stars) are debounced into one push.
- **Breaker + toggle**: streaming respects the account-level auth circuit breaker (issue #103) and gets a per-account "sync ratings to Letterboxd" toggle, default on.
- **Diagnostics**: rating-change events are logged (save reason, item, mapped value) so a "my client's ratings don't sync" report can be answered from logs.

## Capabilities

### New Capabilities

- `rating-streaming`: Detecting Jellyfin rating changes, mapping and pushing them to Letterboxd as film ratings, echo prevention, change detection, debounce, breaker/toggle gating, and diagnostic logging.

### Modified Capabilities

<!-- none: diary sync, diary import, watchlist, review posting keep their requirements; this adds a new independent flow -->

## Impact

- New `RatingSyncHandler` (IHostedService) subscribing to `IUserDataManager.UserDataSaved`.
- `ILetterboxdService`, `LetterboxdApiClient`, `ScrapingLetterboxdService` (+ its composed parts): set-film-rating operation. Endpoint shapes need live verification (research task; same probing approach as the Serializd work).
- A small persisted last-pushed-rating store per (account, film), so a save that leaves the rating unchanged pushes nothing, including after a server restart.
- `Configuration/Account.cs` + both dashboard account editors: `SyncRatings` toggle.
- `DiaryImportTask` already saves with the Import reason (ignored by the handler); the review-modal writeback (`WriteJellyfinRating`) switches from UpdateUserRating to Import.
- README: document the Jellyfin Enhanced mirror (12.10.0.0+) as a supported rating source once this ships.
- Tests: handler unit tests (event filtering, change detection, echo prevention, debounce, breaker), service-layer tests, integration probe.
- Ships a release: version bump, `## Release notes`, `release-notes.ts` entry.

## Non-goals

- Updating the rating stored on an already-posted **diary entry**. Letterboxd's member film rating is what the film page and profile show; retro-editing diary entries needs entry-id bookkeeping we don't have and can create duplicate-entry risk. The film rating satisfies the reported use case.
- TV ratings to Serializd. Jellyfin Enhanced also mirrors series ratings, and `SerializdSyncRunner` sends a show's rating only once per show, so a later change does not reach Serializd either. Same shape of fix on a different service; a follow-up change once this one proves the pattern.
- Streaming likes/favorites changes (favorites already flow with diary sync).
- Rating removal propagation: clearing a rating in Jellyfin (including Jellyfin Enhanced's clear-on-review-delete) does not unrate on Letterboxd in this change (logged, not pushed), to keep the failure surface small; revisit if users ask.
- Making a client write ratings to Jellyfin: if the client never persists the rating server-side, nothing in this plugin can see it. The diagnostics exist to prove which side is failing.
