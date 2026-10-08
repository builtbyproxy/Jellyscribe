using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// How the scheduled Letterboxd sync behaves when Letterboxd blocks it, and with films it can
/// never sync. Pacing is switched off so each run takes milliseconds.
/// </summary>
[Collection("Plugin")]
public class SyncReliabilityTests : IDisposable
{
    private static readonly Func<CancellationToken, Task> DefaultPause = LetterboxdSyncRunner.FilmPause;

    private readonly string _tempDir;
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IUserDataManager _userDataManager = Substitute.For<IUserDataManager>();
    private readonly LetterboxdSyncRunner _runner;

    public SyncReliabilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-reliab-" + Guid.NewGuid().ToString("N"));
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
        LetterboxdSyncRunner.FilmPause = _ => Task.CompletedTask;

        _runner = new LetterboxdSyncRunner(NullLoggerFactory.Instance, _libraryManager, _userManager, _userDataManager);
    }

    public void Dispose()
    {
        LetterboxdSyncRunner.FilmPause = DefaultPause;
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static User MakeUser() => new("demo-user", "test-provider-id", "test-reset-id");

    private static Movie MakeMovie(int? tmdbId, string name)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name };
        if (tmdbId is int id)
            movie.SetProviderId(MetadataProvider.Tmdb, id.ToString());
        return movie;
    }

    private void Setup(User user, params Movie[] movies)
    {
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = user.Id.ToString("N"),
            LetterboxdUsername = "demo-cinephile",
            LetterboxdPassword = "secret",
            Enabled = true,
            SkipPreviouslySynced = true,
        });
        _userManager.GetUsers().Returns(new[] { user });
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(movies.Cast<BaseItem>().ToList());
        foreach (var movie in movies)
            _userDataManager.GetUserData(user, movie).Returns(
                new UserItemData { Key = movie.Id.ToString("N"), Played = true, LastPlayedDate = DateTime.UtcNow.AddHours(-1) });
    }

    private static ILetterboxdService Service()
    {
        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .Returns(ci => new FilmResult("film-" + ci.Arg<int>(), "LID" + ci.Arg<int>(), null));
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(new DiaryInfo(null, false));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        return service;
    }

    private Task<bool> RunAsync(User user)
        => _runner.TryRunForUserAsync(user.Id.ToString("N"), "scheduled", new Progress<double>(), CancellationToken.None);

    // ----- In-run block breaker -----

    [Fact]
    public async Task CloudflareBlocksEveryFilm_StopsAfterAFewAndRecordsOnePause()
    {
        var user = MakeUser();
        var movies = Enumerable.Range(1, 8).Select(i => MakeMovie(1000 + i, "Film " + i)).ToArray();
        Setup(user, movies);
        var service = Service();
        service.LookupFilmByTmdbIdAsync(Arg.Any<int>())
            .ThrowsAsync(new LetterboxdBlockedException("TMDb lookup returned 403 after retries. Cloudflare is blocking."));

        await RunAsync(user);

        await service.Received(LetterboxdSyncRunner.MaxConsecutiveBlocks).LookupFilmByTmdbIdAsync(Arg.Any<int>());
        var events = SyncHistory.GetRecent(50, "demo-user");
        Assert.Equal(LetterboxdSyncRunner.MaxConsecutiveBlocks, events.Count(e => e.Status == SyncStatus.Failed));
        var pause = Assert.Single(events, e => e.Status == SyncStatus.Skipped);
        Assert.Contains("paused", pause.FilmTitle, StringComparison.Ordinal);
        Assert.Contains("5 films", pause.Error, StringComparison.Ordinal);
        // Every film tried failed, so the run is an outage and none of it counts against the films.
        Assert.All(events.Where(e => e.Status == SyncStatus.Failed), e => Assert.True(e.Outage));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    // A 429 reaches the runner only when its Retry-After is longer than the client waits.
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task OnlyBlocksTripTheBreaker(HttpStatusCode status, bool stops)
    {
        var user = MakeUser();
        var movies = Enumerable.Range(1, 6).Select(i => MakeMovie(2000 + i, "Film " + i)).ToArray();
        Setup(user, movies);
        var service = Service();
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>())
            .ThrowsAsync(new HttpRequestException("Response status code does not indicate success", null, status));

        await RunAsync(user);

        await service.Received(stops ? LetterboxdSyncRunner.MaxConsecutiveBlocks : movies.Length)
            .GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ABlockBetweenSuccesses_DoesNotStopTheRun()
    {
        var user = MakeUser();
        var movies = Enumerable.Range(1, 7).Select(i => MakeMovie(3000 + i, "Film " + i)).ToArray();
        Setup(user, movies);
        var service = Service();
        var calls = 0;
        // Block, block, answer, block, block, answer, block: never three in a row.
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(_ =>
            ++calls % 3 == 0
                ? Task.FromResult(new DiaryInfo(null, false))
                : Task.FromException<DiaryInfo>(new DiaryCheckFailedException("Cloudflare challenge", blocked: true)));

        await RunAsync(user);

        Assert.Equal(movies.Length, calls);
    }

    // ----- Films with no TMDb id -----

    [Fact]
    public async Task FilmsWithoutATmdbId_AreRecordedOnce_NotOnEveryRun()
    {
        var user = MakeUser();
        // More of them than the per-film compaction cap, across a restart (which compacts).
        var homeMovies = Enumerable.Range(1, SyncHistory.MaxPrunableEventsPerFilm + 3)
            .Select(i => MakeMovie(null, "Home Movie " + i)).ToArray();
        Setup(user, homeMovies.Append(MakeMovie(4001, "Arrival")).ToArray());
        Service();

        await RunAsync(user);
        await RunAsync(user);
        SyncHistory.ResetForTesting();
        await RunAsync(user);

        var skips = SyncHistory.GetRecent(100, "demo-user").Where(e => e.Error == SyncHistory.NoTmdbIdError).ToList();
        Assert.Equal(homeMovies.Length, skips.Count);
        Assert.Equal(homeMovies.Select(m => m.Name).OrderBy(t => t), skips.Select(e => e.FilmTitle).OrderBy(t => t));
    }
}
