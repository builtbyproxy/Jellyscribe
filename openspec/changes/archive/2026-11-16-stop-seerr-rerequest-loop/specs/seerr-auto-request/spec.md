## ADDED Requirements

### Requirement: Seasons removed after being fetched are not auto-requested again
The Serializd watchlist sync SHALL leave a season out of its Seerr
auto-request when Seerr marks that season deleted and a completed, non-4K
request from the same Seerr user names it, unless the account has
`RequestWatchedTitles` on.

#### Scenario: Watchlisted season was fetched and then deleted
- **WHEN** a watchlisted season is deleted in Seerr and the user has a
  completed request naming it
- **THEN** that season is not requested and no `SyncEvent` is recorded for it

#### Scenario: Whole-show entry with some seasons removed
- **WHEN** a watchlist entry names no seasons and Seerr lists seasons 1 to 3,
  of which season 1 was fetched for the user and then deleted
- **THEN** only seasons 2 and 3 are requested

#### Scenario: Every candidate season was removed
- **WHEN** every season the sync would request was fetched for the user and
  then deleted
- **THEN** no request is sent for the show

#### Scenario: Deleted season nobody completed a request for
- **WHEN** a season is deleted in Seerr and the only completed request
  naming it belongs to another user, or there is none
- **THEN** the season is requested as before

#### Scenario: Seerr's answer cannot be read
- **WHEN** the lookup of the show's state in Seerr fails
- **THEN** the request is sent unchanged

#### Scenario: Account re-requests watched titles
- **WHEN** the account has `RequestWatchedTitles` on
- **THEN** the lookup is skipped and removed seasons are requested as before
