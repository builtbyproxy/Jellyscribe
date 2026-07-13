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
        SerializdServiceFactory.OverrideForTesting = null;
        SerializdSyncRunner.SeriesTmdbIdReader = SerializdSyncRunner.ReadSeriesTmdbId;
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
        Assert.False(SerializdSyncHistory.Has(idHex, ShowTmdbId, 1, 3, SerializdSyncHistory.KindLog));
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
        Assert.True(SerializdSyncHistory.Has(idHex, ShowTmdbId, 1, 3, SerializdSyncHistory.KindLog));
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
        Assert.True(SerializdSyncHistory.Has(idHex, ShowTmdbId, 1, 1, SerializdSyncHistory.KindLog));
        Assert.False(SerializdSyncHistory.Has(idHex, ShowTmdbId, 1, 2, SerializdSyncHistory.KindLog));
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
}
