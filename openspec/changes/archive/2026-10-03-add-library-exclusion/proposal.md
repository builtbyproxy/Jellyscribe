## Why

Jellyscribe exports every completed watch from every Jellyfin library. Users who
track some content elsewhere (issue #124: an Anime library already tracked on
AniList) end up with duplicate, unwanted entries on Letterboxd and Serializd, and
have no way to opt a library out short of disabling the account.

## What Changes

- Each Letterboxd account and each Serializd account gains an **excluded
  libraries** setting: a list of Jellyfin library ids. Empty (the default) keeps
  today's behavior of syncing everything, so existing configs need no migration.
- Every export path skips items that live in an excluded library:
  - scheduled Letterboxd sync (films),
  - scheduled Serializd catch-up (episodes),
  - real-time sync on playback completion (films and episodes).
- The admin config page and the per-user settings page show a checklist of
  Jellyfin libraries on each account card, so the user can tick the ones to
  exclude.
- A new read-only endpoint lists the Jellyfin libraries the caller can see, to
  populate that checklist.
- Skipped items are counted in the sync log, the same way items without a watch
  date are counted today.

## Non-goals

- Import paths are unchanged: diary import, watchlist-to-playlist sync, and
  Seerr auto-requests still consider every library.
- No "include only" mode. An exclude list covers the reported need; an include
  mode can be added later without changing the stored data.
- No server-wide exclusion list. The setting is per account, matching every
  other sync option.
- No per-item or per-series exclusions.
- Items already posted to Letterboxd or Serializd are not removed when a library
  is excluded later.

## Capabilities

### New Capabilities
- `library-exclusion`: per-account exclusion of Jellyfin libraries from export
  to Letterboxd and Serializd, the library listing endpoint, and the config UI.

### Modified Capabilities
- `serializd-sync`: the real-time episode scrobble and the scheduled catch-up
  no longer log episodes that sit in a library excluded by that account.

## Impact

- Config: `Account` and `SerializdAccount` gain an `ExcludedLibraryIds` list
  (XML-serialized in the plugin config; absent means empty).
- Sync: `LetterboxdSyncRunner`, `Serializd/SerializdSyncRunner`, and
  `PlaybackHandler` filter by library per account; a small shared helper resolves
  an item's libraries.
- API: `LetterboxdController` and `SerializdController` account GET/PUT payloads
  carry the new field; a new `LibrariesController` serves the library listing.
- UI: `Web/configPage.html` and `Web/userPage.html` account cards.
- Tests: unit tests for the helper, each runner, and `PlaybackHandler`.
- Shipping change: minor version bump, release notes, site release entry.
