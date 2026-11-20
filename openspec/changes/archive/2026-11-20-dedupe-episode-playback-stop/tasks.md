## 1. Guard

- [x] 1.1 Add a key-based `AcquireAsync(string key, CancellationToken)` to `FilmSyncLock` and route the film overload through it
- [x] 1.2 Add `EpisodeFinishGuard`: per (user, account, show, season, episode range) key, 5 minute window, `UtcNow` override for tests, pruning of expired entries

## 2. Playback handler

- [x] 2.1 In `PlaybackHandler.HandleEpisodeAsync`, hold the episode lock per account, skip and log a finish inside the window, and mark the finish only after Serializd accepted the diary logs
- [x] 2.2 Tests in `SerializdPlaybackTests`: repeated event logs one non-rewatch entry; concurrent events log once; a finish after the window logs a rewatch; a failed first attempt lets the repeat retry without a rewatch flag; other episodes, users and accounts are unaffected

## 3. Dependencies

- [x] 3.1 Apply the Dependabot #202 bumps (Microsoft.Extensions.* 9.0.23, coverlet.collector 10.1.1, Microsoft.NET.Test.Sdk 18.12.0) and close #202 once the release PR is open

## 4. Release

- [x] 4.1 Bump to 2.15.2.0 in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`, add the `release-notes.ts` entry, and write the PR's `## Release notes` section
- [x] 4.2 `dotnet build -c Release` and `dotnet test -c Release` pass

## Evidence

All tasks done 2026-11-20 in #203 (squash a8f4fea): `dotnet test -c Release` 1511 passed, 17 skipped; required CI green; Dependabot #202 closed as folded in; v2.15.2 released 2026-11-20 05:22 UTC.
