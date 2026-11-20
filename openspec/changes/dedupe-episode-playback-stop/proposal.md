## Why

A first watch of a TV episode reaches Serializd twice, the second time flagged as a rewatch (#200, plugin 2.15.1 on Jellyfin 12.4). Jellyfin raises `PlaybackStopped` twice for one finish, about a second apart, and the real-time episode path has no duplicate guard: the first event logs the episode and records it in the local Serializd history, and the second event finds that record and logs a rewatch. The film path does not have this problem because it is serialised per film and checks for a same-day entry first.

## What Changes

- The real-time episode scrobble serialises per Jellyfin user, Serializd account and episode, so two events for the same finish cannot both pass the checks at once.
- A second completed-playback event for the same episode, user and account within a short window (5 minutes) of a logged finish is treated as the same finish: it is skipped and logged, and nothing is sent to Serializd or recorded.
- A finish outside that window still logs a rewatch, as today.
- Dependency patch bumps from Dependabot #202 ride along in the same release (Microsoft.Extensions.DependencyInjection and Microsoft.Extensions.Hosting.Abstractions 9.0.23; test-only coverlet.collector 10.1.1 and Microsoft.NET.Test.Sdk 18.12.0).

## Non-goals

- Films: they already have their own duplicate guards.
- The scheduled Serializd catch-up, which never marks a log as a rewatch.
- Removing the duplicate entries already on users' Serializd diaries.
- Finding out why Jellyfin raises the event twice; the plugin has to cope either way.

## Capabilities

### New Capabilities

### Modified Capabilities
- `serializd-sync`: the real-time episode scrobble gains a requirement that one finish logs one diary entry, even when Jellyfin reports it more than once.

## Impact

- `LetterboxdSync/PlaybackHandler.cs` (episode path), a new in-memory guard beside `FilmSyncLock.cs`, and tests in `LetterboxdSync.Tests/Serializd/SerializdPlaybackTests.cs`.
- Package references in `LetterboxdSync/LetterboxdSync.csproj` and `LetterboxdSync.Tests/LetterboxdSync.Tests.csproj`.
- Patch release 2.15.2. No configuration, API or stored-data change.
