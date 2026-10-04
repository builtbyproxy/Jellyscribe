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

        _handler = NewHandler();
    }

    private RatingSyncHandler NewHandler() =>
        new(_userDataManager, _userManager, _libraryManager, new LoggerFactory().CreateLogger<RatingSyncHandler>())
        {
            UtcNow = () => _now,
            PushSpacing = TimeSpan.Zero
        };

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

    private void Save(double? rating, UserDataSaveReason reason = UserDataSaveReason.UpdateUserData, BaseItem? item = null, RatingSyncHandler? handler = null)
        => (handler ?? _handler).Observe(new UserDataSaveEventArgs
        {
            UserId = _user.Id,
            Item = item ?? _movie,
            SaveReason = reason,
            UserData = new UserItemData { Key = "k", Rating = rating }
        });

    private async Task DrainAfterWindow(RatingSyncHandler? handler = null)
    {
        _now += RatingSyncHandler.DebounceWindow + TimeSpan.FromSeconds(1);
        await (handler ?? _handler).DrainDueAsync(CancellationToken.None);
    }

    private async Task DrainAfterRetryDelay(int attempt)
    {
        _now += RatingSyncHandler.RetryDelay * attempt + TimeSpan.FromSeconds(1);
        await _handler.DrainDueAsync(CancellationToken.None);
    }

    private ILetterboxdService ServiceThatFailsRating(Action? onRate = null)
    {
        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>()).Returns(new FilmResult("sinners-2025", "KQMM", null));
        service.SetFilmRatingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .Returns(_ => { onRate?.Invoke(); return Task.FromException(new Exception("Letterboxd 503")); });
        return service;
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
    public async Task AfterRestart_StoreStillSuppressesAlreadyPushedValue()
    {
        AddAccount();
        Save(7);
        await DrainAfterWindow();

        // A fresh handler (no in-memory state) whose first save of this film arrives before its
        // baseline is in: the persisted store is what stops the duplicate push.
        RatingPushStore.ResetForTesting();
        var restarted = NewHandler();
        Save(7, handler: restarted);
        await DrainAfterWindow(restarted);

        Assert.Single(_pushes);
    }

    [Fact]
    public async Task PushFailure_LeavesStoreUntouched_RecordsNoFailedEvent_AndRetriesWithBackoff()
    {
        AddAccount();
        var real = LetterboxdServiceFactory.OverrideForTesting;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(ServiceThatFailsRating());

        Save(7);
        await DrainAfterWindow();

        Assert.Null(RatingPushStore.GetLastPushed(UserId, "lb-user", TmdbId));
        Assert.Empty(SyncHistory.GetPage(0, 10, "lachlan").Events);
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount("lachlan", TmdbId));
        Assert.True(_handler.TryGetPending(_user.Id, _movie.Id, out var queued));
        Assert.Equal(2, queued.Attempt);

        // Not due again until the backoff passes.
        await DrainAfterWindow();
        Assert.Equal(1, _handler.PendingCount);

        LetterboxdServiceFactory.OverrideForTesting = real;
        await DrainAfterRetryDelay(1);
        Assert.Single(_pushes);
        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public async Task PushFailure_GivesUpAfterMaxAttempts()
    {
        AddAccount();
        var attempts = 0;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            Task.FromResult(ServiceThatFailsRating(() => attempts++));

        Save(7);
        await DrainAfterWindow();
        for (var attempt = 1; attempt < RatingSyncHandler.MaxAttempts; attempt++)
            await DrainAfterRetryDelay(attempt);

        Assert.Equal(RatingSyncHandler.MaxAttempts, attempts);
        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public async Task AuthFailure_IsNotRetried()
    {
        AddAccount();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            Task.FromException<ILetterboxdService>(new Exception("bad password"));

        Save(8);
        await DrainAfterWindow();

        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public void Baseline_UnchangedSaveOfAPreExistingRating_QueuesNothing()
    {
        // The reported hazard: a film rated before this version, later re-rated on Letterboxd.
        // Favoriting it in Jellyfin must not push the stale Jellyfin rating over Letterboxd's.
        AddAccount();
        _userManager.GetUsers().Returns(new[] { _user });
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { _movie });
        _userDataManager.GetUserData(_user, _movie).Returns(new UserItemData { Key = "k", Rating = 8 });

        _handler.SeedBaseline(CancellationToken.None);
        _handler.MarkBaselineReady();

        Save(8, UserDataSaveReason.UpdateUserRating);
        Assert.Equal(0, _handler.PendingCount);

        Save(6);
        Assert.Equal(1, _handler.PendingCount);
    }

    [Fact]
    public void Baseline_NeverOverwritesAChangeObservedDuringSeeding()
    {
        AddAccount();
        Save(9); // arrives while the baseline is still being read
        _userManager.GetUsers().Returns(new[] { _user });
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { _movie });
        _userDataManager.GetUserData(_user, _movie).Returns(new UserItemData { Key = "k", Rating = 8 });

        _handler.SeedBaseline(CancellationToken.None);
        _handler.MarkBaselineReady();

        Save(9, UserDataSaveReason.UpdateUserRating);
        Assert.True(_handler.TryGetPending(_user.Id, _movie.Id, out var pending));
        Assert.Equal(9, pending.Rating);
    }

    [Fact]
    public void AfterBaseline_FirstRatingOfAnUnratedFilm_IsAChange()
    {
        AddAccount();
        _handler.MarkBaselineReady();

        Save(8);
        Assert.Equal(1, _handler.PendingCount);
    }

    [Fact]
    public void ClearingWhilePending_DropsThePendingPush()
    {
        AddAccount();
        Save(8);
        Save(null);
        Assert.Equal(0, _handler.PendingCount);
    }

    [Fact]
    public void QueueCap_StillUpdatesFilmsAlreadyQueued()
    {
        AddAccount();
        Save(6);
        for (var i = 1; i < RatingSyncHandler.MaxPending; i++)
            Save(8, item: new Movie { Id = Guid.NewGuid(), Name = "M" + i });

        Save(9);
        Assert.True(_handler.TryGetPending(_user.Id, _movie.Id, out var pending));
        Assert.Equal(9, pending.Rating);
    }

    [Fact]
    public async Task TapDuringPush_IsNotLost()
    {
        AddAccount();
        var tapped = false;
        LetterboxdServiceFactory.OverrideForTesting = (username, _, _, _, _) =>
        {
            var service = Substitute.For<ILetterboxdService>();
            service.LookupFilmByTmdbIdAsync(TmdbId).Returns(new FilmResult("sinners-2025", "KQMM", null));
            service.SetFilmRatingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
                .Returns(ci =>
                {
                    lock (_pushes) _pushes.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<double>(2), username));
                    if (!tapped) { tapped = true; Save(9); }
                    return Task.CompletedTask;
                });
            return Task.FromResult(service);
        };

        Save(7);
        await DrainAfterWindow();
        Assert.Equal(1, _handler.PendingCount);

        await DrainAfterWindow();
        Assert.Equal(new[] { 3.5, 4.5 }, _pushes.Select(p => p.Rating).ToArray());
    }

    [Fact]
    public async Task OnePass_LogsInOncePerAccount()
    {
        AddAccount();
        var second = new Movie { Id = Guid.NewGuid(), Name = "Second" };
        second.SetProviderId(MetadataProvider.Tmdb, TmdbId.ToString());
        _libraryManager.GetItemById(second.Id).Returns(second);

        Save(7);
        Save(9, item: second);
        await DrainAfterWindow();

        Assert.Equal(2, _pushes.Count);
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public async Task AccountFailingInAPass_IsNotHammered_AndBothFilmsAreRequeued()
    {
        AddAccount();
        var attempts = 0;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            Task.FromResult(ServiceThatFailsRating(() => attempts++));
        var second = new Movie { Id = Guid.NewGuid(), Name = "Second" };
        second.SetProviderId(MetadataProvider.Tmdb, "238");
        _libraryManager.GetItemById(second.Id).Returns(second);

        Save(7);
        Save(9, item: second);
        await DrainAfterWindow();

        Assert.Equal(1, attempts);
        Assert.Equal(2, _handler.PendingCount);
    }

    [Fact]
    public async Task AlreadyPushedValue_SkipsTheLibraryLookup()
    {
        AddAccount().ExcludedLibraryIds = new List<string> { Guid.NewGuid().ToString("N") };
        RatingPushStore.RecordPushed(UserId, "lb-user", TmdbId, 4.0);

        Save(8);
        await DrainAfterWindow();

        _libraryManager.DidNotReceive().GetCollectionFolders(Arg.Any<BaseItem>());
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
    public async Task ExcludedLibrary_BlocksPushForThatAccountOnly()
    {
        var animeLibrary = Guid.NewGuid();
        AddAccount("excludes-anime").ExcludedLibraryIds = new List<string> { animeLibrary.ToString("N") };
        AddAccount("keeps-everything");
        _libraryManager.GetCollectionFolders(_movie)
            .Returns(new List<Folder> { new CollectionFolder { Id = animeLibrary } });

        Save(8);
        await DrainAfterWindow();

        Assert.Equal(new[] { "keeps-everything" }, _pushes.Select(p => p.Account).ToArray());
        Assert.Null(RatingPushStore.GetLastPushed(UserId, "excludes-anime", TmdbId));
    }

    [Fact]
    public async Task OtherUsersSave_NeverPushesToThisUsersAccounts()
    {
        AddAccount();
        var other = new User("someone-else", "test-provider-id", "test-reset-id");
        _userManager.GetUserById(other.Id).Returns(other);

        _handler.Observe(new UserDataSaveEventArgs
        {
            UserId = other.Id,
            Item = _movie,
            SaveReason = UserDataSaveReason.UpdateUserData,
            UserData = new UserItemData { Key = "k", Rating = 8 }
        });
        await DrainAfterWindow();

        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public async Task RatingClearedAfterPush_PushesNothingAndKeepsStore()
    {
        AddAccount();
        Save(7);
        await DrainAfterWindow();

        Save(null);
        Save(0);
        await DrainAfterWindow();

        Assert.Single(_pushes);
        Assert.Equal(3.5, RatingPushStore.GetLastPushed(UserId, "lb-user", TmdbId));
    }

    [Fact]
    public async Task ToggleTurnedOffBeforeDrain_PushesNothing()
    {
        var account = AddAccount();
        Save(8);
        account.SyncRatings = false;

        await DrainAfterWindow();

        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public async Task OneFilmThrowing_DoesNotBlockOrDropOthersInTheSamePass()
    {
        AddAccount();
        var broken = new Movie { Id = Guid.NewGuid(), Name = "Broken" };
        broken.SetProviderId(MetadataProvider.Tmdb, "999");
        _libraryManager.GetItemById(broken.Id).Returns(_ => throw new InvalidOperationException("library mid-scan"));

        Save(8, item: broken);
        Save(8);
        await DrainAfterWindow();

        Assert.Single(_pushes);
        Assert.True(_handler.TryGetPending(_user.Id, broken.Id, out var retry));
        Assert.Equal(2, retry.Attempt);
    }

    [Fact]
    public async Task StartThenStop_SubscribesUnsubscribesAndEndsTheLoop()
    {
        _userManager.GetUsers().Returns(Array.Empty<User>());
        await _handler.StartAsync(CancellationToken.None);
        _userDataManager.Received(1).UserDataSaved += Arg.Any<EventHandler<UserDataSaveEventArgs>>();

        var stop = _handler.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        _userDataManager.Received(1).UserDataSaved -= Arg.Any<EventHandler<UserDataSaveEventArgs>>();
    }
}
