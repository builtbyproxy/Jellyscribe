using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

/// <summary>
/// Drives PlaybackHandler's episode path (Serializd). Series-tmdb reading and the
/// service factory are both overridden so tests don't need the Jellyfin library graph
/// or a live API.
/// </summary>
[Collection("Plugin")]
public class SerializdPlaybackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly PlaybackHandler _handler;

    public SerializdPlaybackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-sz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());
        new Plugin(paths, xml);

        _handler = new PlaybackHandler(
            Substitute.For<ISessionManager>(),
            Substitute.For<IUserDataManager>(),
            _libraryManager,
            new LoggerFactory().CreateLogger<PlaybackHandler>());

        // Isolate the dated-log dedup history and activity feed to this test's temp dir.
        SerializdSyncHistory.DataPathOverride = Path.Combine(_tempDir, "sz-history.jsonl");
        SerializdSyncHistory.ResetForTesting();
        SerializdActivity.DataPathOverride = Path.Combine(_tempDir, "sz-activity.jsonl");
        SerializdActivity.ResetForTesting();
        EpisodeFinishGuard.ResetForTesting();
        EpisodeFinishGuard.UtcNow = () => DateTime.UtcNow;
    }

    public void Dispose()
    {
        EpisodeFinishGuard.ResetForTesting();
        EpisodeFinishGuard.UtcNow = () => DateTime.UtcNow;
        SerializdServiceFactory.OverrideForTesting = null;
        LetterboxdServiceFactory.OverrideForTesting = null;
        SerializdSyncHistory.DataPathOverride = null;
        SerializdSyncHistory.ResetForTesting();
        SerializdActivity.DataPathOverride = null;
        SerializdActivity.ResetForTesting();
        SerializdSeasonFallback.SeasonLengthsReader = SerializdSeasonFallback.ReadSeasonLengths;
        // Restore a functional equivalent of the production default so a leftover
        // override can't leak into another test class.
        PlaybackHandler.SeriesTmdbIdReader = ep =>
        {
            var s = ep.Series?.GetProviderId(MetadataProvider.Tmdb);
            return int.TryParse(s, out var id) ? id : (int?)null;
        };
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static (User User, string IdHex) MakeUser(string name = "lachlan")
    {
        var u = new User(name, "test-provider-id", "test-reset-id");
        return (u, u.Id.ToString("N"));
    }

    private static Episode MakeEpisode(int season = 1, int episode = 1)
        => new() { Name = $"S{season}E{episode}", SeriesName = "Breaking Bad", ParentIndexNumber = season, IndexNumber = episode };

    private void AddSerializdAccount(string userId, bool enabled = true)
    {
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = userId,
            Email = "me@example.com",
            Password = "pw",
            Enabled = enabled,
        });
    }

    [Fact]
    public async Task Episode_WithLinkedAccount_LogsToSerializd()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;

        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(1396, 1).Returns(Task.FromResult<int?>(3572));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(svc);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(1, 4),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        await svc.Received(1).LogEpisodesAsync(1396, 3572,
            Arg.Is<IReadOnlyList<int>>(l => l.Count == 1 && l[0] == 4));
        // Also creates a dated diary log for the episode (backdated to ~now, first watch = not a rewatch).
        await svc.Received(1).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
    }

    [Fact]
    public async Task Episode_InASeasonSerializdKeepsAsOne_LogsAtTheAbsoluteNumber()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => 220542;
        SerializdSeasonFallback.SeasonLengthsReader = _ => new Dictionary<int, int> { [1] = 24, [2] = 24 };

        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(220542, Arg.Any<int>()).Returns(Task.FromResult<int?>(null));
        svc.ResolveSeasonIdAsync(220542, 1).Returns(Task.FromResult<int?>(9001));
        svc.GetSeasonEpisodeCountAsync(220542, 1).Returns(Task.FromResult<int?>(48));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(svc);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(2, 1),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        await svc.Received(1).LogEpisodesAsync(220542, 9001,
            Arg.Is<IReadOnlyList<int>>(l => l.Count == 1 && l[0] == 25));
        await svc.Received(1).CreateEpisodeLogAsync(220542, 9001, 25, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        Assert.True(SerializdSyncHistory.Has(idHex, "me@example.com", 220542, 2, 1, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Episode_PastTheEndOfSerializdsSingleSeason_DoesNotLog()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => 220542;
        SerializdSeasonFallback.SeasonLengthsReader = _ => new Dictionary<int, int> { [1] = 20, [2] = 24 };

        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(220542, Arg.Any<int>()).Returns(Task.FromResult<int?>(null));
        svc.ResolveSeasonIdAsync(220542, 1).Returns(Task.FromResult<int?>(9001));
        svc.GetSeasonEpisodeCountAsync(220542, 1).Returns(Task.FromResult<int?>(24));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(svc);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(2, 5),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        await svc.DidNotReceive().LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>());
        await svc.DidNotReceive().CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
        Assert.False(SerializdSyncHistory.Has(idHex, "me@example.com", 220542, 2, 5, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Episode_LogsActivity_WithUtcViewingDate()
    {
        // SerializdSyncRunner's catch-up stamps ViewingDate with the UTC watch date
        // (WatchedAtUtc); the real-time path must match, or the same logical watch
        // shows a different calendar day in the activity feed depending on which
        // path logged it (worst near midnight, where local and UTC dates differ).
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;

        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(1396, 1).Returns(Task.FromResult<int?>(3572));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(svc);

        var before = DateTime.UtcNow.Date;
        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(1, 4),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        var (events, _) = SerializdActivity.GetPage(0, 10, user.Username);
        var recorded = Assert.Single(events);
        Assert.Equal(before, recorded.ViewingDate!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, recorded.ViewingDate!.Value.Kind);
    }

    [Fact]
    public async Task Episode_NoSeriesTmdbId_SkipsWithoutCallingFactory()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => null;

        var factoryHit = false;
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ISerializdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task Episode_SeasonNotOnSerializd_DoesNotLog()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;

        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(Task.FromResult<int?>(null));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(svc);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        await svc.DidNotReceive().LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>());
    }

    [Fact]
    public async Task Episode_NoLinkedAccount_NoFactoryCall()
    {
        var (user, _) = MakeUser();
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;

        var factoryHit = false;
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
        { factoryHit = true; return Task.FromResult(Substitute.For<ISerializdService>()); };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        Assert.False(factoryHit);
    }

    [Fact]
    public async Task Episode_ServiceThrows_DoesNotBubbleUp()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
            throw new Exception("Serializd is down");

        // Must complete without throwing: a Serializd failure is logged, not propagated.
        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = MakeEpisode(),
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });
    }

    [Fact]
    public async Task Movie_DoesNotTriggerSerializd()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);

        var serializdHit = false;
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
        { serializdHit = true; return Task.FromResult(Substitute.For<ISerializdService>()); };

        // A movie with no Letterboxd account linked simply no-ops on the film path;
        // the point of this test is that the Serializd factory is never touched.
        var movie = new Movie { Name = "Sinners" };
        movie.SetProviderId(MetadataProvider.Tmdb, "1233413");

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        Assert.False(serializdHit);
    }

    // ----- Library exclusion (issue #124) -----

    [Fact]
    public async Task EpisodeInExcludedLibrary_SkipsOnlyTheExcludingAccount()
    {
        var animeLibraryId = Guid.Parse("0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e");
        var (user, idHex) = MakeUser();
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "excludes@example.com",
            Password = "pw",
            Enabled = true,
            ExcludedLibraryIds = { animeLibraryId.ToString("N") },
        });
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "keeps@example.com",
            Password = "pw",
            Enabled = true,
        });
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;
        var episode = MakeEpisode(1, 4);
        _libraryManager.GetCollectionFolders(episode)
            .Returns(new List<MediaBrowser.Controller.Entities.Folder>
            {
                new MediaBrowser.Controller.Entities.CollectionFolder { Id = animeLibraryId }
            });

        var authedAs = new List<string>();
        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(1396, 1).Returns(Task.FromResult<int?>(3572));
        SerializdServiceFactory.OverrideForTesting = (email, _, _) =>
        {
            authedAs.Add(email);
            return Task.FromResult(svc);
        };

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = episode,
            PlayedToCompletion = true,
            Users = new List<User> { user },
        });

        Assert.Equal(new[] { "keeps@example.com" }, authedAs);
        await svc.Received(1).LogEpisodesAsync(1396, 3572,
            Arg.Is<IReadOnlyList<int>>(l => l.Count == 1 && l[0] == 4));
    }

    // ---- Repeated playback-stopped events for one finish (#200) ----

    private ISerializdService StubSerializd()
    {
        PlaybackHandler.SeriesTmdbIdReader = _ => 1396;
        var svc = Substitute.For<ISerializdService>();
        svc.ResolveSeasonIdAsync(1396, 1).Returns(Task.FromResult<int?>(3572));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(svc);
        return svc;
    }

    private static PlaybackStopEventArgs Finished(User user, Episode episode) => new()
    {
        Item = episode,
        PlayedToCompletion = true,
        Users = new List<User> { user },
    };

    [Fact]
    public async Task RepeatedStopEvent_ForOneFinish_LogsOnceAndNotAsARewatch()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        var svc = StubSerializd();
        var episode = MakeEpisode(1, 4);

        await _handler.HandlePlaybackStoppedAsync(Finished(user, episode));
        await _handler.HandlePlaybackStoppedAsync(Finished(user, episode));

        await svc.Received(1).LogEpisodesAsync(1396, 3572, Arg.Any<IReadOnlyList<int>>());
        await svc.Received(1).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        await svc.DidNotReceive().CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), true);
    }

    [Fact]
    public async Task ConcurrentStopEvents_ForOneFinish_LogOnce()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        var svc = StubSerializd();
        var release = new TaskCompletionSource();
        svc.LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>())
            .Returns(_ => release.Task);
        var episode = MakeEpisode(1, 4);

        var first = _handler.HandlePlaybackStoppedAsync(Finished(user, episode));
        var second = _handler.HandlePlaybackStoppedAsync(Finished(user, episode));
        release.SetResult();
        await Task.WhenAll(first, second);

        await svc.Received(1).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        await svc.DidNotReceive().CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), true);
    }

    [Fact]
    public async Task FinishAfterTheWindow_IsLoggedAsARewatch()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        var svc = StubSerializd();
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        EpisodeFinishGuard.UtcNow = () => now;
        var episode = MakeEpisode(1, 4);

        await _handler.HandlePlaybackStoppedAsync(Finished(user, episode));
        now = now.Add(EpisodeFinishGuard.Window).AddSeconds(1);
        await _handler.HandlePlaybackStoppedAsync(Finished(user, episode));

        await svc.Received(1).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        await svc.Received(1).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), true);
    }

    [Fact]
    public async Task FailedFirstAttempt_LetsTheRepeatRetry_WithoutARewatchFlag()
    {
        var (user, idHex) = MakeUser();
        AddSerializdAccount(idHex);
        var svc = StubSerializd();
        var calls = 0;
        svc.CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(_ => ++calls == 1 ? throw new Exception("Serializd is down") : Task.CompletedTask);
        var episode = MakeEpisode(1, 4);

        await _handler.HandlePlaybackStoppedAsync(Finished(user, episode));
        await _handler.HandlePlaybackStoppedAsync(Finished(user, episode));

        await svc.Received(2).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        await svc.DidNotReceive().CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), true);
    }

    [Fact]
    public async Task FinishWithinTheWindow_OfAnotherEpisodeOrUser_IsLoggedNormally()
    {
        var (user, idHex) = MakeUser();
        var (other, otherHex) = MakeUser("other");
        AddSerializdAccount(idHex);
        AddSerializdAccount(otherHex);
        var svc = StubSerializd();

        await _handler.HandlePlaybackStoppedAsync(Finished(user, MakeEpisode(1, 4)));
        await _handler.HandlePlaybackStoppedAsync(Finished(user, MakeEpisode(1, 5)));
        await _handler.HandlePlaybackStoppedAsync(Finished(other, MakeEpisode(1, 4)));

        // Episode 4 once for each user, episode 5 once.
        await svc.Received(2).CreateEpisodeLogAsync(1396, 3572, 4, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        await svc.Received(1).CreateEpisodeLogAsync(1396, 3572, 5, Arg.Any<DateTime>(), Arg.Any<int?>(), false);
        // The other user's history is separate, so their first watch is not a rewatch either.
        await svc.DidNotReceive().CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), true);
        await svc.Received(3).LogEpisodesAsync(1396, 3572, Arg.Any<IReadOnlyList<int>>());
    }
}
