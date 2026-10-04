# Tasks: stream-ratings-to-letterboxd

## 1. Endpoint research (ordered first; blocks the service surface)

- [x] 1.1 Probe the official API's film-relationship rating update against a test account (shape, auth, rating scale); record findings in the change dir (research.md; `RatingEndpointProbeTests`, 2026-10-04)
- [ ] 1.2 Probe the site's rate action for the scraping path (URL, CSRF, payload, response); record findings. Shape documented in research.md; run `RatingEndpointProbeTests.Scraping_SetFilmRating_LandsOnTheFilmRelationship` signed in with raw cookies (CI hits Cloudflare)
- [ ] 1.3 Decide the outcome (provisional, research.md: viable via the API path; scraping pending 1.2) per design.md Open Questions: both paths work (no factory change), one path works (capability-aware factory selection at construction; never a call-time NotSupported, since the factory's fallback is auth-time only), or neither works (stop: record evidence here, park the change, write no service surface)

## 2. Service surface

- [x] 2.1 `ILetterboxdService.SetFilmRatingAsync(filmSlug, filmId, rating)` taking the already-mapped half-star value; implement per 1.3 (plus the factory selection if 1.3 chose it)
- [x] 2.2 Unit tests with mocked HTTP for both implementations, including a parity test that both send the same Letterboxd value for the same input; integration probe test (Category=Integration) that rates and un-rates a known film

## 3. Rating sync handler

- [x] 3.1 `RatingSyncHandler` (IHostedService): subscribe `IUserDataManager.UserDataSaved`; filter reason in {UpdateUserData, UpdateUserRating} + movie + TMDb id + positive rating; per-account fan-out via `Config.GetEnabledAccountsForUser` honoring `SyncRatings`; map once with `Helpers.MapRating`; Debug log only when the observed rating changes (including a clear), Information per push
- [x] 3.2 Debounce per design decision 5: event writes into a capped `ConcurrentDictionary`, one `PeriodicTimer` (1s) loop with the `StopAsync` token drains entries 10s old, compare-and-remove so a mid-drain tap survives
- [x] 3.2a Persisted last-pushed rating store per (Jellyfin user, Letterboxd account, TMDb id), append-only JSONL beside the existing history files with a `DataPathOverride` test hook, compacted to latest-per-key on load; push only when the mapped half-star differs; push then record, never the reverse
- [x] 3.3 Switch `WriteJellyfinRating`'s save reason from `UpdateUserRating` to `Import` (unifies echo prevention on save reason); add a regression test pinning the writeback's save reason
- [x] 3.4 Breaker integration (one more guarded call site: IsOpen pre-check, RecordFailure/RecordSuccess, one-time notify) + sync-history events with a new `SyncEventSources` rating value
- [x] 3.5 Handler tests: reason filtering (Import and playback reasons ignored, UpdateUserData and UpdateUserRating accepted, including the writeback path), change detection (favorite toggle on a pushed film, same-half-star edit, store reload after restart, store untouched when the push throws), debounce (two taps coalesce to one push with the final value; a tap during drain is not lost; StopAsync cancels the loop), fan-out across two enabled accounts, toggle gating, breaker skip, history record; config round-trip test proving `SyncRatings` defaults true for existing configs

## 4. Config + UI

- [x] 4.1 `Account.SyncRatings` (default true) + toggle in both dashboard account editors ("Sync ratings to Letterboxd")
- [x] 4.2 Controller/account-payload tests for the new field
- [x] 4.3 Confirm the dashboard activity feed renders the new rating source sensibly ("Rated <film> 3.5 stars on Letterboxd")

## 5. Verification + release

- [x] 5.1 Live verification on a throwaway Jellyfin container: set a rating via `POST /UserItems/{id}/UserData` and confirm it lands on Letterboxd; toggle favorite on the same film and confirm no second push; then install Jellyfin Enhanced 12.10.0.0+ with `MirrorReviewRatingsToJellyfin` on, post a review, and confirm the stars land on Letterboxd (2026-10-04, throwaway Jellyfin 10.11.11 + Jellyfin Enhanced 12.10.0.0: review edited to 4.5 stars, mirror wrote 9/10, handler pushed 4.5 stars via the official API 11s later and recorded a Rated event; a favorite toggle on the rated film pushed nothing)
- [x] 5.0 Implementation review gates: security, ai-smells, performance, and domain (shares `ILetterboxdService` with diary sync)
- [x] 5.2 Update CLAUDE.md: sync-entry-points list gains the handler; the "Service abstraction with fallback" section states that fallback is auth-time only (and the capability-aware selection, if 1.3 added it)
- [x] 5.2a README "Ratings, reviews & diary": say ratings set after a watch now reach Letterboxd, and name Jellyfin Enhanced 12.10.0.0+ (review rating mirror setting) as a supported source
- [x] 5.3 Version bump to the next free minor at PR time (2.7.0.0 as of 2026-10-04) in `Directory.Build.props` + `LetterboxdSync/LetterboxdSync.csproj`; `feat:` PR with `## Release notes` and `site/src/data/release-notes.ts` entry referencing the user report and the Jellyfin Enhanced mirror; release notes and README state that clearing a rating in Jellyfin does not remove it on Letterboxd. Write the notes only after 1.3 resolves, so they are not conditional
