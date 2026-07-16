## 1. Core activity model

- [ ] 1.1 `LetterboxdSync/SyncHistory.cs`: add `Requested` to the
      `SyncStatus` enum, appended after `Rewatch` so existing numeric
      values (`0`-`3`) are unchanged.
- [ ] 1.2 `LetterboxdSync/SyncHistory.cs`: add `SyncEventSources.SeerrAutoRequestFilm`
      and `SyncEventSources.SeerrAutoRequestTv` constants, alongside the
      existing `DiaryImport`.
- [ ] 1.3 Test: a round-trip test (record a `Requested`/`Failed` event with
      the new `Source` values, reload via `SyncHistory.GetPage`/`GetRecent`,
      assert status/source survive JSONL serialization).

## 2. Title resolution via Jellyseerr

- [ ] 2.1 `LetterboxdSync/SeerrClient.cs`: extend `GetMovieInfoAsync`'s
      existing parse of `GET /api/v1/movie/{tmdbId}` to also read the
      root-level `"title"` field; return it alongside `Status`/
      `RequesterUserIds` (no new HTTP call, the response is already
      fetched).
- [ ] 2.2 `LetterboxdSync/SeerrClient.cs`: add `GetSeriesInfoAsync`
      (`GET /api/v1/tv/{tmdbId}`), parsing the root-level `"name"` field.
      Best-effort like `GetMovieInfoAsync`: catches exceptions, returns
      `null` on any failure, never throws.
- [ ] 2.3 Test: `GetMovieInfoAsync` returns the parsed title on a mocked
      success response; `GetSeriesInfoAsync` returns the parsed name on a
      mocked success response; both return `null` (not throw) on a
      non-success/mocked-error response.

## 3. Film watchlist recording (WatchlistSyncRunner)

- [ ] 3.1 In the Seerr auto-request loop (`SyncOneUserAsync`), on
      `RequestResult.Requested`: resolve the title via the extended
      `GetMovieInfoAsync`, falling back to `"TMDb {id}"` when it returns
      `null`, and call `SyncHistory.Record` with `Status = Requested`,
      `Source = SeerrAutoRequestFilm`.
- [ ] 3.2 Same loop, on `RequestResult.Failed` (including the existing
      catch block around `RequestMovieAsync`): record a `SyncEvent` with
      `Status = Failed`, `Source = SeerrAutoRequestFilm`, `Error` set from
      the same message already passed to `_logger.LogWarning`.
- [ ] 3.3 Confirm `RequestResult.AlreadyExists` records nothing (no change
      needed if 3.1/3.2 only branch on `Requested`/`Failed`, but add an
      explicit test per 3.4 so a future edit can't silently start emitting
      noise for it).
- [ ] 3.4 Test: `WatchlistSyncRunner` unit tests covering all three
      outcomes (`Requested` → event recorded with title; `Failed` → event
      recorded with `Error`; `AlreadyExists` → no event recorded), using
      the existing `JellyseerrClientFactoryOverride` mock-injection hook.

## 4. TV watchlist recording (SerializdWatchlistSyncRunner)

- [ ] 4.1 In `SeerrIntegrationAsync`'s auto-request loop, on
      `RequestResult.Requested`: resolve the title via the new
      `GetSeriesInfoAsync`, falling back to `"TMDb {id}"` when it returns
      `null`, and call `SyncHistory.Record` with `Status = Requested`,
      `Source = SeerrAutoRequestTv`.
- [ ] 4.2 Same loop, on `RequestResult.Failed`: record a `SyncEvent` with
      `Status = Failed`, `Source = SeerrAutoRequestTv`, `Error` set from
      the failure.
- [ ] 4.3 Confirm `RequestResult.AlreadyExists` records nothing, same as
      3.3.
- [ ] 4.4 Test: `SerializdWatchlistSyncRunner` unit tests mirroring 3.4's
      three outcomes, using `SeerrClientFactoryOverride`.

## 5. API surface

- [ ] 5.1 `LetterboxdSync/Api/LetterboxdController.cs`: `GetStats` gains a
      `requested` count (and, for symmetry with the existing tuple shape,
      confirm `failed` already includes Seerr-sourced failures, it does,
      `SyncHistory.GetStats` counts by `Status` not `Source`).
- [ ] 5.2 Confirm `GetHistory` needs no shape change (it already returns
      raw `SyncEvent` objects; `Requested` passes through as-is), add a
      test asserting that rather than leaving it assumed.
- [ ] 5.3 Test: `LetterboxdControllerTests` (or equivalent) covering
      `GetStats`'s new `requested` field with a mix of statuses recorded.

## 6. Dashboard rendering

- [ ] 6.1 `LetterboxdSync/Web/userPage.html`: add `Requested` to the
      `['Success','Skipped','Failed','Rewatch']` status-label fallback
      array (line ~393) and a `.ws-pill.Requested` / `.ws-chip.on[data-s="Requested"]`
      style pair alongside the existing four, plus a filter chip button.
- [ ] 6.2 `LetterboxdSync/Web/statsPage.html`: update both inline
      `statusText` ternaries (lines ~155 and ~219) to include `Requested`,
      and add a `statRequested` stat tile alongside `statSuccess`/
      `statRewatches`/`statSkipped`/`statFailed`.
- [ ] 6.3 Test: no automated visual regression harness for `site/` or the
      embedded `Web/` dashboard (matches the precedent in
      `rebrand-jellyscribe`'s tasks.md 2.3/1.3); verify manually via the
      `/browse` skill, trigger a watchlist sync against a test Seerr
      instance, confirm a `Requested` row renders with a distinct badge
      and no blank/`undefined` labels.

## 7. Release

- [ ] 7.1 Version bump (patch, this is additive dashboard visibility, not
      a breaking change) in `Directory.Build.props` and
      `LetterboxdSync/LetterboxdSync.csproj`.
- [ ] 7.2 `## Release notes` PR section + `site/src/data/release-notes.ts`
      entry: user-facing prose explaining that successful (and failed)
      watchlist auto-requests now show up in sync history instead of only
      the server log.
