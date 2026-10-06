# rating-streaming Specification

## Purpose
Sends a film's rating to the member's Letterboxd film rating whenever it changes in Jellyfin (the web UI has no rating control, so in practice from clients, the API, or Jellyfin Enhanced's review mirror), not only inside a new diary entry, so a rating given after the watch was logged still reaches Letterboxd. Only real changes are sent, the plugin's own rating writes never echo back, and failures never affect diary sync. Added in 2.7.0 (#135); the website-login path's live check is #140.
## Requirements
### Requirement: Rating changes stream to Letterboxd automatically
When a Jellyfin user's rating on a movie changes through a save with reason UpdateUserData or UpdateUserRating, the plugin SHALL push the mapped half-star value to Letterboxd as the member's film rating for every enabled account of that user whose rating-sync toggle is on, without any user interaction. The film SHALL be resolved by TMDb id; items without one are skipped with a log line.

#### Scenario: Rate after watching in a client app
- **WHEN** a user finishes a film (already synced to the diary) and later rates it 7/10 in a Jellyfin client that saves ratings through the user-data API
- **THEN** the user's Letterboxd film rating becomes 3.5 stars, and a sync-history entry with source "rating" records it

#### Scenario: Rating mirrored from a Jellyfin Enhanced review
- **WHEN** Jellyfin Enhanced's review rating mirror is on and a user posts a 4-star review of a film they already watched
- **THEN** the Jellyfin rating becomes 8/10 and the user's Letterboxd film rating becomes 4 stars

#### Scenario: Rating without a watch
- **WHEN** a user rates a library film they never played
- **THEN** the rating is still pushed as the film rating; no diary entry is created, and Letterboxd marks the film watched

#### Scenario: Toggle off
- **WHEN** an account's rating-sync toggle is off and the user rates a film
- **THEN** nothing is pushed for that account, while other enabled accounts of the same user still receive the rating

### Requirement: The plugin's own rating writes never echo back out
Rating saves originating from the plugin itself SHALL NOT be streamed. All plugin-originated rating writes (diary import and the review-modal writeback) SHALL save with the Import reason, and the handler SHALL stream only UpdateUserRating saves.

#### Scenario: Diary import does not bounce
- **WHEN** diary import writes a Letterboxd rating into Jellyfin
- **THEN** no push back to Letterboxd occurs

#### Scenario: Review-modal writeback does not double-push
- **WHEN** a user posts a review with stars in Jellyscribe (which already carries the rating to Letterboxd and mirrors it into Jellyfin)
- **THEN** the mirror write does not trigger a second Letterboxd push

### Requirement: Saves that leave the rating unchanged push nothing
The plugin SHALL push only when the mapped half-star value differs from the last value it successfully pushed for that user, account, and film. The last pushed value SHALL survive a server restart.

#### Scenario: Rating that existed before the update
- **WHEN** a film was rated 8/10 in Jellyfin before this version, its Letterboxd rating was later changed to 5 stars, and after the update the user favorites the film in Jellyfin
- **THEN** no Letterboxd push occurs and the 5-star rating stays

#### Scenario: Favoriting a rated film
- **WHEN** a user whose film rating was already pushed toggles that film's favorite (a save with reason UpdateUserRating that does not change the rating)
- **THEN** no Letterboxd push occurs

#### Scenario: Restart then unrelated save
- **WHEN** the server restarts and a user then edits playstate on a film whose unchanged rating was pushed before the restart
- **THEN** no Letterboxd push occurs

#### Scenario: Edit that maps to the same half-star
- **WHEN** a rating already pushed as 3.5 stars changes from 7/10 to 7.2/10
- **THEN** no Letterboxd push occurs

### Requirement: A failed push is retried a bounded number of times
A push that fails for a reason other than authentication SHALL be retried with backoff, up to three attempts in total, unless a newer rating for the same film is queued first. An authentication failure SHALL NOT be retried.

#### Scenario: Letterboxd briefly unavailable
- **WHEN** the push of a 7/10 rating fails with a server error and Letterboxd recovers before the retry
- **THEN** the retry pushes 3.5 stars and nothing else is needed from the user

### Requirement: Rapid changes debounce to one push
Successive rating changes for the same (user, item) within the debounce window SHALL result in exactly one push carrying the final value.

#### Scenario: Rating cleared before it syncs
- **WHEN** a user sets a film to 8/10 and clears the rating within the debounce window
- **THEN** nothing is pushed

#### Scenario: Tapping through star values
- **WHEN** a user changes a film's rating three times within a few seconds, ending on 9/10
- **THEN** exactly one Letterboxd push occurs, with 4.5 stars

### Requirement: Streaming respects account health and reports outcomes
Rating pushes SHALL skip breaker-open accounts without a login attempt, SHALL record auth failures through the breaker exactly like other entry points, and SHALL record successes and failures in sync history with source "rating".

#### Scenario: Breaker open
- **WHEN** an account's auth breaker is open and the user rates a film
- **THEN** no login attempt is made and the skip is logged

#### Scenario: Auth failure counts toward the breaker
- **WHEN** a rating push fails at authentication for the third consecutive time across entry points
- **THEN** the breaker opens and the admin is notified once, per the auth-circuit-breaker capability

### Requirement: Rating events are diagnosable from logs
Every movie rating change observed by the plugin SHALL be logged with item, user, and value, so support can distinguish "the client never wrote the rating to Jellyfin" from "the push to Letterboxd failed" using the existing log viewer.

#### Scenario: Client that does not persist ratings
- **WHEN** a user rates films in a client that never writes ratings to the Jellyfin server
- **THEN** the plugin logs contain no rating-change events for those actions, proving the gap is client-side

