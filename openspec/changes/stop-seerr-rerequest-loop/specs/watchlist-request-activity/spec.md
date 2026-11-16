## MODIFIED Requirements

### Requirement: Already-existing requests stay silent
The plugin SHALL NOT record a `SyncEvent` when a watchlist sync's Seerr
auto-request step finds the title already requested/available
(`SeerrClient.RequestResult.AlreadyExists`). A TV request Seerr answers with
HTTP 202 (every requested season already has an open request, nothing
created) SHALL be reported as `AlreadyExists`.

#### Scenario: Already-exists outcome produces no activity entry
- **WHEN** `RequestMovieAsync` or `RequestSeriesAsync` returns
  `AlreadyExists`
- **THEN** no `SyncEvent` is recorded for that title

#### Scenario: Seerr answers a TV request with 202
- **WHEN** Seerr answers `RequestSeriesAsync`'s POST with HTTP 202 and a
  body carrying only a message
- **THEN** `RequestSeriesAsync` returns `AlreadyExists`, no approval call is
  made, and no `SyncEvent` is recorded
