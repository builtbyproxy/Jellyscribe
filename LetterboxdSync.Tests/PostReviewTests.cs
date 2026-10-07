using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for LetterboxdDiary.PostReviewAsync, exercising the success paths
/// (production-log-entries endpoint with various review/rewatch/rating combinations)
/// plus a couple of failure paths that don't go through the slow 15-25s 403 backoff.
/// The retry/backoff path is intentionally not tested here because it would block
/// the test runner on real timers.
/// </summary>
public class PostReviewTests
{
    private static readonly Uri BaseUri = new("https://letterboxd.com/");
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    /// <summary>
    /// Minimal film page HTML that gets ExtractFilmIdentifiers to succeed.
    /// The scraper requires both:
    /// (a) a productionId, here delivered via the `x-letterboxd-identifier` response
    ///     header (the alternative is a `data-postered-identifier` JSON blob), and
    /// (b) a `data-film-id` attribute on a div matching `data-film-slug='{slug}'`.
    /// </summary>
    private static HttpResponseMessage FilmPageResponse(string slug = "sinners", string filmId = "filmlid", string productionId = "PROD1")
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><div data-film-slug=\"{slug}\" data-film-id=\"{filmId}\"></div></html>")
        };
        res.Headers.TryAddWithoutValidation("x-letterboxd-identifier", productionId);
        return res;
    }

    private static HttpResponseMessage FilmPageWithoutProductionId(string slug = "sinners", string filmId = "abc123")
    {
        // No x-letterboxd-identifier header and no data-postered-identifier, the scraper
        // falls back to data-film-id-only, leaving productionId null. PostReviewAsync then
        // sends the filmId in the payload instead of productionId.
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><div data-film-slug=\"{slug}\" data-film-id=\"{filmId}\"></div></html>")
        };
    }

    private static HttpResponseMessage CsrfRootResponse(ReviewMockHandler handler)
    {
        // RefreshCsrfAsync expects to read com.xk72.webparts.csrf from cookies after GET /.
        handler.Http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf-token"));
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostReviewAsync_WithReviewText_PostsToProductionEndpoint()
    {
        string? capturedBody = null;

        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (request.Method == HttpMethod.Get && path == "/film/sinners/")
                return FilmPageResponse();
            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v0/production-log-entries"))
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"success\":true}")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        await diary.PostReviewAsync("sinners", "great film", containsSpoilers: false, isRewatch: false);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"text\":\"great film\"", capturedBody!);
        Assert.Contains("\"productionId\":\"PROD1\"", capturedBody);
        // Default rating omitted, like flag false, no spoilers.
        Assert.DoesNotContain("\"rating\":", capturedBody);
        Assert.Contains("\"containsSpoilers\":false", capturedBody);
    }

    [Fact]
    public async Task PostReviewAsync_WithRating_IncludesRatingInPayload()
    {
        string? capturedBody = null;

        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (path == "/film/sinners/") return FilmPageResponse();
            if (request.Method == HttpMethod.Post)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        await diary.PostReviewAsync("sinners", "good", rating: 4.5);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"rating\":4.5", capturedBody!);
    }

    [Fact]
    public async Task PostReviewAsync_RewatchWithoutReviewText_PostsRewatchFlag()
    {
        string? capturedBody = null;

        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (path == "/film/sinners/") return FilmPageResponse();
            if (request.Method == HttpMethod.Post)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        await diary.PostReviewAsync("sinners", reviewText: null, isRewatch: true, date: "2026-04-29");

        Assert.NotNull(capturedBody);
        Assert.Contains("\"rewatch\":true", capturedBody!);
        Assert.Contains("\"diaryDate\":\"2026-04-29\"", capturedBody);
        // No review text means no review block at all.
        Assert.DoesNotContain("\"review\":", capturedBody);
    }

    [Fact]
    public async Task PostReviewAsync_ContainsSpoilers_FlaggedInPayload()
    {
        string? capturedBody = null;

        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (path == "/film/sinners/") return FilmPageResponse();
            if (request.Method == HttpMethod.Post)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        await diary.PostReviewAsync("sinners", "spoiler heavy", containsSpoilers: true);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"containsSpoilers\":true", capturedBody!);
    }

    [Fact]
    public async Task PostReviewAsync_NoProductionId_FallsBackToFilmId()
    {
        string? capturedBody = null;

        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (path == "/film/sinners/") return FilmPageWithoutProductionId();
            if (request.Method == HttpMethod.Post)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        await diary.PostReviewAsync("sinners", "ok", isRewatch: false);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"filmId\":\"abc123\"", capturedBody!);
        Assert.DoesNotContain("\"productionId\":", capturedBody);
    }

    [Fact]
    public async Task PostReviewAsync_400Response_ThrowsWithStatusCode()
    {
        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (path == "/film/sinners/") return FilmPageResponse();
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"validation failed\"}")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        var ex = await Assert.ThrowsAsync<Exception>(() =>
            diary.PostReviewAsync("sinners", "x"));

        Assert.Contains("400", ex.Message);
        Assert.Contains("sinners", ex.Message);
    }

    [Fact]
    public async Task PostReviewAsync_DateOmitted_UsesTodaysDate()
    {
        string? capturedBody = null;

        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (path == "/film/sinners/") return FilmPageResponse();
            if (request.Method == HttpMethod.Post)
            {
                capturedBody = request.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var diary = handler.CreateDiary(TestLogger);
        await diary.PostReviewAsync("sinners", "x", date: null);

        Assert.NotNull(capturedBody);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        Assert.Contains($"\"diaryDate\":\"{today}\"", capturedBody!);
    }

    private static ReviewMockHandler EchoingReviewEndpoint(HttpStatusCode status, string body)
    {
        var handler = new ReviewMockHandler();
        handler.Responder = (request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Get && path == "/")
                return CsrfRootResponse(handler);
            if (request.Method == HttpMethod.Get && path == "/film/sinners/")
                return FilmPageResponse();
            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v0/production-log-entries"))
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        return handler;
    }

    [Fact]
    public async Task PostReviewAsync_Success_LogsStatusAndLength_NeverTheReplyBody()
    {
        // Letterboxd's reply to a new log entry can echo the review back.
        const string review = "a private first draft about the ending";
        var reply = "{\"id\":\"abc\",\"review\":{\"text\":\"" + review + "\"}}";
        var logger = new ListLogger();

        await EchoingReviewEndpoint(HttpStatusCode.OK, reply).CreateDiary(logger).PostReviewAsync("sinners", review, date: null);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("private first draft"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("Posted review for sinners") && e.Message.Contains("status=200")
            && e.Message.Contains($"bodyLen={reply.Length}"));
    }

    [Fact]
    public async Task PostReviewAsync_Failure_LogsAShortStartOfTheReply()
    {
        var reply = "{\"error\":\"invalid rating\"}" + new string('x', 1000);
        var logger = new ListLogger();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            EchoingReviewEndpoint(HttpStatusCode.BadRequest, reply).CreateDiary(logger).PostReviewAsync("sinners", "a fine film", date: null));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("Review post for sinners failed"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("status=400", entry.Message);
        Assert.Contains("invalid rating", entry.Message);
        Assert.DoesNotContain(new string('x', 400), entry.Message);
    }

    [Theory]
    [InlineData("{\"error\":\"invalid rating\"}", "ok", "{\"error\":\"invalid rating\"}")]
    [InlineData("{\"error\":\"quoted: an honest review\"}", "an honest review", "{\"error\":\"quoted: [review]\"}")]
    [InlineData("{\"text\":\"caf\\u00E9 noir\"}", "café noir", "{\"text\":\"[review]\"}")]
    [InlineData("plain café noir", "café noir", "plain [review]")]
    public void WithoutReview_CutsTheReview_ButLeavesAShortOneAlone(string body, string review, string expected)
        => Assert.Equal(expected, LetterboxdDiary.WithoutReview(body, review));

    [Fact]
    public async Task PostReviewAsync_FailureThatQuotesTheReview_KeepsTheReviewOutOfTheLogAndTheError()
    {
        const string review = "my \"secret\" draft\nsecond line";
        var reply = "{\"error\":\"review too long\",\"review\":{\"text\":" + System.Text.Json.JsonSerializer.Serialize(review) + "}}";
        var logger = new ListLogger();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            EchoingReviewEndpoint(HttpStatusCode.BadRequest, reply).CreateDiary(logger).PostReviewAsync("sinners", review, date: null));

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret"));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Contains(logger.Entries, e => e.Message.Contains("review too long") && e.Message.Contains("[review]"));
    }

    /// <summary>
    /// Self-contained mock handler that wires up an HttpClient + LetterboxdHttpClient
    /// + LetterboxdDiary chain. The Responder captures requests and produces responses;
    /// the handler stores its own LetterboxdHttpClient so the responder can mutate
    /// the cookie container (for the CSRF fetch).
    /// </summary>
    internal class ReviewMockHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, LetterboxdHttpClient, HttpResponseMessage>? Responder { get; set; }
        public LetterboxdHttpClient Http { get; private set; } = null!;

        public LetterboxdDiary CreateDiary(ILogger logger)
        {
            Http = new LetterboxdHttpClient(logger, this);
            var auth = new LetterboxdAuth(Http, logger);
            var scraper = new LetterboxdScraper(Http, logger);
            return new LetterboxdDiary(Http, auth, scraper, logger);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Responder == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(Responder(request, Http));
        }
    }
}
