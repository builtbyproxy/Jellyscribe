using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// TMDb → film lookups are cached per implementation type, so a film is resolved (and the
/// scraper's throttle paid) once per process rather than once per run per linked account.
/// Each test uses its own TMDb id because the caches are process-wide.
/// </summary>
[Collection("Plugin")]
public class FilmLookupCacheTests : IDisposable
{
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger("test");

    private static readonly Func<CancellationToken, Task> DefaultPause = LetterboxdSyncRunner.FilmPause;

    private readonly string _tempDir;

    public FilmLookupCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-filmcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "sync-history.jsonl");
        SyncHistory.ResetForTesting();
        LetterboxdScraper.ResetFilmCacheForTesting(9_000_001, 9_000_003);
        LetterboxdApiClient.ResetFilmCacheForTesting(9_000_002, 9_000_003);
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.OverrideForTesting = null;
        LetterboxdSyncRunner.FilmPause = DefaultPause;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static ScraperTests.ScraperMockHandler ScraperHandler(int tmdbId, Action onLookup)
        => new((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.StartsWith($"/tmdb/{tmdbId}"))
            {
                onLookup();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/arrival/\" /></head></html>")
                };
            }

            if (path == "/film/arrival/")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<div data-film-slug=\"arrival\" data-film-id=\"290327\"></div>")
                };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static ApiMockHandler ApiHandler(Action onLookup)
        => ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") != true) return null;
            onLookup();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    items = new[] { new { id = "fyqi", name = "Arrival", link = "https://letterboxd.com/film/arrival/" } }
                }))
            };
        });

    [Fact]
    public async Task Scraper_SecondLookupOfAFilm_IsServedFromCacheWithoutThrottling()
    {
        const int tmdbId = 9_000_001;
        var lookups = 0;
        var (http1, first) = ScraperHandler(tmdbId, () => lookups++).CreateClients(Log);
        var (http2, second) = ScraperHandler(tmdbId, () => lookups++).CreateClients(Log);
        using var _ = http1;
        using var __ = http2;

        var resolved = await first.LookupFilmByTmdbIdAsync(tmdbId);
        var timer = Stopwatch.StartNew();
        var cached = await second.LookupFilmByTmdbIdAsync(tmdbId);

        Assert.Equal(1, lookups);
        Assert.Equal(resolved, cached);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"cache hit took {timer.Elapsed}");
    }

    [Fact]
    public async Task ApiClient_SecondLookupOfAFilm_IsServedFromCache()
    {
        const int tmdbId = 9_000_002;
        var lookups = 0;
        using var first = new LetterboxdApiClient(Log, ApiHandler(() => lookups++));
        using var second = new LetterboxdApiClient(Log, ApiHandler(() => lookups++));

        var resolved = await first.LookupFilmByTmdbIdAsync(tmdbId);
        var cached = await second.LookupFilmByTmdbIdAsync(tmdbId);

        Assert.Equal(1, lookups);
        Assert.Equal("fyqi", cached.FilmId);
        Assert.Equal(resolved, cached);
    }

    [Fact]
    public async Task Caches_AreNeverSharedBetweenImplementations()
    {
        const int tmdbId = 9_000_003;
        var apiLookups = 0;
        var (http, scraper) = ScraperHandler(tmdbId, () => { }).CreateClients(Log);
        using var _ = http;
        using var api = new LetterboxdApiClient(Log, ApiHandler(() => apiLookups++));

        var scraped = await scraper.LookupFilmByTmdbIdAsync(tmdbId);
        var fromApi = await api.LookupFilmByTmdbIdAsync(tmdbId);

        Assert.Equal("290327", scraped.FilmId);
        Assert.Equal("fyqi", fromApi.FilmId);
        Assert.Equal(1, apiLookups);
    }

    private (LetterboxdSyncRunner Runner, string UserId) RunnerWithOneFilm(ILetterboxdService service)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());
        new Plugin(paths, xml);

        var user = new User("demo-user", "test-provider-id", "test-reset-id");
        var userId = user.Id.ToString("N");
        var userManager = Substitute.For<IUserManager>();
        userManager.GetUsers().Returns(new[] { user });
        Plugin.Instance!.Configuration.Accounts.Add(new Account
        {
            UserJellyfinId = userId,
            LetterboxdUsername = "demo-cinephile",
            LetterboxdPassword = "secret",
            Enabled = true,
            SkipPreviouslySynced = false,
        });

        var movie = new Movie { Name = "Arrival" };
        movie.SetProviderId(MetadataProvider.Tmdb, "329865");
        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { movie });
        var userDataManager = Substitute.For<IUserDataManager>();
        userDataManager.GetUserData(user, movie).Returns(
            new UserItemData { Key = "k", Played = true, LastPlayedDate = DateTime.UtcNow });

        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        return (new LetterboxdSyncRunner(NullLoggerFactory.Instance, libraryManager, userManager, userDataManager), userId);
    }

    // Records the order of the lookup and the pause, so the tests assert sequence, not timing.
    private static ILetterboxdService RecordingService(bool website, List<string> calls)
    {
        var service = Substitute.For<ILetterboxdService>();
        service.IsWebsiteSession.Returns(website);
        service.LookupFilmByTmdbIdAsync(329865).Returns(async _ =>
        {
            lock (calls) calls.Add("lookup-start");
            await Task.Yield();
            lock (calls) calls.Add("lookup-done");
            return new FilmResult("arrival", "290327", null);
        });
        service.GetDiaryInfoAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(_ =>
        {
            lock (calls) calls.Add("diary");
            return new DiaryInfo(null, false);
        });
        LetterboxdSyncRunner.FilmPause = _ =>
        {
            lock (calls) calls.Add("pause");
            return Task.CompletedTask;
        };
        return service;
    }

    [Fact]
    public async Task Runner_OnTheApi_StartsThePauseBeforeTheLookup()
    {
        var calls = new List<string>();
        var service = RecordingService(website: false, calls);
        var (runner, userId) = RunnerWithOneFilm(service);

        Assert.True(await runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None));

        Assert.Equal(new[] { "pause", "lookup-start", "lookup-done", "diary" }, calls);
        await service.Received(1).MarkAsWatchedAsync("arrival", "290327", Arg.Any<DateTime?>(), Arg.Any<bool>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<double?>());
    }

    // On the website the lookup fetches the film page itself; the diary page must not follow it
    // straight away, so the pause comes after the lookup, not alongside it.
    [Fact]
    public async Task Runner_OnTheWebsite_PausesAfterTheLookup()
    {
        var calls = new List<string>();
        var service = RecordingService(website: true, calls);
        var (runner, userId) = RunnerWithOneFilm(service);

        Assert.True(await runner.TryRunForUserAsync(userId, "test", new Progress<double>(), CancellationToken.None));

        Assert.Equal(new[] { "lookup-start", "lookup-done", "pause", "diary" }, calls);
    }
}
