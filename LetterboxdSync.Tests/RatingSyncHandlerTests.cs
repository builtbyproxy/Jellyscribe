using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// RatingSyncHandler: which saves it accepts, the debounce, change detection against the
/// last-pushed store, per-account fan-out, and the failure paths that must not touch the
/// diary runner's history.
/// </summary>
[Collection("Plugin")]
public class RatingSyncHandlerTests : IDisposable
{
    private const int TmdbId = 1233413;
    private readonly string _tempDir;
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IUserDataManager _userDataManager = Substitute.For<IUserDataManager>();
    private readonly RatingSyncHandler _handler;
    private readonly User _user = new("lachlan", "test-provider-id", "test-reset-id");
    private readonly Movie _movie;
    private DateTime _now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly List<(string Slug, string FilmId, double Rating, string Account)> _pushes = new();
    private int _factoryCalls;

    public RatingSyncHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-rating-" + Guid.NewGuid().ToString("N"));
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

        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "sync-history.jsonl");
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = Path.Combine(_tempDir, "auth-breaker.json");
        AuthBreaker.ResetForTesting();
        RatingPushStore.DataPathOverride = Path.Combine(_tempDir, "rating-pushes.jsonl");
        RatingPushStore.ResetForTesting();

        _movie = new Movie { Id = Guid.NewGuid(), Name = "Sinners" };
        _movie.SetProviderId(MetadataProvider.Tmdb, TmdbId.ToString());
        _userManager.GetUserById(_user.Id).Returns(_user);
        _libraryManager.GetItemById(_movie.Id).Returns(_movie);

        LetterboxdServiceFactory.OverrideForTesting = (username, _, _, _, _) =>
        {
            Interlocked.Increment(ref _factoryCalls);
            var service = Substitute.For<ILetterboxdService>();
            service.LookupFilmByTmdbIdAsync(TmdbId).Returns(new FilmResult("sinners-2025", "KQMM", null));
            service.SetFilmRatingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
                .Returns(ci =>
                {
                    lock (_pushes) _pushes.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<double>(2), username));
                    return Task.CompletedTask;
                });
            return Task.FromResult(service);
        };

        _handler = new RatingSyncHandler(_userDataManager, _userManager, _libraryManager,
            new LoggerFactory().CreateLogger<RatingSyncHandler>())
        {
            UtcNow = () => _now
        };
    }

    public void Dispose()
    {
        _handler.Dispose();
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        RatingPushStore.DataPathOverride = null;
        RatingPushStore.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private string UserId => _user.Id.ToString("N");

    private Account AddAccount(string lbUser = "lb-user", bool syncRatings = true, bool enabled = true)
    {
        var account = new Account
        {
            UserJellyfinId = UserId,
            LetterboxdUsername = lbUser,
            LetterboxdPassword = "secret",
            Enabled = enabled,
            SyncRatings = syncRatings
        };
        Plugin.Instance!.Configuration.Accounts.Add(account);
        return account;
    }

    private void Save(double? rating, UserDataSaveReason reason = UserDataSaveReason.UpdateUserData, BaseItem? item = null)
        => _handler.Observe(new UserDataSaveEventArgs
        {
            UserId = _user.Id,
            Item = item ?? _movie,
            SaveReason = reason,
            UserData = new UserItemData { Key = "k", Rating = rating }
        });

    private async Task DrainAfterWindow()
    {
        _now += RatingSyncHandler.DebounceWindow + TimeSpan.FromSeconds(1);
        await _handler.DrainDueAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(UserDataSaveReason.Import)]
    [InlineData(UserDataSaveReason.PlaybackProgress)]
    [InlineData(UserDataSaveReason.PlaybackFinished)]
    [InlineData(UserDataSaveReason.TogglePlayed)]
    public void Observe_IgnoresNonUserReasons(UserDataSaveReason reason)
    {
        AddAccount();
        Save(8, reason);
        Assert.Equal(0, _handler.PendingCount);
    }

    [Theory]
    [InlineData(UserDataSaveReason.UpdateUserData)]
    [InlineData(UserDataSaveReason.UpdateUserRating)]
    public void Observe_AcceptsRatingCarryingReasons(UserDataSaveReason reason)
    {
        AddAccount();
        Save(8, reason);
        Assert.Equal(1, _handler.PendingCount);
    }

    [Fact]
    public void Observe_IgnoresNonMovies()
    {
        AddAccount();
        Save(8, item: new Episode { Id = Guid.NewGuid(), Name = "Pilot" });
        Assert.Equal(0, _handler.PendingCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void Observe_NoPositiveRating_QueuesNothing(double? rating)
    {
        AddAccount();
        Save(rating);
        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public void Observe_NoAccountWithRatingSyncOn_QueuesNothing()
    {
        AddAccount(syncRatings: false);
        Save(8);
        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public void Observe_QueueCap_DropsNewFilmsOnly()
    {
        AddAccount();
        for (var i = 0; i < RatingSyncHandler.MaxPending; i++)
            Save(8, item: new Movie { Id = Guid.NewGuid(), Name = "M" + i });

        Save(8, item: new Movie { Id = Guid.NewGuid(), Name = "one too many" });
        Assert.Equal(RatingSyncHandler.MaxPending, _handler.PendingCount);
    }

    [Fact]
    public async Task Debounce_TapsWithinWindow_PushOnceWithFinalValue()
    {
        AddAccount();
        Save(6);
        _now += TimeSpan.FromSeconds(3);
        Save(7);
        _now += TimeSpan.FromSeconds(3);
        Save(9);

        // 8s after the last tap: still inside the window.
        _now += TimeSpan.FromSeconds(8);
        await _handler.DrainDueAsync(CancellationToken.None);
        Assert.Empty(_pushes);

        await DrainAfterWindow();
        var push = Assert.Single(_pushes);
        Assert.Equal(("sinners-2025", "KQMM", 4.5), (push.Slug, push.FilmId, push.Rating));
        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public async Task Push_RecordsStoreAndRatedHistoryEvent()
    {
        AddAccount();
        Save(7);
        await DrainAfterWindow();

        Assert.Equal(3.5, RatingPushStore.GetLastPushed(UserId, "lb-user", TmdbId));
        var evt = Assert.Single(SyncHistory.GetPage(0, 10, "lachlan").Events);
        Assert.Equal(SyncStatus.Rated, evt.Status);
        Assert.Equal(SyncEventSources.Rating, evt.Source);
        Assert.Equal("Sinners · Rated 3.5 stars", evt.FilmTitle);
        Assert.Null(evt.ViewingDate);
    }

    [Fact]
    public async Task Push_RatedEvent_DoesNotSatisfyDiaryDuplicateBackstop()
    {
        AddAccount();
        Save(7);
        await DrainAfterWindow();

        Assert.Null(SyncHistory.GetLastSuccessfulSyncDate("lachlan", TmdbId));
    }

    [Fact]
    public async Task FavoriteToggleOnPushedFilm_PushesNothing()
    {
        AddAccount();
        Save(7);
        await DrainAfterWindow();
        Assert.Single(_pushes);

        // Jellyfin saves favorite toggles with UpdateUserRating; the rating is unchanged.
        Save(7, UserDataSaveReason.UpdateUserRating);
        await DrainAfterWindow();

        Assert.Single(_pushes);
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public async Task EditMappingToSameHalfStar_PushesNothing()
    {
        AddAccount();
        RatingPushStore.RecordPushed(UserId, "lb-user", TmdbId, 3.5);

        Save(7.2);
        await DrainAfterWindow();

        Assert.Empty(_pushes);
        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public async Task StoreReloadedAfterRestart_StillSuppressesNoOpSave()
    {
        AddAccount();
        Save(7);
        await DrainAfterWindow();

        RatingPushStore.ResetForTesting(); // simulates a restart: next read reloads from disk
        Save(7);
        await DrainAfterWindow();

        Assert.Single(_pushes);
    }

    [Fact]
    public async Task PushFailure_LeavesStoreUntouched_RecordsNoFailedEvent_AndRetriesNextSave()
    {
        AddAccount();
        var failing = Substitute.For<ILetterboxdService>();
        failing.LookupFilmByTmdbIdAsync(TmdbId).Returns(new FilmResult("sinners-2025", "KQMM", null));
        failing.SetFilmRatingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .ThrowsAsync(new Exception("Letterboxd rejected rating"));
        var real = LetterboxdServiceFactory.OverrideForTesting;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(failing);

        Save(7);
        await DrainAfterWindow();

        Assert.Null(RatingPushStore.GetLastPushed(UserId, "lb-user", TmdbId));
        Assert.Empty(SyncHistory.GetPage(0, 10, "lachlan").Events);
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount("lachlan", TmdbId));

        LetterboxdServiceFactory.OverrideForTesting = real;
        Save(7);
        await DrainAfterWindow();
        Assert.Single(_pushes);
    }

    [Fact]
    public async Task FanOut_PushesToEachAccountWithRatingSyncOn()
    {
        AddAccount("main");
        AddAccount("second");
        AddAccount("opted-out", syncRatings: false);
        AddAccount("disabled", enabled: false);

        Save(9);
        await DrainAfterWindow();

        Assert.Equal(new[] { "main", "second" }, _pushes.Select(p => p.Account).OrderBy(a => a).ToArray());
        Assert.All(_pushes, p => Assert.Equal(4.5, p.Rating));
    }

    [Fact]
    public async Task OpenBreaker_SkipsWithoutLogin()
    {
        AddAccount();
        for (var i = 0; i < AuthBreaker.Threshold; i++) AuthBreaker.RecordFailure(UserId, "lb-user", "bad password");

        Save(8);
        await DrainAfterWindow();

        Assert.Equal(0, _factoryCalls);
        Assert.Null(RatingPushStore.GetLastPushed(UserId, "lb-user", TmdbId));
    }

    [Fact]
    public async Task AuthFailure_CountsTowardBreaker_WithoutFailedHistory()
    {
        AddAccount();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            Task.FromException<ILetterboxdService>(new Exception("bad password"));

        Save(8);
        await DrainAfterWindow();

        Assert.Equal(1, AuthBreaker.GetState(UserId, "lb-user")?.ConsecutiveFailures);
        Assert.Empty(SyncHistory.GetPage(0, 10, "lachlan").Events);
    }

    [Fact]
    public async Task MovieWithoutTmdbId_IsSkipped()
    {
        AddAccount();
        var noTmdb = new Movie { Id = Guid.NewGuid(), Name = "Home video" };
        _libraryManager.GetItemById(noTmdb.Id).Returns(noTmdb);

        Save(8, item: noTmdb);
        await DrainAfterWindow();

        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public async Task StartThenStop_SubscribesUnsubscribesAndEndsTheLoop()
    {
        await _handler.StartAsync(CancellationToken.None);
        _userDataManager.Received(1).UserDataSaved += Arg.Any<EventHandler<UserDataSaveEventArgs>>();

        var stop = _handler.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        _userDataManager.Received(1).UserDataSaved -= Arg.Any<EventHandler<UserDataSaveEventArgs>>();
    }
}
