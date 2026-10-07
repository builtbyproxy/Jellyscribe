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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Which day a watch is logged on, for which account, and how often: the scheduled runner and the
/// real-time handler must agree on the viewing date, scope history per Letterboxd account, never
/// post on an unanswered diary check, and never give up on a film because Letterboxd had a bad day.
/// </summary>
[Collection("Plugin")]
public class SyncCorrectnessTests : IDisposable
{
    private const int FilmTmdb = 1233413;
    private readonly string _tempDir;
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IUserDataManager _userDataManager = Substitute.For<IUserDataManager>();
    private readonly LetterboxdSyncRunner _runner;
    private readonly PlaybackHandler _handler;

    // UTC-8 with no daylight saving, so the expected local day never depends on the machine.
    private static readonly TimeZoneInfo UtcMinus8 =
        TimeZoneInfo.CreateCustomTimeZone("test-utc-minus-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

    public SyncCorrectnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-synccor-" + Guid.NewGuid().ToString("N"));
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

        _runner = new LetterboxdSyncRunner(NullLoggerFactory.Instance, _libraryManager, _userManager, _userDataManager);
        _handler = new PlaybackHandler(Substitute.For<ISessionManager>(), _userDataManager, _libraryManager,
            new LoggerFactory().CreateLogger<PlaybackHandler>());
    }

    public void Dispose()
    {
        Helpers.LocalTimeZoneOverride = null;
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static User MakeUser(string name = "lachlan") => new(name, "test-provider-id", "test-reset-id");

    private static Movie MakeMovie(int tmdbId = FilmTmdb, string name = "Sinners")
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return movie;
    }

