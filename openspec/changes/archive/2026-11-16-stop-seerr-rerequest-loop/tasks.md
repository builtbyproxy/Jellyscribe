# Tasks: stop-seerr-rerequest-loop

## 1. 202 is not a new request

- [x] 1.1 `SeerrClient.RequestSeriesAsync`: HTTP 202 returns `AlreadyExists` before the body check, with no approval call
- [x] 1.2 Test: a 202 with Seerr's message body returns `AlreadyExists`

## 2. Removed seasons

- [x] 2.1 `SeerrClient.GetRemovedSeasonsAsync`: season list plus the seasons deleted in Seerr that a completed, non-4K request from the user names; null when the lookup fails
- [x] 2.2 Tests: removed season found; another user's completed request, an open request, a 4K request and a season that is not deleted are not counted; unreadable answers return null
- [x] 2.3 `SerializdWatchlistSyncRunner`: drop removed seasons after the watched check, behind `RequestWatchedTitles`; narrow whole-show requests; let the request through when the lookup fails
- [x] 2.4 Runner tests for each scenario in `specs/seerr-auto-request/spec.md`

- [x] 2.5 `SeerrClient`: read `/api/v1/tv/{id}` once per show per client and share it between the title, season-list and removed-season lookups; test that one show costs one read
- [x] 2.6 Runner test: a 202 answer records no `SyncEvent` and makes no approval call

## 3. Verification

- [x] 3.1 Full unit suite (2026-11-16: 1506 passed after the review fixes; an earlier run had one unrelated failure, `PostReview_SuccessfulFlow_ReturnsOkAndCallsService`, which passed alone and on the rerun)
- [x] 3.2 After release, on the server where it was seen: the next sync logs the skip and records no `Requested` row for the affected show. Done 2026-11-16 on 2.15.1: a Serializd watchlist sync logged the skip for the affected show ("Seerr already fetched it for this user and it was deleted afterwards"), Seerr created no request, and the count of auto-request activity rows did not change. Closes #196

## 4. Release plumbing

- [x] 4.1 Bump `AssemblyVersion`/`FileVersion` (patch) in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`
- [x] 4.2 PR with a `fix:` title, `## Release notes` paragraph and a `site/src/data/release-notes.ts` entry (2026-11-16: #195)
