# Design: stop-seerr-rerequest-loop

## Context

`RequestSeriesAsync` has no status pre-check; it posts and classifies the answer. Seerr (server/routes/request.ts) maps `NoSeasonsAvailableError` to HTTP 202 with only a `message`, so the body check that tells a real request from a no-op finds no `media` and reports `Requested`.

`GET /api/v1/tv/{id}` returns `mediaInfo.seasons[].status` (media status, 7 = deleted) and `mediaInfo.requests[]` with `status` (request status, 5 = completed), `is4k`, `requestedBy.id` and `seasons[].seasonNumber`. Shape checked against a live Seerr on 2026-11-16.

## Decisions

- **202 is always "already exists" for TV.** The status code alone decides; the message text is not matched, so a reworded message cannot bring the bug back.
- **"Removed after being fetched" needs both signals**: the season is deleted in Seerr, and a completed, non-4K request from the same Seerr user names that season. A deleted season with no completed request from this user is still requested (another user asked for it, or it never came through Seerr), so a first request is never blocked.
- **The filter lives in the runner beside the watched check**, behind the same `RequestWatchedTitles` switch, because both answer the same question: was this taken away on purpose. `SeerrClient` only reports what Seerr says.
- **A failed lookup lets the request through.** Unlike the watched check, there is no local evidence the show was ever fetched, and blocking on a Seerr hiccup would hold back first requests.
- **Whole-show requests are narrowed** to Seerr's regular season list minus the removed seasons, the same way the watched check narrows them. With no season list to narrow against, the show is skipped: removed seasons are known to exist and "all" would request them.

- **One read of `/api/v1/tv/{id}` per show per run.** The title lookup, the watched check's season list and the removed-season lookup all parse the same answer, so the client keeps each successful answer for its lifetime (one run for one account). A failed read is not kept.

## Risks

- A user who puts a show back on the watchlist to get it again after a cleanup is now ignored until they turn on "Re-request watched titles" or request it in Seerr. The skip is logged at Information with that hint. This is the same trade as #165.
- Until Seerr's availability sync notices a deletion, the old request is still open and Seerr answers 202; after it, the season is deleted and the request completed. Both states now end without a request.
