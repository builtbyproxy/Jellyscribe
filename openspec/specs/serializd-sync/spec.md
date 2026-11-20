# serializd-sync Specification

## Purpose
Linking a Serializd account and logging TV episodes there, in real time on playback and in a scheduled catch-up, with Serializd failures kept away from Letterboxd syncing; shipped in 2.0.0 (#94).
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
the playing user whose excluded libraries do not contain the episode, keyed by the
series TMDb id, the Serializd season id, and the episode number.

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

#### Scenario: Episode in an excluded library
- **WHEN** a completed episode sits in a library the Serializd account excludes
- **THEN** no Serializd request is made for that account and an information line
  is logged naming the episode and account

### Requirement: Snake_case write payloads
All Serializd write requests SHALL serialise their bodies in snake_case
(`show_id`, `season_ids`, `episode_numbers`), because the API returns HTTP 500 for
camelCase bodies.

#### Scenario: Correct casing on the wire
- **WHEN** any log/unlog request is sent
- **THEN** the JSON body uses snake_case keys and the request succeeds (200)

### Requirement: Scheduled catch-up
A scheduled task SHALL periodically log any recently-played episodes that are not
yet recorded on Serializd, without marking them as rewatches, skipping episodes
in libraries the account excludes.

#### Scenario: Missed episode caught up
- **WHEN** an episode was played but its real-time log failed or was skipped
- **THEN** the next scheduled run logs it as watched, not as a rewatch

#### Scenario: Excluded episode not caught up
- **WHEN** a played episode sits in a library the account excludes
- **THEN** the scheduled run does not log it, records no failure, and reports it
  in the run's excluded-library skip count

### Requirement: Service failure isolation
A Serializd API failure SHALL NOT prevent Letterboxd syncing, and a Letterboxd
failure SHALL NOT prevent Serializd syncing.

#### Scenario: Serializd down during mixed session
- **WHEN** the Serializd API returns errors while a user also has films to sync
- **THEN** the Letterboxd film sync completes normally and the Serializd failure
  is logged and reported via telemetry only

### Requirement: One finish logs one episode entry
The real-time episode scrobble SHALL log a single finish of an episode once per
Serializd account, even when Jellyfin raises more than one completed
`PlaybackStopped` event for it. Events for the same episode, Jellyfin user and
Serializd account SHALL be handled one at a time, and a completed event that
arrives within 5 minutes of a finish already logged for that episode, user and
account SHALL be skipped without calling Serializd or recording history.

#### Scenario: Repeated stop event for one finish
- **WHEN** an episode the user has never logged raises two completed
  `PlaybackStopped` events about a second apart
- **THEN** exactly one diary entry is created on Serializd, it is not flagged as a
  rewatch, and the second event is logged as a duplicate and skipped

#### Scenario: Concurrent stop events
- **WHEN** the two completed events for the same episode are handled at the same
  time
- **THEN** the second waits for the first to finish and is then skipped

#### Scenario: Genuine rewatch later
- **WHEN** the user finishes the same episode again more than 5 minutes after
  the logged finish
- **THEN** a new diary entry is created and flagged as a rewatch

#### Scenario: Failed first attempt does not block the repeat
- **WHEN** the first event's Serializd call fails and a second completed event
  for the same finish follows
- **THEN** the second event tries the log again and it is not flagged as a rewatch

#### Scenario: Other accounts and episodes unaffected
- **WHEN** a different episode, a different user, or a different Serializd
  account finishes within the window
- **THEN** it is logged normally

