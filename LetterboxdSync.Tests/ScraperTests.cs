using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class ScraperTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    // Lookups are cached process-wide, and two tests here resolve the same id to different films.
    public ScraperTests() => LetterboxdScraper.ResetFilmCacheForTesting(693134, 99999, 12345, 198102);

    [Fact]
    public async Task LookupFilmByTmdbId_ValidFilm_ReturnsFilmResult()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            // TMDb redirect page, returns HTML with canonical link
            if (path.StartsWith("/tmdb/693134"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/dune-part-two/\" /></head></html>")
                };
            }

            // Film page, returns HTML with data-film-id
            if (path == "/film/dune-part-two/")
            {
                var res = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<div data-film-slug=\"dune-part-two\" data-film-id=\"945898\"></div>")
                };
                res.Headers.TryAddWithoutValidation("x-letterboxd-identifier", "PROD-dune");
                return res;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var result = await scraper.LookupFilmByTmdbIdAsync(693134);

        Assert.Equal("dune-part-two", result.Slug);
        Assert.Equal("945898", result.FilmId);
        Assert.Equal("PROD-dune", result.ProductionId);
    }

    // Shapes copied from live letterboxd.com pages on 2026-10-08. A film (The Godfather) carries its
    // TMDb movie id on the body and a TMDb button to /movie/. A TV entry Letterboxd lists as a film
    // (Chernobyl) still says data-tmdb-type="movie", has an empty data-tmdb-id, and only its TMDb
    // button links to /tv/.
    private const string MoviePage =
        "<html><body class=\"film backdropped\" data-tmdb-id=\"238\" data-tmdb-type=\"movie\">" +
        "<a href=\"https://www.themoviedb.org/movie/238/\" class=\"micro-button track-event\" data-track-action=\"TMDB\" target=\"_blank\">TMDB</a></body></html>";
    private const string TvPage =
        "<html><body class=\"film backdropped\" data-tmdb-id=\"\" data-tmdb-type=\"movie\">" +
        "<a href=\"https://www.themoviedb.org/tv/87108/\" class=\"micro-button track-event\" data-track-action=\"TMDB\" target=\"_blank\">TMDB</a></body></html>";

    [Fact]
    public void ReadTmdbEntry_MoviePage_ReturnsTheMovieId()
        => Assert.Equal((238, false), LetterboxdScraper.ReadTmdbEntry(MoviePage));

    [Fact]
    public void ReadTmdbEntry_TvPage_IsNotAMovie()
        => Assert.Equal((null, true), LetterboxdScraper.ReadTmdbEntry(TvPage));

    [Fact]
    public void ReadTmdbEntry_TvButtonWinsOverAMovieTypeAndAnId()
        => Assert.Equal((null, true), LetterboxdScraper.ReadTmdbEntry(
            "<html><body data-tmdb-id=\"198102\" data-tmdb-type=\"movie\"><a href=\"https://www.themoviedb.org/tv/198102/\" data-track-action=\"TMDB\">TMDB</a></body></html>"));

    [Fact]
    public void ReadTmdbEntry_WithoutTheTypeAttribute_FallsBackToTheTmdbLink()
    {
        Assert.Equal((null, true), LetterboxdScraper.ReadTmdbEntry(
            "<html><body data-tmdb-id=\"198102\"><a href=\"https://www.themoviedb.org/tv/198102/\" data-track-action=\"TMDB\">TMDB</a></body></html>"));
        Assert.Equal((198102, false), LetterboxdScraper.ReadTmdbEntry(
            "<html><body data-tmdb-id=\"198102\"><a href=\"https://www.themoviedb.org/movie/198102/\" data-track-action=\"TMDB\">TMDB</a></body></html>"));
    }

    [Fact]
    public void ReadTmdbEntry_ATvLinkInAReview_DoesNotMakeTheFilmTv()
        => Assert.Equal((198102, false), LetterboxdScraper.ReadTmdbEntry(
            "<html><body data-tmdb-id=\"198102\">" +
            "<div class=\"review\"><a href=\"https://www.themoviedb.org/tv/1399/\">a show I liked</a></div>" +
            "<a href=\"https://www.themoviedb.org/movie/198102/\" data-track-action=\"TMDB\">TMDB</a></body></html>"));

    [Theory]
    [InlineData("film")]
    [InlineData("Movie ")]
    public void ReadTmdbEntry_AnUnfamiliarType_StillCountsAsAFilm(string type)
        => Assert.Equal((198102, false), LetterboxdScraper.ReadTmdbEntry($"<html><body data-tmdb-id=\"198102\" data-tmdb-type=\"{type}\"></body></html>"));

    [Fact]
    public void ReadTmdbEntry_OlderMarkupWithNeither_StillCountsAsAFilm()
        => Assert.Equal((550, false), LetterboxdScraper.ReadTmdbEntry("<html><body data-tmdb-id=\"550\"></body></html>"));

    [Theory]
    [InlineData("<html><body></body></html>")]
    [InlineData("<html><body data-tmdb-id=\"\"></body></html>")]
    [InlineData("<html><body data-tmdb-id=\"abc\"></body></html>")]
    public void ReadTmdbEntry_NoUsableId_ReturnsNull(string html)
        => Assert.Equal((null, false), LetterboxdScraper.ReadTmdbEntry(html));

    [Fact]
    public async Task LookupFilmByTmdbId_ResolvingToATvEntry_IsNotFound()
    {
        var filmPageRead = false;
        var handler = new ScraperMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.StartsWith("/tmdb/198102"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/hijack-2023/\" /></head></html>")
                };
            if (path == "/film/hijack-2023/")
            {
                filmPageRead = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TvPage.Replace("<a ", "<div data-film-slug=\"hijack-2023\" data-film-id=\"1\"></div><a "))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        await Assert.ThrowsAsync<FilmNotFoundException>(() => scraper.LookupFilmByTmdbIdAsync(198102));
        Assert.True(filmPageRead);
    }

    [Fact]
    public async Task CloudflareBackoff_StopsWhenTheSyncIsCancelled()
    {
        var requests = 0;
        using var cts = new CancellationTokenSource();
        var handler = new ScraperMockHandler((_, _) =>
        {
            requests++;
            // The sync is stopped during the backoff that follows this 403 (cancelling here would
            // land on the send, which also takes the token).
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });

        var (http, _) = handler.CreateClients(TestLogger);
        using var __ = http;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // A 403 backs off 15 s or more before the next attempt; an uncancellable backoff would
        // sit that out before the next send noticed the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.GetWithCloudflareRetryAsync("/tmdb/1", cancellationToken: cts.Token));

        Assert.Equal(1, requests);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task LookupFilmByTmdbId_NotFound_Throws()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        await Assert.ThrowsAsync<FilmNotFoundException>(() => scraper.LookupFilmByTmdbIdAsync(99999));
    }

    [Fact]
    public async Task LookupFilmByTmdbId_403ThenSuccess_RetriesWithBackoff()
    {
        int tmdbCallCount = 0;

        var handler = new ScraperMockHandler((request, http) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";

            if (path.StartsWith("/tmdb/"))
            {
                tmdbCallCount++;
                if (tmdbCallCount == 1)
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/test-film/\" /></head></html>")
                };
            }

            if (path == "/film/test-film/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<div data-film-slug=\"test-film\" data-film-id=\"111\"></div>")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var result = await scraper.LookupFilmByTmdbIdAsync(12345);

        Assert.Equal("test-film", result.Slug);
        Assert.True(tmdbCallCount >= 2, $"Expected retry, got {tmdbCallCount} calls");
    }

    [Fact]
    public void ExtractFilmIdentifiers_HeaderAndHtml_ExtractsBoth()
    {
        var html = "<div data-film-slug=\"gladiator-ii\" data-film-id=\"54321\"></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-letterboxd-identifier", "PROD-glad");

        var (filmId, productionId) = scraper.ExtractFilmIdentifiers(html, "gladiator-ii", response.Headers);

        Assert.Equal("54321", filmId);
        Assert.Equal("PROD-glad", productionId);
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_NoHeader_FallsBackToPosteredIdentifier()
    {
        var html = @"<div data-postered-identifier='{""lid"":""PROD-fallback""}' />
                     <div data-film-slug=""test"" data-film-id=""999""></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var (filmId, productionId) = scraper.ExtractFilmIdentifiers(html, "test");

        Assert.Equal("999", filmId);
        Assert.Equal("PROD-fallback", productionId);
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_NoFilmElement_Throws()
    {
        var html = "<html><body><p>No film here</p></body></html>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        Assert.Throws<Exception>(() => scraper.ExtractFilmIdentifiers(html, "missing-film"));
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_MalformedPosteredJson_IgnoredAndFilmIdStillExtracted()
    {
        // The data-postered-identifier JSON is garbage; the parse must be swallowed and
        // the film-id still read from the element, leaving productionId null.
        var html = @"<div data-postered-identifier='{not valid json' />
                     <div data-film-slug=""test"" data-film-id=""777""></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var (filmId, productionId) = scraper.ExtractFilmIdentifiers(html, "test");

        Assert.Equal("777", filmId);
        Assert.Null(productionId);
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_NoIdentifiersAtAll_Throws()
    {
        // The element is found but carries neither a usable filmId nor a productionId
        // → explicit failure rather than silently returning empty ids that would
        // corrupt later API calls.
        var html = "<div data-film-slug=\"test\" data-film-id=\"\"></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var ex = Assert.Throws<Exception>(() => scraper.ExtractFilmIdentifiers(html, "test"));
        Assert.Contains("identifiers", ex.Message);
        http.Dispose();
    }

    [Fact]
    public void ExtractFilmIdentifiers_NewMarkup_ExtractsBothFromPosteredIdentifier()
    {
        // Mid-2026 Letterboxd markup: no data-film-slug / data-film-id; ids live in the
        // entity-encoded data-postered-identifier JSON on the data-item-link element.
        var html = "<div class=\"react-component\" data-item-slug=\"spider-man\" " +
                   "data-item-link=\"/film/spider-man/\" " +
                   "data-postered-identifier='{&quot;lid&quot;:&quot;2a8i&quot;,&quot;uid&quot;:&quot;film:51561&quot;,&quot;type&quot;:&quot;film&quot;}'></div>";
        var http = new LetterboxdHttpClient(TestLogger);
        var scraper = new LetterboxdScraper(http, TestLogger);

        var (filmId, productionId) = scraper.ExtractFilmIdentifiers(html, "spider-man");

        Assert.Equal("51561", filmId);
        Assert.Equal("2a8i", productionId);
        http.Dispose();
    }

    [Fact]
    public async Task GetDiaryInfo_NoDiaryEntries_ReturnsEmpty()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>No diary entries</body></html>")
            };
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var info = await scraper.GetDiaryInfoAsync("test-film", "testuser");

        Assert.Null(info.LastDate);
        Assert.False(info.HasAnyEntry);
    }

    [Fact]
    public async Task GetDiaryInfo_WithEntries_ReturnsMostRecent()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"
                    <a class=""month"">Mar</a>
                    <a class=""month"">Jan</a>
                    <a class=""date"">25</a>
                    <a class=""date"">10</a>
                    <a class=""year"">2026</a>
                    <a class=""year"">2026</a>")
            };
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var info = await scraper.GetDiaryInfoAsync("test-film", "testuser");

        Assert.True(info.HasAnyEntry);
        Assert.NotNull(info.LastDate);
    }

    [Fact]
    public async Task GetDiaryInfo_404_ReturnsEmpty()
    {
        var handler = new ScraperMockHandler((request, http) =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        var info = await scraper.GetDiaryInfoAsync("nonexistent", "testuser");

        Assert.Null(info.LastDate);
        Assert.False(info.HasAnyEntry);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetDiaryInfo_ErrorStatus_ThrowsInsteadOfReportingNoEntries(HttpStatusCode status)
    {
        var handler = new ScraperMockHandler((_, _) => new HttpResponseMessage(status));
        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        await Assert.ThrowsAsync<DiaryCheckFailedException>(() => scraper.GetDiaryInfoAsync("test-film", "testuser"));
    }

    [Fact]
    public async Task GetDiaryInfo_CloudflareChallenge_Throws()
    {
        var handler = new ScraperMockHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><title>Just a moment...</title></html>")
        });
        var (http, scraper) = handler.CreateClients(TestLogger);
        using var _ = http;

        await Assert.ThrowsAsync<DiaryCheckFailedException>(() => scraper.GetDiaryInfoAsync("test-film", "testuser"));
    }

    [Theory]
    [InlineData("<html><head><title>Just a moment...</title></head></html>", true)]
    [InlineData("<html><head><title>Attention Required! | Cloudflare</title></head></html>", true)]
    [InlineData("<html><head><title>Diary</title></head><body><p>Just a moment of silence, attention required.</p></body></html>", false)]
    [InlineData("<html><head><title>Diary</title></head><body><script src=\"/cdn-cgi/challenge-platform/scripts/jsd/main.js\"></script></body></html>", false)]
    public void IsCloudflareChallenge_LooksAtTheChallengeMarkersNotPageText(string html, bool expected)
    {
        Assert.Equal(expected, LetterboxdScraper.IsCloudflareChallenge(html));
    }

    [Fact]
    public async Task ScrapingService_DiaryCheck_UsesTheSlugForTheFilmIdItReturned()
    {
        var diaryPaths = new List<string>();
        var handler = new ScraperMockHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.StartsWith("/tmdb/693134"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><head><link rel=\"canonical\" href=\"https://letterboxd.com/film/dune-part-two/\" /></head></html>")
                };
            if (path == "/film/dune-part-two/")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<div data-film-slug=\"dune-part-two\" data-film-id=\"945898\"></div>")
                };
            if (path.EndsWith("/diary/"))
            {
                lock (diaryPaths) diaryPaths.Add(path);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new ScrapingLetterboxdService(TestLogger, handler);

        var film = await service.LookupFilmByTmdbIdAsync(693134);
        await service.GetDiaryInfoAsync(film.FilmId, "testuser");

        // Callers pass back FilmId (the numeric id here); the diary page lives at the slug.
        Assert.Equal("945898", film.FilmId);
        Assert.Equal(new[] { "/testuser/film/dune-part-two/diary/" }, diaryPaths);
    }

    [Fact]
    public async Task ScrapingService_DiaryCheck_RefusesANumericIdItNeverLookedUp()
    {
        var handler = new ScraperMockHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var service = new ScrapingLetterboxdService(TestLogger, handler);

        await Assert.ThrowsAsync<DiaryCheckFailedException>(() => service.GetDiaryInfoAsync("945898", "testuser"));
    }

    internal class ScraperMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> _responder;
        private LetterboxdHttpClient? _httpClient;

        public ScraperMockHandler(Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage> responder)
            => _responder = responder;

        public (LetterboxdHttpClient Http, LetterboxdScraper Scraper) CreateClients(ILogger logger)
        {
            var http = new LetterboxdHttpClient(logger, this);
            _httpClient = http;
            var scraper = new LetterboxdScraper(http, logger);
            return (http, scraper);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, _httpClient!));
    }
}
