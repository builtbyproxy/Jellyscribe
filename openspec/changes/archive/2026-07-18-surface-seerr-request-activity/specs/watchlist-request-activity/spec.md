# watchlist-request-activity Specification

## Purpose
Make the outcome of a watchlist-driven Seerr auto-request (film via
Letterboxd, TV via Serializd) visible in the plugin's own sync-history
dashboard, with a real title, instead of only existing as a server log line.

## ADDED Requirements

### Requirement: Successful Seerr auto-request recorded as activity
The plugin SHALL record a `SyncEvent` with `Status = SyncStatus.Requested`
whenever a watchlist sync's Seerr auto-request step successfully creates a
new request (`SeerrClient.RequestResult.Requested`), tagged with a `Source`
identifying which watchlist (film or TV) triggered it.

#### Scenario: Film auto-request succeeds
- **WHEN** `WatchlistSyncRunner`'s Seerr loop calls `RequestMovieAsync` for a
  watchlisted film and it returns `Requested`
- **THEN** a `SyncEvent` is recorded with `Status = Requested`,
  `Source = SyncEventSources.SeerrAutoRequestFilm`, the film's TMDb id, and
  its title

#### Scenario: TV auto-request succeeds
- **WHEN** `SerializdWatchlistSyncRunner`'s Seerr loop calls
  `RequestSeriesAsync` for a watchlisted show and it returns `Requested`
- **THEN** a `SyncEvent` is recorded with `Status = Requested`,
  `Source = SyncEventSources.SeerrAutoRequestTv`, the show's TMDb id, and
  its title

### Requirement: Failed Seerr auto-request recorded as activity
The plugin SHALL record a `SyncEvent` with `Status = SyncStatus.Failed`
whenever a watchlist sync's Seerr auto-request step fails
(`SeerrClient.RequestResult.Failed`, or the call throws), tagged with the
same `Source` as the success case, and an `Error` message.

#### Scenario: Film request fails
- **WHEN** `RequestMovieAsync` returns `Failed` or throws for a watchlisted
  film
- **THEN** a `SyncEvent` is recorded with `Status = Failed`,
  `Source = SyncEventSources.SeerrAutoRequestFilm`, and a non-empty `Error`

#### Scenario: TV request fails
- **WHEN** `RequestSeriesAsync` returns `Failed` or throws for a
  watchlisted show
- **THEN** a `SyncEvent` is recorded with `Status = Failed`,
  `Source = SyncEventSources.SeerrAutoRequestTv`, and a non-empty `Error`

### Requirement: Already-existing requests stay silent
The plugin SHALL NOT record a `SyncEvent` when a watchlist sync's Seerr
auto-request step finds the title already requested/available
(`SeerrClient.RequestResult.AlreadyExists`).

#### Scenario: Already-exists outcome produces no activity entry
- **WHEN** `RequestMovieAsync` or `RequestSeriesAsync` returns
  `AlreadyExists`
- **THEN** no `SyncEvent` is recorded for that title

### Requirement: Title resolution for requested/failed titles
A `SyncEvent` recorded for a Seerr auto-request outcome SHALL include a
human-readable title resolved from Jellyseerr (movie title or show name),
falling back to a TMDb-id label when the lookup fails, without blocking or
failing the request itself.

#### Scenario: Title available from Jellyseerr
- **WHEN** Jellyseerr's movie or TV info lookup succeeds and returns a
  title/name
- **THEN** the recorded `SyncEvent.FilmTitle` is that title/name

#### Scenario: Title lookup fails
- **WHEN** Jellyseerr's movie or TV info lookup fails or errors
- **THEN** the Seerr request attempt still proceeds unaffected, and the
  recorded `SyncEvent.FilmTitle` falls back to `"TMDb {id}"`

### Requirement: Dashboard surfaces requested activity
The plugin's dashboard sync-history table and stats SHALL render
`Requested` as a distinct status from `Success`/`Skipped`/`Failed`/
`Rewatch`, and count it separately in `/Letterboxd/Stats`.

#### Scenario: History table renders a Requested entry
- **WHEN** a user views their sync history and it includes a `SyncEvent`
  with `Status = Requested`
- **THEN** the table renders that row with a status label/badge distinct
  from the four existing statuses, not blank or "undefined"

#### Scenario: Stats endpoint counts Requested entries
- **WHEN** `/Letterboxd/Stats` is called for a user with one or more
  `Requested` events
- **THEN** the response includes a count of those events, separate from
  `success`/`failed`/`skipped`/`rewatches`
