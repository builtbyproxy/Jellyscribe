using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

/// <summary>
/// End-to-end coverage of SerializdSyncRunner.SyncOneAsync's catch-up loop guard: episodes
/// marked played without a LastPlayedDate (as SerializdDiaryImportRunner deliberately leaves
/// them) must never be re-logged, and duplicate Episode items for the same logical episode
/// must only produce one dated diary log.
/// </summary>
[Collection("Plugin")]
public class SerializdSyncRunnerCatchUpTests : IDisposable
{
    private const int ShowTmdbId = 1396;
    private static readonly TimeSpan DefaultFailurePause = SerializdSyncRunner.FailurePause;

    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly SerializdSyncRunner _runner;

    public SerializdSyncRunnerCatchUpTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-szsync-" + Guid.NewGuid().ToString("N"));
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

        SerializdSyncHistory.DataPathOverride = Path.Combine(_tempDir, "serializd-sync-history.jsonl");
        SerializdSyncHistory.ResetForTesting();
        SerializdActivity.DataPathOverride = Path.Combine(_tempDir, "serializd-activity.jsonl");
        SerializdActivity.ResetForTesting();

        _userManager = Substitute.For<IUserManager>();
        _libraryManager = Substitute.For<ILibraryManager>();
        _userDataManager = Substitute.For<IUserDataManager>();

        SerializdSyncRunner.SeriesTmdbIdReader = _ => ShowTmdbId;