    private void AddAccount(User user, string lbUser, bool skipPreviouslySynced = true)
        => Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = user.Id.ToString("N"),
            LetterboxdUsername = lbUser,
            LetterboxdPassword = "secret",
            Enabled = true,
            SkipPreviouslySynced = skipPreviouslySynced
        });

    private void Library(User user, params (Movie Movie, DateTime LastPlayedUtc)[] films)
    {
        _userManager.GetUsers().Returns(new[] { user });
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(films.Select(f => (BaseItem)f.Movie).ToList());
        foreach (var (movie, played) in films)
            _userDataManager.GetUserData(user, movie).Returns(
                new UserItemData { Key = movie.Id.ToString("N"), Played = true, LastPlayedDate = played });
    }

    private static ILetterboxdService Service(DiaryInfo? diary = null)
    {
        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns(ci => new FilmResult("film-" + ci.Arg<int>(), "LID" + ci.Arg<int>(), null));
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(diary ?? new DiaryInfo(null, false));
        return service;
    }

    private static Task MarkedAny(ILetterboxdService s, int times)
        => s.Received(times).MarkAsWatchedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime?>(),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<double?>());

    private Task<bool> RunAsync(User user, CancellationToken ct = default)
        => _runner.TryRunForUserAsync(user.Id.ToString("N"), "scheduled", new Progress<double>(), ct);

    // ----- History is per Letterboxd account -----

    [Fact]
    public async Task SecondAccount_StillGetsFilmTheFirstAccountAlreadyLogged()
    {
        var user = MakeUser();
        AddAccount(user, "lb-first");
        AddAccount(user, "lb-second");
        var played = DateTime.UtcNow.AddHours(-2);
        var movie = MakeMovie();
        Library(user, (movie, played));

        // lb-first logged it already (as the real-time sync would have).
        SyncHistory.Record(new SyncEvent
        {
            FilmTitle = "Sinners",
            TmdbId = FilmTmdb,
            Username = "lachlan",
            Account = "lb-first",
            Timestamp = DateTime.UtcNow,
            ViewingDate = Helpers.ToLocalViewingDate(played),
            Status = SyncStatus.Success
        });

        var services = new Dictionary<string, ILetterboxdService> { ["lb-first"] = Service(), ["lb-second"] = Service() };
        LetterboxdServiceFactory.OverrideForTesting = (lb, _, _, _, _) => Task.FromResult(services[lb]);

        Assert.True(await RunAsync(user));

        await MarkedAny(services["lb-first"], 0);
        await MarkedAny(services["lb-second"], 1);
        var row = SyncHistory.GetRecent(10, "lachlan").First(e => e.Status == SyncStatus.Success && e.Account == "lb-second");
        Assert.Equal(FilmTmdb, row.TmdbId);
    }

    [Fact]
    public void Lookups_ScopeByAccount_WithLegacyRowsCountingForEveryAccount()
    {
        var day = new DateTime(2026, 10, 6);
        var events = new List<SyncEvent>
        {
            new() { Username = "u", TmdbId = 1, ViewingDate = day, Status = SyncStatus.Success, Account = "lb-a", Timestamp = day },
            new() { Username = "u", TmdbId = 2, ViewingDate = day, Status = SyncStatus.Success, Timestamp = day },
            new() { Username = "u", TmdbId = 3, Status = SyncStatus.Failed, Account = "lb-a", PermanentFailure = true, Timestamp = day },
        };

        Assert.True(SyncHistory.WasSuccessfullySynced(events, "u", 1, day, "lb-a"));
        Assert.True(SyncHistory.WasSuccessfullySynced(events, "u", 1, day, "LB-A"));
        Assert.False(SyncHistory.WasSuccessfullySynced(events, "u", 1, day, "lb-b"));
        Assert.Null(SyncHistory.GetLastSuccessfulSyncDate(events, "u", 1, "lb-b"));
        // A row written before accounts were recorded counts for every account.
        Assert.True(SyncHistory.WasSuccessfullySynced(events, "u", 2, day, "lb-b"));
        Assert.Equal(1, SyncHistory.GetConsecutiveFailureCount(events, "u", 3, "lb-a"));
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount(events, "u", 3, "lb-b"));
        Assert.Null(SyncHistory.GetLastStatusForFilm(events, "u", 3, "lb-b"));
    }

    // ----- One viewing date: the server's local day -----

    [Fact]
    public void ToLocalViewingDate_UsesServerZoneNotUtcDay()
    {
        Helpers.LocalTimeZoneOverride = UtcMinus8;
        // 03:30 UTC on the 7th is 19:30 on the 6th at UTC-8.
        Assert.Equal(new DateTime(2026, 10, 6), Helpers.ToLocalViewingDate(new DateTime(2026, 10, 7, 3, 30, 0, DateTimeKind.Utc)));
        // Jellyfin's stored timestamps often come back with an unspecified Kind; they are UTC.
        Assert.Equal(new DateTime(2026, 10, 6), Helpers.ToLocalViewingDate(new DateTime(2026, 10, 7, 3, 30, 0)));
        Assert.Equal(new DateTime(2026, 10, 7), Helpers.ToLocalViewingDate(new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task ScheduledSync_LogsEveningWatchOnTheLocalDay()
    {
        Helpers.LocalTimeZoneOverride = UtcMinus8;
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        Library(user, (movie, new DateTime(2026, 10, 7, 3, 30, 0, DateTimeKind.Utc)));
        var service = Service();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await RunAsync(user);

        await service.Received(1).MarkAsWatchedAsync("film-" + FilmTmdb, "LID" + FilmTmdb,
            new DateTime(2026, 10, 6), Arg.Any<bool>(), Arg.Any<string?>(), false, Arg.Any<double?>());
        var row = Assert.Single(SyncHistory.GetRecent(10, "lachlan"));
        Assert.Equal(new DateTime(2026, 10, 6), row.ViewingDate);
        Assert.Equal("lb-user", row.Account);
    }

    [Fact]
    public async Task ScheduledSync_SkipsLocallyAFilmRealTimeSyncLoggedThatEvening()
    {
        Helpers.LocalTimeZoneOverride = UtcMinus8;
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        var played = DateTime.UtcNow.AddMinutes(-5);
        Library(user, (movie, played));
        var service = Service();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        // Real-time sync on finish.
        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });
        await MarkedAny(service, 1);
        service.ClearReceivedCalls();

        // The scheduled run derives the same day from LastPlayedDate, so it never asks Letterboxd.
        await RunAsync(user);

        await service.DidNotReceive().LookupFilmByTmdbIdAsync(Arg.Any<int>());
        await MarkedAny(service, 0);
    }

    // ----- A film already on the diary is settled -----

    [Fact]
    public async Task FilmAlreadyOnDiary_IsNotCheckedAgainNextRun()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        var played = DateTime.UtcNow.AddHours(-1);
        Library(user, (movie, played));
        var service = Service(new DiaryInfo(Helpers.ToLocalViewingDate(played), true));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await RunAsync(user);
        await service.Received(1).LookupFilmByTmdbIdAsync(FilmTmdb);
        var skip = Assert.Single(SyncHistory.GetRecent(10, "lachlan"));
        Assert.Equal(SyncStatus.Skipped, skip.Status);
        Assert.Equal(SyncHistory.AlreadyOnDiaryError, skip.Error);
        service.ClearReceivedCalls();

        await RunAsync(user);

        await service.DidNotReceive().LookupFilmByTmdbIdAsync(Arg.Any<int>());
    }

    // ----- Giving up on a film needs failures that are not an outage -----

    [Fact]
    public async Task TransientFailuresOnEveryRun_NeverAbandonTheFilm()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        Library(user, (movie, DateTime.UtcNow.AddHours(-1)));
        var service = Service();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .ThrowsAsync(new Exception("TMDb lookup returned 403 for /tmdb/1233413 after retries. Cloudflare is blocking."));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        for (var run = 0; run < LetterboxdSyncRunner.MaxConsecutiveSyncFailures + 1; run++)
            await RunAsync(user);

        // Every run tried again: an outage is not a reason to stop.
        await service.Received(LetterboxdSyncRunner.MaxConsecutiveSyncFailures + 1).LookupFilmByTmdbIdAsync(FilmTmdb);
        Assert.All(SyncHistory.GetRecent(10, "lachlan"), e => Assert.False(e.PermanentFailure));
    }

    [Fact]
    public async Task NotFoundFilm_CountsAsPermanent_WhenOtherFilmsSucceed()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var missing = MakeMovie(111, "Missing");
        var fine = MakeMovie(222, "Fine");
        Library(user, (missing, DateTime.UtcNow.AddHours(-1)), (fine, DateTime.UtcNow.AddHours(-1)));
        var service = Service();
        service.LookupFilmByTmdbIdAsync(111).ThrowsAsync(new FilmNotFoundException(111, "Film with TMDb ID 111 not found on Letterboxd"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await RunAsync(user);

        var failed = Assert.Single(SyncHistory.GetRecent(10, "lachlan"), e => e.Status == SyncStatus.Failed);
        Assert.True(failed.PermanentFailure);
        Assert.Equal(1, SyncHistory.GetConsecutiveFailureCount("lachlan", 111, "lb-user"));
    }

    [Fact]
    public async Task RunWhereEveryFilmFails_IsAnOutage_AndCountsTowardNothing()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var a = MakeMovie(111, "A");
        var b = MakeMovie(222, "B");
        Library(user, (a, DateTime.UtcNow.AddHours(-1)), (b, DateTime.UtcNow.AddHours(-1)));
        var service = Service();
        // Letterboxd is erroring; one film also came back "not found" during it.
        service.LookupFilmByTmdbIdAsync(111).ThrowsAsync(new FilmNotFoundException(111, "not found on Letterboxd"));
        service.LookupFilmByTmdbIdAsync(222).ThrowsAsync(new Exception("Response status code does not indicate success: 503"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await RunAsync(user);

        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount("lachlan", 111, "lb-user"));
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount("lachlan", 222, "lb-user"));
        // The outage marks are on disk too, not just in memory.
        SyncHistory.ResetForTesting();
        Assert.Equal(new FailureStreak(0, 0, 0), SyncHistory.GetFailureStreak("lachlan", 111, "lb-user"));
        Assert.Equal(new FailureStreak(0, 0, 0), SyncHistory.GetFailureStreak("lachlan", 222, "lb-user"));
        Assert.Equal(2, SyncHistory.GetRecent(10, "lachlan").Count(e => e.Status == SyncStatus.Failed));
    }

    [Fact]
    public async Task OnlyUnfindableFilmsLeft_AreAbandonedAfterTheThreshold()
    {
        // The steady state once everything findable has synced: every run tries only films
        // Letterboxd does not have. That is not an outage, so they must still be given up on.
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var a = MakeMovie(111, "A");
        var b = MakeMovie(222, "B");
        Library(user, (a, DateTime.UtcNow.AddHours(-1)), (b, DateTime.UtcNow.AddHours(-1)));
        var service = Service();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns(ci => Task.FromException<FilmResult>(new FilmNotFoundException(ci.Arg<int>(), "not found on Letterboxd")));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        for (var run = 0; run < LetterboxdSyncRunner.MaxConsecutiveSyncFailures + 1; run++)
            await RunAsync(user);

        await service.Received(LetterboxdSyncRunner.MaxConsecutiveSyncFailures).LookupFilmByTmdbIdAsync(111);
        await service.Received(LetterboxdSyncRunner.MaxConsecutiveSyncFailures).LookupFilmByTmdbIdAsync(222);
    }

    [Theory]
    [InlineData(10, 7, true)]
    [InlineData(10, 6, false)]   // ten failures packed into a bad week: not enough
    [InlineData(9, 9, false)]
    public void TransientFailures_AbandonOnlyWhenSpreadOverTime(int count, int days, bool abandoned)
    {
        var start = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var events = Enumerable.Range(0, count).Select(i => new SyncEvent
        {
            Username = "u",
            TmdbId = 1,
            Account = "lb",
            Status = SyncStatus.Failed,
            Timestamp = start.AddDays(i % days).AddMinutes(i)
        }).ToList();

        var streak = SyncHistory.GetFailureStreak(events, "u", 1, "lb");

        Assert.Equal(abandoned, LetterboxdSyncRunner.ShouldAbandon(streak));
    }

    [Fact]
    public void OutageFailures_CountTowardNothing_AndASuccessResetsTheStreak()
    {
        var start = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var outage = Enumerable.Range(0, 20).Select(i => new SyncEvent
        {
            Username = "u",
            TmdbId = 1,
            Status = SyncStatus.Failed,
            Outage = true,
            Timestamp = start.AddDays(i)
        }).ToList();
        Assert.Equal(new FailureStreak(0, 0, 0), SyncHistory.GetFailureStreak(outage, "u", 1));

        var reset = new List<SyncEvent>
        {
            new() { Username = "u", TmdbId = 1, Status = SyncStatus.Failed, PermanentFailure = true, Timestamp = start },
            new() { Username = "u", TmdbId = 1, Status = SyncStatus.Failed, PermanentFailure = true, Timestamp = start.AddDays(1) },
            new() { Username = "u", TmdbId = 1, Status = SyncStatus.Success, Timestamp = start.AddDays(2) },
            new() { Username = "u", TmdbId = 1, Status = SyncStatus.Failed, PermanentFailure = true, Timestamp = start.AddDays(3) },
        };
        Assert.Equal(1, SyncHistory.GetFailureStreak(reset, "u", 1).Permanent);
    }

    // ----- An unanswered diary check never posts -----

    [Fact]
    public async Task FailedDiaryCheck_RecordsRetryableFailure_AndDoesNotPost()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        Library(user, (movie, DateTime.UtcNow.AddHours(-1)));
        var service = Service();
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new DiaryCheckFailedException("Could not check the Letterboxd diary: returned 503"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await RunAsync(user);

        await MarkedAny(service, 0);
        var row = Assert.Single(SyncHistory.GetRecent(10, "lachlan"));
        Assert.Equal(SyncStatus.Failed, row.Status);
        Assert.False(row.PermanentFailure);
    }

    [Fact]
    public async Task RealTime_FailedDiaryCheck_DoesNotPost()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        var service = Service();
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new DiaryCheckFailedException("Could not check the Letterboxd diary: returned 429"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        await MarkedAny(service, 0);
        Assert.Equal(SyncStatus.Failed, Assert.Single(SyncHistory.GetRecent(10, "lachlan")).Status);
    }

    [Fact]
    public async Task RealTime_NotFoundFilm_IsRecordedAsPermanent()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        var service = Service();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .ThrowsAsync(new FilmNotFoundException(FilmTmdb, "Film with TMDb ID 1233413 not found on Letterboxd"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        var row = Assert.Single(SyncHistory.GetRecent(10, "lachlan"));
        Assert.True(row.PermanentFailure);
        Assert.Equal("lb-user", row.Account);
    }

    // ----- Real-time backstop and per-film lock -----

    [Fact]
    public async Task RealTime_LocalHistoryBackstop_SuppressesSecondSameDayEntry()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        // The diary has not caught up with the entry made earlier today.
        var service = Service(new DiaryInfo(null, false));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        SyncHistory.Record(new SyncEvent
        {
            FilmTitle = "Sinners",
            TmdbId = FilmTmdb,
            Username = "lachlan",
            Account = "lb-user",
            Timestamp = DateTime.UtcNow,
            ViewingDate = Helpers.ToLocalViewingDate(DateTime.UtcNow),
            Status = SyncStatus.Success
        });

        await _handler.HandlePlaybackStoppedAsync(new PlaybackStopEventArgs
        {
            Item = movie,
            PlayedToCompletion = true,
            Users = new List<User> { user }
        });

        await MarkedAny(service, 0);
        var skip = SyncHistory.GetRecent(10, "lachlan").First();
        Assert.Equal(SyncStatus.Skipped, skip.Status);
        Assert.StartsWith(SyncHistory.BackstopErrorPrefix, skip.Error);
    }

    [Fact]
    public async Task RealTime_TwoOverlappingFinishes_PostOnce()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        var service = Service(new DiaryInfo(null, false));
        // A slow lookup so the two finishes overlap.
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>()).Returns(async _ =>
        {
            await Task.Delay(200);
            return new FilmResult("sinners-2025", "KQMM", null);
        });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        var args = new PlaybackStopEventArgs { Item = movie, PlayedToCompletion = true, Users = new List<User> { user } };

        await Task.WhenAll(_handler.HandlePlaybackStoppedAsync(args), _handler.HandlePlaybackStoppedAsync(args));

        await MarkedAny(service, 1);
        Assert.Equal(0, FilmSyncLock.ActiveCount);
    }

    // ----- Cancellation records nothing -----

    [Fact]
    public async Task CancelledRun_RecordsNoFailure()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        Library(user, (movie, DateTime.UtcNow.AddHours(-1)));
        using var cts = new CancellationTokenSource();
        var service = Service();
        // Matched on the run's own token: the runner must hand it to the lookup.
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>(), cts.Token).Returns(_ =>
        {
            cts.Cancel(); // shutdown arrives while the film is in flight
            return new FilmResult("sinners-2025", "KQMM", null);
        });
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(user, cts.Token));

        Assert.DoesNotContain(SyncHistory.GetRecent(10, "lachlan"), e => e.Status == SyncStatus.Failed);
    }

    // ----- Scheduled sync marks rewatches -----

    [Fact]
    public async Task ScheduledSync_MarksRewatchFromDiaryLastDate()
    {
        var user = MakeUser();
        AddAccount(user, "lb-user");
        var movie = MakeMovie();
        var played = DateTime.UtcNow.AddHours(-1);
        Library(user, (movie, played));
        var service = Service(new DiaryInfo(Helpers.ToLocalViewingDate(played).AddDays(-30), true));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await RunAsync(user);

        await service.Received(1).MarkAsWatchedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime?>(),
            Arg.Any<bool>(), Arg.Any<string?>(), true, Arg.Any<double?>());
        Assert.Equal(SyncStatus.Rewatch, Assert.Single(SyncHistory.GetRecent(10, "lachlan")).Status);
    }
}
