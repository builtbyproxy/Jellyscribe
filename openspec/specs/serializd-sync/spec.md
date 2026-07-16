# serializd-sync Specification

## Purpose
TBD - created by archiving change add-serializd-tv-sync. Update Purpose after archive.
## Requirements
### Requirement: Serializd account linking
Each Jellyfin user SHALL be able to link one or more Serializd accounts, each
authenticated by email + password, stored with secrets encrypted at rest using
the same `SecretProtector` shadow-property pattern as Letterboxd accounts. A
linked account has an independent enabled/disabled flag.

#### Scenario: Link a Serializd account
- **WHEN** a user submits a valid Serializd email and password on the Serializd
  config tab
- **THEN** the plugin calls `POST /login`, stores the returned bearer token and
  the password encrypted, and shows the account as linked and enabled

#### Scenario: Rejected credentials
- **WHEN** the submitted password is wrong
- **THEN** the API returns `401 {"message":"Incorrect password."}` and the plugin
  surfaces a link failure without persisting a token

#### Scenario: Secrets never leak through the config round-trip
- **WHEN** the admin config page performs its get-full-config → mutate →
  put-full-config cycle
- **THEN** the encrypted password and token are `[JsonIgnore]` and are not echoed
  back as ciphertext that could clobber the stored values

### Requirement: Real-time episode scrobble
When a Jellyfin `Episode` is played to completion, the plugin SHALL log that
episode as watched on Serializd for every enabled Serializd account belonging to
the playing user, keyed by the series TMDb id, the Serializd season id, and the
episode number.

#### Scenario: Episode finished
- **WHEN** an `Episode` raises `PlaybackStopped` with `PlayedToCompletion` true
  and its series has a TMDb id
- **THEN** the plugin resolves the season id via `GET /show/{tmdbId}` (matching
  `seasonNumber == ParentIndexNumber`) and calls `POST /episode_log/add` with a
  snake_case body `{episode_numbers, season_id, show_id}`

#### Scenario: Film still goes to Letterboxd
- **WHEN** the completed item is a movie
- **THEN** it follows the existing Letterboxd path and no Serializd call is made

#### Scenario: Multi-episode file
- **WHEN** the episode item spans a range (`IndexNumber`..`IndexNumberEnd`)
- **THEN** every episode number in the range is included in `episode_numbers`

#### Scenario: Missing TMDb id
- **WHEN** the episode's series has no TMDb id
- **THEN** the plugin logs a warning and skips that item without error

### Requirement: Snake_case write payloads
All Serializd write requests SHALL serialise their bodies in snake_case
(`show_id`, `season_ids`, `episode_numbers`), because the API returns HTTP 500 for
camelCase bodies.

#### Scenario: Correct casing on the wire
- **WHEN** any log/unlog request is sent
- **THEN** the JSON body uses snake_case keys and the request succeeds (200)

### Requirement: Scheduled catch-up
A scheduled task SHALL periodically log any recently-played episodes that are not
yet recorded on Serializd, without marking them as rewatches.

#### Scenario: Missed episode caught up
- **WHEN** an episode was played but its real-time log failed or was skipped
- **THEN** the next scheduled run logs it as watched, not as a rewatch

### Requirement: Service failure isolation
A Serializd API failure SHALL NOT prevent Letterboxd syncing, and a Letterboxd
failure SHALL NOT prevent Serializd syncing.

#### Scenario: Serializd down during mixed session
- **WHEN** the Serializd API returns errors while a user also has films to sync
- **THEN** the Letterboxd film sync completes normally and the Serializd failure
  is logged and reported via telemetry only

