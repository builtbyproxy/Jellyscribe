## MODIFIED Requirements

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
