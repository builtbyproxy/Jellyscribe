## ADDED Requirements

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
