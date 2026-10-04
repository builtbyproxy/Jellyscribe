# Research: rating endpoints (tasks 1.1-1.3)

## 1.1 Official API: `PATCH /film/{id}/me` (verified live, 2026-10-04)

Probe: `LetterboxdSync.Tests/Integration/RatingEndpointProbeTests.cs`, run by the Integration workflow against the dedicated test account (run 37182008807). Film: The Godfather, TMDb 238, LID `2aNK`. The probe restores the film's original relationship state and asserts the restore.

The request body is a partial `FilmRelationshipUpdateRequest` (`rating`, `watched`, `inWatchlist`, `liked`); omitted fields are left alone. `id` is the film LID, the same value `LetterboxdApiClient.LookupFilmByTmdbIdAsync` returns as `FilmResult.FilmId`.

| Request | HTTP | `messages` | Relationship after |
|---|---|---|---|
| `{"rating":3.5}` on an unwatched film | 200 | `[]` | rating 3.5, watched true, diary entries 0 -> 0 |
| `{"rating":3.5}` again | 200 | `[]` | unchanged (idempotent) |
| `{"rating":3.3}` | **200** | `[{"type":"Error","code":"InvalidRatingValue",...}]` | rating still 3.5 |
| `{"rating":null}` | 200 | `[]` | rating cleared, watched stays true |
| `{"watched":false}` (no other activity) | 200 | `[]` | watched false |
| `{"inWatchlist":true}` | 200 | `[]` | in watchlist |
| `{"rating":4.0}` on a watchlisted film | 200 | `[]` | rating 4, watched true, **removed from watchlist** |

Conclusions:
- The scale is the half-star scale `Helpers.MapRating` already produces (0.5 to 5.0, step 0.5).
- Setting a rating writes no diary entry, so it cannot collide with diary sync's duplicate checks.
- Business-rule failures come back as HTTP 200 with an `Error` entry in `messages`. Checking the status code alone would report a rejected rating as pushed; the client must treat any `Error` message as a failure.
- A rating marks the film watched and takes it off the watchlist. That is Letterboxd's documented rule (`FilmRelationshipUpdateRequest`: "If set, `watched` is assumed to be `true`", and marking watched removes a watchlisted film). Knock-on effect: Jellyscribe's watchlist import reconciles its playlist (`PlaylistReconciler`) and, when enabled, the Seerr watchlist mirror (`WatchlistSyncRunner`) against the Letterboxd watchlist, so the film also drops out of both on their next run. That is consistent with the film now being rated.

## 1.2 Scraping path: `POST /s/film:{id}/rate/` (documented, NOT verified live)

The Integration workflow cannot reach the scraping path: sign-in returns a Cloudflare 403 without `LETTERBOXD_TEST_RAW_COOKIES` (the existing scraping live test skips for the same reason). The sidebar rating widget is server-rendered only for signed-in members, so its request is not visible on a public film page.

Public sources, which do not fully agree:
- One third-party Jellyfin plugin states it verified both write paths and uses `/s/film:{filmId}/rate/`; an independent project's notes list `/film/{slug}/rate/` instead. The first is the stronger source (it documents the scale difference explicitly and pins it with tests), but the signed-in run must settle the path.
- Form `POST https://letterboxd.com/s/film:{filmId}/rate/` with `rating` and `__csrf`, plus `X-Requested-With: XMLHttpRequest` and a `Referer` of the film page.
- `rating` is a **0-10 integer** (half-stars x 2), and `0` clears the rating. This is a different scale from the official API; sending half-stars here would halve every rating.
- `filmId` is the numeric film id, which is what `LetterboxdScraper.ExtractFilmIdentifiers` returns as `FilmResult.FilmId` (the numeric tail of `data-postered-identifier`'s `uid`).
- The public film page's log form uses the same 0-10 integer field (`<input name="rating" type="range" min="0" max="10" step="1">`).

Still unknown until a signed-in run: the response body on success and failure (site endpoints often answer 200 with a JSON `result: false`), and whether a rate on a watchlisted film also removes it from the watchlist.

## 1.3 Outcome

The official API can set, re-set, and clear a member film rating, with no diary side effects: **the feature is viable**. The scraping endpoint is well documented but unverified. Recommendation:

- Implement the API path now (tasks 2.x for `LetterboxdApiClient`).
- Implement the scraping path from the documented shape, behind the same parity test, and close 1.2 with one signed-in local run (`LETTERBOXD_TEST_RAW_COOKIES` + matching `LETTERBOXD_TEST_USER_AGENT`) that records the response bodies here.
- If that run shows the scraping endpoint does not work, fall back to outcome 2 in design.md (capability-aware selection, API only) rather than shipping an unverified write path.
