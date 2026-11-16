# Proposal: stop-seerr-rerequest-loop

## Why

A Serializd watchlist sync with Seerr auto-request on showed the same show as "Requested" in the activity list on every run, seen on a live server on 2026-11-16. Two separate faults were behind it. No GitHub issue exists; the second fault is the TV case issue #165 did not cover.

1. Seerr answers `POST /api/v1/request` with HTTP 202 and `{"message":"No seasons available to request"}` when every requested season already has an open request. Nothing is created, but the TV path treats any 2xx as a new request and records a `Requested` activity row each run.
2. A cleanup tool deleted the show under disk pressure. Seerr marked the season deleted and closed the request as completed. The show was still on the watchlist, so the next sync requested it again and it was downloaded again. The "already watched" check from #165 did not stop it because no episode of the show had been logged to Serializd by this plugin.

## What Changes

- A 202 answer to a TV request counts as "already on Seerr": no activity row, no approval call.
- Before auto-requesting a show, the Serializd watchlist sync asks Seerr which seasons it already fetched for this user and that were deleted afterwards (the season is deleted in Seerr and one of the user's own requests naming it is completed). Those seasons are not requested again. A whole-show request is narrowed to the remaining seasons, and dropped when none remain.
- Accounts with "Re-request watched titles" on keep requesting as before, the same switch that already bypasses the watched check.

## Capabilities

### New Capabilities

- `seerr-auto-request`: which watchlisted TV seasons the Serializd watchlist sync leaves out of its Seerr auto-request because Seerr already fetched them for the user and they were deleted afterwards.

### Modified Capabilities

- `watchlist-request-activity`: "Already-existing requests stay silent" gains the 202 case.

## Impact

- `LetterboxdSync/SeerrClient.cs`: 202 handling in `RequestSeriesAsync`; new `GetRemovedSeasonsAsync`.
- `LetterboxdSync/Serializd/SerializdWatchlistSyncRunner.cs`: new filter after the watched check.
- No extra Seerr traffic: the lookup reuses the `GET /api/v1/tv/{id}` answer the title lookup already reads, now kept once per show per run.
- Tests in `SeerrClientTests` and `SerializdWatchlistSyncRunnerTests`.
- Ships a patch release: version bump, `## Release notes`, `release-notes.ts` entry.

## Non-goals

- The film path. `RequestMovieAsync` deliberately requests a film Seerr marks deleted again, and the #165 watched check already covers films.
- Using Jellyfin play state as "watched". The watched check stays based on what was logged to Serializd.
- Rewriting activity rows already recorded for 202 answers.
- A new setting. The existing "Re-request watched titles" switch is the override.
