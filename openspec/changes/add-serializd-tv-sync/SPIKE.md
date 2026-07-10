# Serializd API spike (2026-07-10)

Goal: confirm whether the Letterboxd Sync architecture can be extended to also
scrobble TV to [Serializd](https://www.serializd.com). All findings below were
verified live against the real API with the maintainer's account (`8bitproxy`),
using reversible test writes that were undone immediately.

## Verdict: feasible, and the write path is simpler than Letterboxd's

Serializd has **no official/public API**, but the private API is stable enough
that two community libraries already wrap it:
[serializd-py](https://github.com/Velocidensity/serializd-py) (writes) and
[unserializd](https://github.com/Skyth3r/unserializd) (reads). We read both and
then verified the endpoints ourselves.

## Confirmed facts

**Base URL / transport**
- API base: `https://serializd.onrender.com/api` (Render-hosted; **not** behind
  Cloudflare, unlike `www.serializd.com` which challenges every request).
- Required headers on every call: `Origin: https://www.serializd.com`,
  `Referer: https://www.serializd.com`, `X-Requested-With: serializd_vercel`.
  Set a real `User-Agent` (default urllib UA is fine here, but match our other
  clients).
- **No Cloudflare on the API host** means none of the Letterboxd client's
  Cloudflare machinery is needed: no cookie jar, no 403+backoff retry loop, no
  `ForceReauthenticateAsync`. This deletes the single most fragile part of the
  Letterboxd client.

**Auth (confirmed working)**
- `POST /login` with `{"email","password"}` → `{"username","token"}`. The token
  is a bearer token: send `Authorization: Bearer <token>` thereafter.
- `POST /validateauthtoken` with `{"token"}` → `{"isValid": true, "username": ...}`.
- Login is by **email + password**, not username. A wrong password returns a
  real `401 {"message":"Incorrect password."}` from the API (the first password
  we tried was rejected this way; the corrected one succeeded).

**TMDb-native (confirmed)**
- `GET /show/{tmdbShowId}` → show info incl. `seasons[]`, each with an internal
  Serializd `id` (the "seasonId") and a `seasonNumber`.
- `GET /show/{tmdbShowId}/season/{seasonNumber}` → season detail.
- Everything keys off the **TMDb show id** plus season number. Jellyfin episodes
  already carry the parent series' TMDb id + `ParentIndexNumber` (season) +
  `IndexNumber` (episode), so **the whole `TmdbCache` + `LetterboxdScraper`
  matching layer is unnecessary** for Serializd.

**Marking watched (the core scrobble path, confirmed reversibly)**
- Per-episode: `POST /episode_log/add` with
  `{"episode_numbers":[N], "season_id": <serializdSeasonId>, "show_id": <tmdbId>}`
  → `200 {"message":"Successfully added episode","nextEpisode":...,"shouldMarkSeasonAsWatched":false}`.
  The response literally tells us the next episode and whether to promote the
  season to watched. Removal: `POST /episode_log/remove` (same body).
- Per-season: `POST /watched_v2` with `{"season_ids":[...], "show_id": <tmdbId>}`.
  Removal: `POST /watched/remove_v2` (same body).
- **Payload casing gotcha:** these write endpoints require **snake_case**
  (`season_ids`, `show_id`, `episode_numbers`). Sending camelCase returns `500`.
  (Confirmed: camelCase 500, snake_case 200 on both `/watched_v2` and
  `/episode_log/add`.)

**Reads (from unserializd, base `…/api/user/{username}/`)**
- `/diary`, `/watchedpage_v2/{page}?sort_by=`, `/watchlistpage_v2/{page}`,
  `/currently_watching_page/{page}`, `/reviewspage_v3/`, plus paused/dropped/
  tags/lists/following. Diary + review objects carry `rating`, `reviewText`,
  `containsSpoilers`, `isRewatch`, `episodeNumber`, and a **`backdate`** field.

## Open questions (need a browser network capture to close)

Two things could not be pinned down from the read libraries or endpoint probing,
because the relevant route is built dynamically in the minified web bundle and
guessing paths is both unreliable and risks rate-limiting:

1. **The dated-diary/"log-with-review" create endpoint.** Critical finding:
   marking a season/episode *watched* does **not** create a diary entry (we
   logged Breaking Bad S1, diary stayed empty, then unlogged). Serializd models a
   persistent **watched status** separately from a **dated diary log** that
   carries rating + review + `backdate`. The endpoint that creates the dated log
   (what the `/show/{id}/add-review` page submits) was not identifiable by
   probing: `/review/add` exists but is **read-only** (`Allow: GET, HEAD, OPTIONS`),
   and the submit chunk isn't among the shared JS bundles.
2. **Whether `backdate` is writable** on that create endpoint. The field is
   present in every diary/review *response* and there are `backdate_asc/desc`
   sort params, which strongly implies the create path accepts a watch date, but
   this is unconfirmed. (Note: the Trakt→Serializd migration tool reports watch
   dates *cannot* be migrated, hinting backdate may be limited or unsupported on
   writes. Real-time scrobbling is unaffected either way, since it logs "now".)

**How to close them:** one authenticated session in the maintainer's real Chrome
(bypasses Cloudflare): open a show's add-review page, submit a throwaway review
with a rating and a past date, read the exact request from the Network tab
(method + path + JSON body), then delete the review. ~15 minutes. This is the
only remaining blocker for the rating/dated-diary features; the plain
watched-scrobble path (v1 core) is fully confirmed and needs no further capture.

## Implication for the build

- **v1 (fully de-risked):** real-time episode scrobble on `PlaybackStopped` →
  `/episode_log/add`, plus a scheduled catch-up. This is the TV analogue of the
  Letterboxd diary sync and every endpoint it needs is confirmed.
- **v2 (needs the capture above):** ratings, written reviews, and backdated diary
  logs.
- The Jellyseerr integration transfers directly (request TV instead of film).
