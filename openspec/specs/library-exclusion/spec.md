# library-exclusion Specification

## Purpose
Lets each Letterboxd and Serializd account keep chosen Jellyfin libraries (for example an Anime library tracked elsewhere) out of every export path, scheduled and real-time, without affecting diary import, watchlist sync, or Seerr requests. Added in 2.6.0 for issue #124.
## Requirements
### Requirement: Per-account excluded libraries
Each Letterboxd account and each Serializd account SHALL carry a list of excluded
Jellyfin library ids. An empty or absent list SHALL mean no library is excluded,
so configurations saved by earlier versions keep syncing every library.

#### Scenario: Upgrade keeps existing behavior
- **WHEN** a configuration saved by an earlier version, with no excluded library
  list, is loaded
- **THEN** every account's excluded list is empty and every library is synced as
  before

#### Scenario: Accounts are independent
- **WHEN** one Jellyfin user has two Letterboxd accounts and only the first
  excludes the Anime library
- **THEN** a film in the Anime library is synced to the second account and not to
  the first

### Requirement: Library membership rule
An item SHALL be treated as excluded for an account when any Jellyfin library
that contains the item has its id in that account's excluded list. An item that
belongs to no known library SHALL NOT be treated as excluded.

#### Scenario: Item in an excluded library
- **WHEN** a film's only library is in the account's excluded list
- **THEN** the film is excluded for that account

#### Scenario: Item in two libraries, one excluded
- **WHEN** an episode is contained by both a TV library and an excluded Anime
  library
- **THEN** the episode is excluded for that account

#### Scenario: Deleted library id
- **WHEN** the excluded list holds the id of a library that no longer exists
- **THEN** that id matches no item and sync proceeds normally for everything else

### Requirement: Scheduled Letterboxd sync honors exclusions
The scheduled Letterboxd sync SHALL NOT export a film to an account when the film
is excluded for that account, and SHALL log how many films it skipped for that
reason.

#### Scenario: Excluded film skipped on scheduled run
- **WHEN** the scheduled sync runs and a played film sits in a library the
  account excludes
- **THEN** no diary entry is posted for that film on that account, no failure is
  recorded, and the run log reports the film in its excluded-library skip count

### Requirement: Real-time film sync honors exclusions
When a film finishes playing, the plugin SHALL skip each Letterboxd account that
excludes the film's library, before making any request to Letterboxd for that
account.

#### Scenario: Excluded film finished
- **WHEN** a film in an excluded library raises playback-stopped with played to
  completion
- **THEN** no Letterboxd request is made for the excluding account and an
  information line is logged naming the film and account

#### Scenario: Second account still syncs in real time
- **WHEN** the same Jellyfin user has a second Letterboxd account that does not
  exclude that library
- **THEN** the film is synced to the second account as usual

### Requirement: Library listing for the settings pages
The plugin SHALL expose an authenticated endpoint listing the Jellyfin libraries
that can hold films or TV (collection type movies, tvshows, mixed, or unset), each
with its id, name, and collection type. For a caller who is not an administrator
it SHALL list only the libraries that caller can access.

#### Scenario: Admin lists libraries
- **WHEN** an administrator requests the library list
- **THEN** every film, TV, and mixed library on the server is returned with its id
  and name

#### Scenario: Restricted user lists libraries
- **WHEN** a user without access to the Anime library requests the list
- **THEN** the Anime library is not in the response

#### Scenario: Music library omitted
- **WHEN** the server has a music library
- **THEN** it is not in the response

### Requirement: Excluded libraries in the settings UI
The admin config page and the per-user settings page SHALL show, on each
Letterboxd and Serializd account card, a checklist of libraries from the listing
endpoint, pre-checked from the account's excluded list. On save, stored ids for listed
libraries SHALL be replaced by the checked ones, and stored ids for libraries the page did
not list SHALL be kept, so a partial or failed library list never clears an exclusion. An
account save request that omits the field entirely SHALL keep the stored list.

#### Scenario: User excludes a library
- **WHEN** a user ticks "Anime" on their Serializd account card and saves
- **THEN** the stored account's excluded list contains the Anime library id and
  the next sync skips Anime episodes for that account

#### Scenario: Library the caller cannot see is kept on save
- **WHEN** an admin excluded the Anime library on a user's account, and that user,
  who has no access to Anime, edits and saves the account from their own page
- **THEN** the stored list still contains the Anime library id

#### Scenario: Client omits the field
- **WHEN** an account save request carries no excluded library list for an
  existing account
- **THEN** the account's stored list is unchanged

