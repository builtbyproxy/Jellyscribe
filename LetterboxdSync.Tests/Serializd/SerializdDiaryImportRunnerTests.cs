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

[Collection("Plugin")]
public class SerializdDiaryImportRunnerTests : IDisposable
{
    private const int ShowTmdbId = 1396; // Breaking Bad

    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly SerializdDiaryImportRunner _runner;

    public SerializdDiaryImportRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-szdiary-" + Guid.NewGuid().ToString("N"));
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

        SerializdSyncHistory.DataPathOverride = Path.Combine(_tempDir, "serializd-history.jsonl");
        SerializdSyncHistory.ResetForTesting();

        _userManager = Substitute.For<IUserManager>();
        _libraryManager = Substitute.For<ILibraryManager>();
        _userDataManager = Substitute.For<IUserDataManager>();

        // Episodes in these tests are detached entities with no parent-series graph;
        // resolve every one to the same show, like a real single-series library slice.
        SerializdDiaryImportRunner.SeriesTmdbIdReader = _ => ShowTmdbId;

        _runner = new SerializdDiaryImportRunner(
            NullLoggerFactory.Instance, _libraryManager, _userManager, _userDataManager);
    }

    public void Dispose()
    {
        SerializdServiceFactory.OverrideForTesting = null;
        SerializdDiaryImportRunner.SeriesTmdbIdReader = SerializdSyncRunner.ReadSeriesTmdbId;
        SerializdSyncHistory.DataPathOverride = null;
        SerializdSyncHistory.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private (User User, string IdHex) AddUserWithImportAccount(bool importEnabled = true)
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var idHex = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "user@example.com",
            Password = "pw",
            Enabled = true,
            EnableDiaryImport = importEnabled
        });
        return (user, idHex);
    }

    private static Episode MakeEpisode(int season, int number)
        => new() { Name = $"S{season:00}E{number:00}", ParentIndexNumber = season, IndexNumber = number };

    private void LibraryHas(params Episode[] episodes)
        => _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem>(episodes));

    private ISerializdService DiaryReturns(params SerializdDiaryEpisode[] entries)
    {
        var service = Substitute.For<ISerializdService>();
        service.GetDiaryEpisodesAsync().Returns(new List<SerializdDiaryEpisode>(entries));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        return service;
    }

    private static UserItemData MakeUserData(bool played = false)
        => new() { Key = "test-key", Played = played };

    [Fact]
    public async Task Run_MatchingUnplayedEpisode_MarksPlayedWithoutPlayDate()
    {
        var (user, idHex) = AddUserWithImportAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        DiaryReturns(new SerializdDiaryEpisode(ShowTmdbId, 1, 3));
        var ud = MakeUserData(played: false);
        _userDataManager.GetUserData(user, ep).Returns(ud);

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(ud.Played);
        // The loop guard: no LastPlayedDate, so the scrobble catch-up won't re-export
        // the imported episode straight back to Serializd.
        Assert.Null(ud.LastPlayedDate);
        _userDataManager.Received(1).SaveUserData(user, ep, ud, UserDataSaveReason.Import, Arg.Any<CancellationToken>());
        Assert.True(SerializdSyncHistory.Has(idHex, ShowTmdbId, 1, 3, SerializdDiaryImportRunner.KindImported));
    }

    [Fact]
    public async Task Run_AlreadyPlayedEpisode_NotTouchedOrRecorded()
    {
        var (user, idHex) = AddUserWithImportAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        DiaryReturns(new SerializdDiaryEpisode(ShowTmdbId, 1, 3));
        _userDataManager.GetUserData(user, ep).Returns(MakeUserData(played: true));

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        _userDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
        Assert.False(SerializdSyncHistory.Has(idHex, ShowTmdbId, 1, 3, SerializdDiaryImportRunner.KindImported));
    }

    [Fact]
    public async Task Run_DiaryEntryNotInLibrary_Skipped()
    {
        var (user, _) = AddUserWithImportAccount();
        LibraryHas(MakeEpisode(1, 1));
        DiaryReturns(new SerializdDiaryEpisode(ShowTmdbId, 4, 9)); // not in the library

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        _userDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_NullUserData_SkippedWithoutThrowing()
    {
        var (user, _) = AddUserWithImportAccount();
        var ep = MakeEpisode(1, 3);
        LibraryHas(ep);
        DiaryReturns(new SerializdDiaryEpisode(ShowTmdbId, 1, 3));
        _userDataManager.GetUserData(user, ep).Returns((UserItemData?)null);

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        _userDataManager.DidNotReceive().SaveUserData(
            Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UserItemData>(), Arg.Any<UserDataSaveReason>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_ImportDisabledOnAccount_NeverAuthenticates()
    {
        AddUserWithImportAccount(importEnabled: false);
        var factoryCalled = false;
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
        {
            factoryCalled = true;
            return Task.FromResult(Substitute.For<ISerializdService>());
        };

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.False(factoryCalled, "diary import is opt-in; a disabled account must never authenticate");
    }

    [Fact]
    public async Task Run_ServiceThrows_ReportsProgressAndDoesNotThrow()
    {
        AddUserWithImportAccount();
        SerializdServiceFactory.OverrideForTesting = (_, _, _) =>
            Task.FromException<ISerializdService>(new InvalidOperationException("auth down"));
        double last = 0;
        var progress = new Progress<double>(v => last = v);

        await _runner.RunForAllAsync(progress, CancellationToken.None);

        // The per-pair catch swallows the failure and the run still completes.
        await Task.Delay(10); // Progress<T> posts callbacks asynchronously
        Assert.Equal(100, last);
    }
}
