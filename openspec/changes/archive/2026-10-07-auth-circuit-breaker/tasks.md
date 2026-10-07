# Tasks: auth-circuit-breaker

## 1. Breaker state store

- [x] 1.1 `LetterboxdSync/AuthBreaker.cs`: static store keyed by (userId, lbUsername) with `IsOpen`, `GetState`, `RecordFailure` (returns true on the closed→open transition at 3), `RecordSuccess`, `Reset`; JSON persistence in the plugin configurations directory with `DataPathOverride` + `ResetForTesting` seams, following `SyncHistory`
- [x] 1.2 Unit tests: threshold transition (2→3 opens exactly once), success reset, reset, persistence round-trip via `DataPathOverride`, per-account isolation

## 2. Entry-point guards

- [x] 2.1 `LetterboxdSyncRunner`: pre-check `IsOpen` per account (skip + Information log + sync-history skip event), `RecordFailure`/`RecordSuccess` around `CreateAuthenticatedAsync`, activity-log entry via injected `IActivityManager` when `RecordFailure` reports the open transition
- [x] 2.2 Same guard in `WatchlistSyncRunner` and `DiaryImportTask`
- [x] 2.3 Same guard in `PlaybackHandler` (Letterboxd path only)
- [x] 2.4 Runner tests: breaker-open account is skipped without the factory being invoked; third failure writes exactly one activity entry; other accounts unaffected

## 3. Reset + surfacing

- [x] 3.1 `LetterboxdController`: `Reset` on `PUT Account` and `PUT Accounts` when credentials are persisted; `GET Accounts` gains `authPaused`/`authPausedSince`
- [x] 3.2 Controller tests: re-save resets an open breaker; accounts payload carries paused fields
- [x] 3.3 Paused badge in `configPage.html` + `userPage.html` account rows
- [ ] 3.4 NOT DONE at archive (2026-10-07): no live run is on record. Manual verification on live Jellyfin: force-open a breaker (bad password), observe skip + activity entry, re-save credentials, observe recovery. Covered in tests by `AuthBreakerRunnerTests`, `AuthBreakerPlaybackTests` and `AuthBreakerControllerTests`; the live check is tracked in #143

## 4. Release plumbing

- [x] 4.1 Bump `AssemblyVersion`/`FileVersion` (minor) in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`
- [x] 4.2 PR with `feat:` title, `## Release notes` paragraph, `site/src/data/release-notes.ts` entry; reference issue #103 (2026-07-31: #105 merged with a `feat:` title, a `## Release notes` section, "Closes #103" and the 2.3.0 `release-notes.ts` entry; shipped as v2.3.0)