        _runner = new SerializdSyncRunner(NullLoggerFactory.Instance, _libraryManager, _userManager, _userDataManager);
    }

    public void Dispose()
    {
        SerializdSyncRunner.FailurePause = DefaultFailurePause;
        SerializdServiceFactory.OverrideForTesting = null;
        SerializdSyncRunner.SeriesTmdbIdReader = SerializdSyncRunner.ReadSeriesTmdbId;
        SerializdSeasonFallback.SeasonLengthsReader = SerializdSeasonFallback.ReadSeasonLengths;
        SerializdSyncHistory.DataPathOverride = null;
        SerializdSyncHistory.ResetForTesting();
        SerializdActivity.DataPathOverride = null;
        SerializdActivity.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private (User User, string IdHex) AddUserWithAccount()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var idHex = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "user@example.com",
            Password = "pw",
            Enabled = true
        });
        return (user, idHex);
    }

    // BaseItem.Id defaults to Guid.Empty when unset, and BaseItem equality is Id-based, so two
    // freshly constructed Episode instances compare equal. NSubstitute's default argument
    // matching uses that equality, which would make GetUserData(user, epA) and
    // GetUserData(user, epB) collide as "the same call" unless each episode gets a distinct Id.
    private static Episode MakeEpisode(int season, int number)
        => new() { Id = Guid.NewGuid(), Name = $"S{season:00}E{number:00}", ParentIndexNumber = season, IndexNumber = number };

    private void LibraryHas(params Episode[] episodes)
        => _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem>(episodes));

    private static UserItemData MakeUserData(DateTime? lastPlayed)
        => new() { Key = "test-key", Played = true, LastPlayedDate = lastPlayed };

    private ISerializdService FakeService(out List<(int Show, int SeasonId, int Episode)> logged)
    {
        var capturedLogged = new List<(int, int, int)>();
        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(ci => 500 + (int)ci[1]);
        service.CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(ci =>
            {
                capturedLogged.Add(((int)ci[0], (int)ci[1], (int)ci[2]));
                return Task.CompletedTask;
            });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        logged = capturedLogged;
        return service;
    }

    [Fact]
    public async Task Run_EpisodePlayedWithNoLastPlayedDate_NeverLogged()
    {
        var (user, idHex) = AddUserWithAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        // Mirrors SerializdDiaryImportRunner: Played=true, LastPlayedDate left null.
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(lastPlayed: null));
        var service = FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Empty(logged);
        await service.DidNotReceive().CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
        await service.DidNotReceive().LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>());
        Assert.False(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 1, 3, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Run_EpisodeInASeasonSerializdKeepsAsOne_LoggedAtTheAbsoluteNumber()
    {
        var (user, idHex) = AddUserWithAccount();
        var ep = MakeEpisode(2, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        SerializdSeasonFallback.SeasonLengthsReader = _ => new Dictionary<int, int> { [1] = 24, [2] = 24 };
        var service = FakeService(out var logged);
        service.ResolveSeasonIdAsync(ShowTmdbId, 2).Returns(Task.FromResult<int?>(null));
        service.GetSeasonEpisodeCountAsync(ShowTmdbId, 1).Returns(Task.FromResult<int?>(48));

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        await service.Received(1).LogEpisodesAsync(ShowTmdbId, 501,
            Arg.Is<IReadOnlyList<int>>(l => l.Count == 1 && l[0] == 27));
        Assert.Equal((ShowTmdbId, 501, 27), Assert.Single(logged));
        Assert.True(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 2, 3, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Run_EpisodePastTheEndOfSerializdsSingleSeason_NotLogged()
    {
        var (user, idHex) = AddUserWithAccount();
        var fits = MakeEpisode(2, 4);
        var tooFar = MakeEpisode(2, 5);
        LibraryHas(fits, tooFar);
        _userDataManager.GetUserData(user, Arg.Any<Episode>()).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        SerializdSeasonFallback.SeasonLengthsReader = _ => new Dictionary<int, int> { [1] = 20, [2] = 24 };
        var service = FakeService(out var logged);
        service.ResolveSeasonIdAsync(ShowTmdbId, 2).Returns(Task.FromResult<int?>(null));
        service.GetSeasonEpisodeCountAsync(ShowTmdbId, 1).Returns(Task.FromResult<int?>(24));

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        await service.Received(1).LogEpisodesAsync(ShowTmdbId, 501,
            Arg.Is<IReadOnlyList<int>>(l => l.Count == 1 && l[0] == 24));
        Assert.Equal((ShowTmdbId, 501, 24), Assert.Single(logged));
        Assert.False(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 2, 5));
        Assert.False(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 2, 5, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Run_EpisodePlayedWithEpochLastPlayedDate_NeverLogged()
    {
        // A manual checkmark can leave LastPlayedDate at an epoch-adjacent value like
        // 1970-01-01 instead of null (issue #106). That's non-null, so it must not slip
        // past the guard that also catches the null case above.
        var (user, idHex) = AddUserWithAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(new DateTime(1970, 1, 1)));
        var service = FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Empty(logged);
        await service.DidNotReceive().CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
        Assert.False(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 1, 3, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Run_EpisodePlayedWithLastPlayedDate_LoggedOnce()
    {
        var (user, idHex) = AddUserWithAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Single(logged);
        Assert.True(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 1, 3, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Run_DuplicateEpisodeItems_LoggedOnlyOnce()
    {
        // Two library items resolving to the same (show, season, episode), e.g. unmerged
        // duplicate files. Both are played with the same watch date.
        var (user, _) = AddUserWithAccount();
        var when = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var epA = MakeEpisode(1, 3);
        var epB = MakeEpisode(1, 3);
        LibraryHas(epA, epB);
        _userDataManager.GetUserData(user, epA).Returns(MakeUserData(when));
        _userDataManager.GetUserData(user, epB).Returns(MakeUserData(when));
        var service = FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Single(logged);
        await service.Received(1).CreateEpisodeLogAsync(
            ShowTmdbId, Arg.Any<int>(), 3, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Run_MixOfDatedAndUndatedEpisodes_OnlyDatedOneLogged()
    {
        var (user, idHex) = AddUserWithAccount();
        var when = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var dated = MakeEpisode(1, 1);
        var undated = MakeEpisode(1, 2);
        LibraryHas(dated, undated);
        _userDataManager.GetUserData(user, dated).Returns(MakeUserData(when));
        _userDataManager.GetUserData(user, undated).Returns(MakeUserData(null));
        FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Single(logged);
        Assert.True(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 1, 1, SerializdSyncHistory.KindLog));
        Assert.False(SerializdSyncHistory.Has(idHex, "user@example.com", ShowTmdbId, 1, 2, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public async Task Run_RatedShow_QueriesLibraryOnlyOnce()
    {
        // SyncShowMetaAsync used to re-query the whole library for BaseItemKind.Series to
        // re-find shows the episode scan had already resolved. It now reuses the Series
        // reference cached during that scan, so a run with something to log/rate performs
        // exactly one GetItemList call, not two.
        var (user, _) = AddUserWithAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        FakeService(out _);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        _libraryManager.Received(1).GetItemList(Arg.Any<InternalItemsQuery>());
    }

    // ===== Gate / TryRunForUserAsync =====

    [Fact]
    public async Task RunForAllAsync_GateAlreadyHeld_SkipsImmediately()
    {
        AddUserWithAccount();
        await SerializdSyncGate.Instance.WaitAsync(0, CancellationToken.None);
        try
        {
            await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);
            _libraryManager.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
        }
        finally
        {
            SerializdSyncGate.Instance.Release();
        }
    }

    [Fact]
    public async Task TryRunForUserAsync_GateAlreadyHeld_ReturnsFalse()
    {
        var (_, idHex) = AddUserWithAccount();
        await SerializdSyncGate.Instance.WaitAsync(0, CancellationToken.None);
        try
        {
            var result = await _runner.TryRunForUserAsync(idHex, "test", CancellationToken.None);
            Assert.False(result);
        }
        finally
        {
            SerializdSyncGate.Instance.Release();
        }
    }

    [Fact]
    public async Task TryRunForUserAsync_UnknownUser_ReturnsFalse()
    {
        _userManager.GetUsers().Returns(Array.Empty<User>());

        var result = await _runner.TryRunForUserAsync(Guid.NewGuid().ToString("N"), "test", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task TryRunForUserAsync_NoEnabledAccounts_ReturnsFalse()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        _userManager.GetUsers().Returns(new[] { user });

        var result = await _runner.TryRunForUserAsync(user.Id.ToString("N"), "test", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task TryRunForUserAsync_Success_ReturnsTrue()
    {
        var (user, idHex) = AddUserWithAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        FakeService(out _);

        var result = await _runner.TryRunForUserAsync(idHex, "test", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task Run_OneAccountThrowsDuringAuth_OtherAccountStillSyncs()
    {
        var (user, idHex) = AddUserWithAccount(); // "user@example.com"
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "second@example.com",
            Password = "pw",
            Enabled = true,
        });
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        var goodService = Substitute.For<ISerializdService>();
        goodService.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(ci => 500 + (int)ci[1]);
        SerializdServiceFactory.OverrideForTesting = (email, _, _) =>
            email == "user@example.com"
                ? throw new Exception("Serializd auth failed")
                : Task.FromResult(goodService);

        // Must not throw: the per-account try/catch in SyncOneAsync's caller isolates failures.
        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        await goodService.Received(1).CreateEpisodeLogAsync(
            ShowTmdbId, Arg.Any<int>(), 3, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
    }

    // ===== Date filter =====

    [Fact]
    public async Task Run_DateFilterEnabled_ExcludesEpisodesOutsideWindow()
    {
        var (user, idHex) = AddUserWithAccount();
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).EnableDateFilter = true;
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).DateFilterDays = 1;

        var recent = MakeEpisode(1, 1);
        var old = MakeEpisode(1, 2);
        LibraryHas(recent, old);
        _userDataManager.GetUserData(user, recent).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        _userDataManager.GetUserData(user, old).Returns(MakeUserData(DateTime.UtcNow.AddDays(-30)));
        var service = FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Single(logged);
        Assert.Equal(1, logged[0].Episode);
    }

    // ===== SkipPreviouslySynced =====

    [Fact]
    public async Task Run_SkipPreviouslySyncedFalse_ReSendsAlreadyLoggedEpisode()
    {
        var (user, idHex) = AddUserWithAccount();
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).SkipPreviouslySynced = false;

        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        var when = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(when));
        SerializdSyncHistory.Record(idHex, "user@example.com", ShowTmdbId, 1, 3, SerializdSyncHistory.KindLog);
        SerializdSyncHistory.Record(idHex, "user@example.com", ShowTmdbId, 1, 3, SerializdSyncHistory.KindWatched);
        FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Single(logged); // re-sent despite already being recorded
    }

    // ===== Season resolution failure =====

    [Fact]
    public async Task Run_SeasonNotFound_SkipsWithoutThrowing()
    {
        var (user, _) = AddUserWithAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns((int?)null);
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None); // must not throw

        await service.DidNotReceive().CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
    }

    // ===== StopOnFailure =====

    [Fact]
    public async Task Run_StopOnFailureTrue_StopsAfterFirstLogFailure()
    {
        var (user, idHex) = AddUserWithAccount();
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).StopOnFailure = true;

        var epA = MakeEpisode(1, 1);
        var epB = MakeEpisode(1, 2);
        LibraryHas(epA, epB);
        _userDataManager.GetUserData(user, epA).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        _userDataManager.GetUserData(user, epB).Returns(MakeUserData(new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)));

        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(ci => 500 + (int)ci[1]);
        service.CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new Exception("Serializd 500"));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None); // must not throw

        await service.Received(1).CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Run_StopOnFailureFalse_ContinuesAfterLogFailure()
    {
        var (user, _) = AddUserWithAccount(); // StopOnFailure defaults to false

        var epA = MakeEpisode(1, 1);
        var epB = MakeEpisode(1, 2);
        LibraryHas(epA, epB);
        _userDataManager.GetUserData(user, epA).Returns(MakeUserData(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        _userDataManager.GetUserData(user, epB).Returns(MakeUserData(new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)));

        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(ci => 500 + (int)ci[1]);
        service.CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), 1, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new Exception("Serializd 500"));
        service.CreateEpisodeLogAsync(Arg.Any<int>(), Arg.Any<int>(), 2, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(Task.CompletedTask);
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None); // must not throw

        await service.Received(1).CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), 2, Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
    }

    // ----- Library exclusion (issue #124) -----

    private static readonly Guid AnimeLibraryId = Guid.Parse("0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e");

    private void ExcludeOnAccount(Guid libraryId)
        => Plugin.Instance!.Configuration.SerializdAccounts[0].ExcludedLibraryIds.Add(libraryId.ToString("N"));

    private void InLibrary(Guid libraryId, params Episode[] episodes)
    {
        foreach (var ep in episodes)
            _libraryManager.GetCollectionFolders(ep).Returns(new List<Folder> { new CollectionFolder { Id = libraryId } });
    }

    [Fact]
    public async Task Run_EpisodesInExcludedLibrary_NeverLoggedAndNoAuth()
    {
        var (user, _) = AddUserWithAccount();
        ExcludeOnAccount(AnimeLibraryId);
        var seriesId = Guid.NewGuid();
        var e1 = MakeEpisode(1, 1);
        var e2 = MakeEpisode(1, 2);
        var e3 = MakeEpisode(1, 3);
        foreach (var ep in new[] { e1, e2, e3 })
        {
            ep.SeriesId = seriesId;
            _userDataManager.GetUserData(user, ep).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        }
        LibraryHas(e1, e2, e3);
        InLibrary(AnimeLibraryId, e1, e2, e3);
        var factoryHit = false;
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
        {
            factoryHit = true;
            return Task.FromResult(Substitute.For<ISerializdService>());
        };

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.False(factoryHit);
        // One library lookup for the whole series, not one per episode.
        _libraryManager.Received(1).GetCollectionFolders(Arg.Any<BaseItem>());
    }

    [Fact]
    public async Task Run_ExcludingAccount_StillLogsEpisodesFromOtherLibraries()
    {
        var (user, _) = AddUserWithAccount();
        ExcludeOnAccount(AnimeLibraryId);
        var anime = MakeEpisode(1, 1);
        anime.SeriesId = Guid.NewGuid();
        // SeriesId left empty on the logged episode: a set SeriesId makes ep.Series resolve through
        // Jellyfin's static BaseItem.LibraryManager, which is not wired in unit tests. This also
        // covers the uncached lookup path.
        var drama = MakeEpisode(2, 5);
        _userDataManager.GetUserData(user, anime).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        _userDataManager.GetUserData(user, drama).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        LibraryHas(anime, drama);
        InLibrary(AnimeLibraryId, anime);
        InLibrary(Guid.NewGuid(), drama);
        var service = FakeService(out var logged);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Equal(new[] { (ShowTmdbId, 502, 5) }, logged);
        await service.Received(1).LogEpisodesAsync(ShowTmdbId, 502, Arg.Is<IReadOnlyList<int>>(l => l.Count == 1 && l[0] == 5));
    }

    // When Serializd is down, the catch-up stops the account after a few failures in a row
    // instead of trying (and retrying) every remaining episode.
    [Fact]
    public async Task Run_SerializdFailingEveryCall_StopsTheAccountAfterAFewInARow()
    {
        SerializdSyncRunner.FailurePause = TimeSpan.Zero;
        var (user, _) = AddUserWithAccount();
        var episodes = new Episode[12];
        for (var i = 0; i < episodes.Length; i++)
            episodes[i] = MakeEpisode(i + 1, 1);
        LibraryHas(episodes);
        _userDataManager.GetUserData(user, Arg.Any<Episode>()).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        var service = FakeService(out _);
        service.LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>())
            .Returns(Task.FromException(new System.Net.Http.HttpRequestException("Serializd returned 503")));

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        await service.Received(SerializdSyncRunner.MaxConsecutiveFailures)
            .LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>());
        await service.DidNotReceive().CreateEpisodeLogAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Arg.Any<bool>());
        var (events, _) = SerializdActivity.GetPage(0, 50);
        Assert.Contains(events, e => e.Status == SyncStatus.Skipped && e.FilmTitle.Contains("paused", StringComparison.Ordinal));
    }

    // A few items Serializd rejects on their own (a 400) are not an outage: the rest still sync.
    [Fact]
    public async Task Run_ItemsRejectedOnTheirOwn_AtTheHeadOfTheQueue_DoNotStopTheAccount()
    {
        SerializdSyncRunner.FailurePause = TimeSpan.Zero;
        var (user, _) = AddUserWithAccount();
        var episodes = new Episode[2 * SerializdSyncRunner.MaxConsecutiveFailures];
        for (var i = 0; i < episodes.Length; i++)
            episodes[i] = MakeEpisode(i + 1, 1);
        LibraryHas(episodes);
        _userDataManager.GetUserData(user, Arg.Any<Episode>()).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        var service = FakeService(out var logged);
        var calls = 0;
        service.LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>())
            .Returns(_ => ++calls <= SerializdSyncRunner.MaxConsecutiveFailures + 1
                ? Task.FromException(new SerializdRequestException(System.Net.HttpStatusCode.BadRequest, "Serializd /episode/log failed (400): bad season"))
                : Task.CompletedTask);

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Equal(episodes.Length, calls);
        Assert.Equal(episodes.Length, logged.Count);
    }

    [Fact]
    public async Task Run_ASuccessBetweenFailures_ResetsTheCount()
    {
        SerializdSyncRunner.FailurePause = TimeSpan.Zero;
        var (user, _) = AddUserWithAccount();
        var episodes = new Episode[2 * SerializdSyncRunner.MaxConsecutiveFailures];
        for (var i = 0; i < episodes.Length; i++)
            episodes[i] = MakeEpisode(i + 1, 1);
        LibraryHas(episodes);
        _userDataManager.GetUserData(user, Arg.Any<Episode>()).Returns(MakeUserData(DateTime.UtcNow.AddHours(-1)));
        var service = FakeService(out _);
        var calls = 0;
        service.LogEpisodesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>())
            .Returns(_ => ++calls % 2 == 0
                ? Task.CompletedTask
                : Task.FromException(new System.Net.Http.HttpRequestException("Serializd returned 503")));

        await _runner.RunForAllAsync(new Progress<double>(), "test", CancellationToken.None);

        Assert.Equal(episodes.Length, calls);
    }
}
