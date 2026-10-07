using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdApiClientTests
{
    private static readonly Microsoft.Extensions.Logging.ILogger Log =
        NullLoggerFactory.Instance.CreateLogger("test");

    private const string ShowJson =
        "{\"seasons\":[{\"id\":3577,\"seasonNumber\":0},{\"id\":3572,\"seasonNumber\":1,\"episodeCount\":7},{\"id\":3573,\"seasonNumber\":2}]}";

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body) };

    private static string ReadBody(HttpRequestMessage req)
        => req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

    public SerializdApiClientTests() => SerializdApiClient.ResetCachesForTesting();

    [Fact]
    public async Task Authenticate_LogsInOnce_ThenReusesCachedToken()
    {
        int logins = 0;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
            {
                logins++;
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"tok123\"}");
            }
            return Json(HttpStatusCode.OK, "{}");
        });

        using var c1 = new SerializdApiClient(Log, handler);
        await c1.AuthenticateAsync("me@example.com", "pw");

        using var c2 = new SerializdApiClient(Log, handler);
        await c2.AuthenticateAsync("me@example.com", "pw");

        Assert.Equal(1, logins); // second client reused the cached token
    }

    [Fact]
    public async Task Authenticate_NamesTheAccountByTag_NeverByEmail()
    {
        var handler = new ApiMockHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/login")
            ? Json(HttpStatusCode.OK, "{\"username\":\"demo-bingewatcher\",\"token\":\"tok\"}")
            : Json(HttpStatusCode.OK, "{}"));
        var logger = new ListLogger();

        using (var first = new SerializdApiClient(logger, handler)) await first.AuthenticateAsync("demo@example.com", "pw");
        using (var cached = new SerializdApiClient(logger, handler)) await cached.AuthenticateAsync("demo@example.com", "pw");

        var tag = LogRedaction.AccountTag("demo@example.com");
        Assert.Contains(logger.Entries, e => e.Message == "Authenticated with Serializd as " + tag);
        Assert.Contains(logger.Entries, e => e.Message == "Reusing cached Serializd token for " + tag);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("demo@example.com"));
    }

    [Fact]
    public async Task Authenticate_SameEmailWrongPassword_DoesNotReuseCachedToken()
    {
        var logins = new List<string>();
        var handler = new ApiMockHandler(req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{}");
            var body = ReadBody(req);
            logins.Add(body);
            return body.Contains("\"right\"")
                ? Json(HttpStatusCode.OK, "{\"username\":\"victim\",\"token\":\"victim-token\"}")
                : Json(HttpStatusCode.Unauthorized, "{\"message\":\"Incorrect password.\"}");
        });

        using var owner = new SerializdApiClient(Log, handler);
        await owner.AuthenticateAsync("isolation@example.com", "right");

        using var attacker = new SerializdApiClient(Log, handler);
        var rejected = await Assert.ThrowsAnyAsync<Exception>(() => attacker.AuthenticateAsync("isolation@example.com", "wrong"));
        Assert.Contains("(401)", rejected.Message);

        using var ownerAgain = new SerializdApiClient(Log, handler);
        await ownerAgain.AuthenticateAsync("isolation@example.com", "right");

        Assert.Equal(2, logins.Count); // the wrong password hit /login; the right one reused the cache
        Assert.Contains("\"wrong\"", logins[1]);
    }

    [Fact]
    public async Task Authenticate_BadCredentials_Throws()
    {
        var handler = new ApiMockHandler(_ =>
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Incorrect password.\"}"));

        using var client = new SerializdApiClient(Log, handler);
        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() => client.AuthenticateAsync("me@example.com", "wrong"));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task ResolveSeasonId_KnownSeason_ReturnsId_UnknownSeason_ReturnsNull()
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            if (req.RequestUri.AbsolutePath.Contains("/show/1396"))
                return Json(HttpStatusCode.OK, ShowJson);
            return Json(HttpStatusCode.NotFound, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        Assert.Equal(3572, await client.ResolveSeasonIdAsync(1396, 1));
        Assert.Equal(3577, await client.ResolveSeasonIdAsync(1396, 0)); // specials
        Assert.Null(await client.ResolveSeasonIdAsync(1396, 99));       // no such season
    }

    [Fact]
    public async Task GetSeasonEpisodeCount_ReadsTheShowOnce_NullWithoutACount()
    {
        var showFetches = 0;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            if (req.RequestUri.AbsolutePath.Contains("/show/1396"))
            {
                showFetches++;
                return Json(HttpStatusCode.OK, ShowJson);
            }
            return Json(HttpStatusCode.NotFound, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        Assert.Equal(3572, await client.ResolveSeasonIdAsync(1396, 1));
        Assert.Equal(7, await client.GetSeasonEpisodeCountAsync(1396, 1));
        Assert.Null(await client.GetSeasonEpisodeCountAsync(1396, 2));  // no episodeCount
        Assert.Null(await client.GetSeasonEpisodeCountAsync(1396, 99)); // no such season
        Assert.Equal(1, showFetches);
    }

    [Fact]
    public async Task LogEpisodes_SendsSnakeCaseBodyToAddEndpoint()
    {
        string? capturedPath = null;
        string capturedBody = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            capturedPath = req.RequestUri.AbsolutePath;
            capturedBody = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"message\":\"Successfully added episode\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.LogEpisodesAsync(1396, 3572, new[] { 1, 2 });

        Assert.EndsWith("/episode_log/add", capturedPath);
        // snake_case is load-bearing (camelCase 500s), assert exact keys present and camelCase absent.
        Assert.Contains("\"episode_numbers\"", capturedBody);
        Assert.Contains("\"season_id\"", capturedBody);
        Assert.Contains("\"show_id\"", capturedBody);
        Assert.DoesNotContain("episodeNumbers", capturedBody);
        Assert.DoesNotContain("seasonId", capturedBody);
        Assert.DoesNotContain("showId", capturedBody);
        Assert.Contains("[1,2]", capturedBody);
    }

    [Fact]
    public async Task UnlogEpisodes_PostsToRemoveEndpoint()
    {
        string? capturedPath = null;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            capturedPath = req.RequestUri.AbsolutePath;
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.UnlogEpisodesAsync(1396, 3572, new[] { 1 });

        Assert.EndsWith("/episode_log/remove", capturedPath);
    }

    [Fact]
    public async Task CreateEpisodeLog_PostsDatedLogWithRating()
    {
        string? path = null;
        string body = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            path = req.RequestUri.AbsolutePath;
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":123}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.CreateEpisodeLogAsync(1396, 3572, 4,
            new DateTime(2026, 6, 19, 20, 0, 0, DateTimeKind.Utc), rating: 7, isRewatch: false);

        Assert.EndsWith("/show/reviews/add", path);
        Assert.Contains("\"show_id\":1396", body);
        Assert.Contains("\"season_id\":3572", body);
        Assert.Contains("\"episode_number\":4", body);
        Assert.Contains("\"is_log\":true", body);
        Assert.Contains("\"backdate\":\"2026-06-19T20:00:00Z\"", body);
        Assert.Contains("\"rating\":7", body);
        Assert.DoesNotContain("showId", body); // snake_case
    }

    [Fact]
    public async Task CreateEpisodeLog_UnratedSendsRatingZero_NotOmitted()
    {
        // Omitting rating 500s on the real API, so an unrated log must send rating:0.
        string body = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":123}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.CreateEpisodeLogAsync(1396, 3572, 4, DateTime.UtcNow, rating: null, isRewatch: false);

        Assert.Contains("\"rating\":0", body);
    }

    [Fact]
    public async Task SetShowMeta_PostsShowLevelRatingAndLike_NotADiaryLog()
    {
        string? path = null;
        string body = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            path = req.RequestUri.AbsolutePath;
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":9}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.SetShowMetaAsync(77236, rating: 9, like: true);

        Assert.EndsWith("/show/reviews/add", path);
        Assert.Contains("\"show_id\":77236", body);
        Assert.Contains("\"season_id\":null", body);
        Assert.Contains("\"episode_number\":null", body);
        Assert.Contains("\"is_log\":false", body); // rating/like, not a Diary row
        Assert.Contains("\"like\":true", body);
        Assert.Contains("\"rating\":9", body);
    }

    [Fact]
    public async Task GetWatchlist_ParsesShowsAndResolvesWatchlistedSeasonNumbers()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/watchlistpage_v2/1"))
                // The Bear (136315): seasonIds 515011 → season 5. Whole-show item (206828): no seasonIds.
                return Json(HttpStatusCode.OK,
                    "{\"totalPages\":1,\"items\":[" +
                    "{\"showId\":136315,\"seasonIds\":[515011]}," +
                    "{\"showId\":206828,\"seasonIds\":[]}]}");
            if (path.Contains("/show/136315"))
                return Json(HttpStatusCode.OK, "{\"seasons\":[{\"id\":392941,\"seasonNumber\":3},{\"id\":515011,\"seasonNumber\":5}]}");
            if (path.Contains("/show/206828"))
                return Json(HttpStatusCode.OK, "{\"seasons\":[{\"id\":302382,\"seasonNumber\":1}]}");
            return Json(HttpStatusCode.OK, "{\"items\":[]}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var entries = await client.GetWatchlistAsync();

        Assert.Equal(2, entries.Count);
        var bear = entries.Single(e => e.ShowTmdbId == 136315);
        Assert.Equal(new[] { 5 }, bear.SeasonNumbers);              // season 515011 → 5
        var whole = entries.Single(e => e.ShowTmdbId == 206828);
        Assert.Empty(whole.SeasonNumbers);                          // no seasonIds → whole show
    }

    [Theory]
    [InlineData("error")]
    [InlineData("empty")]
    [InlineData("noitems")]
    public async Task GetWatchlist_LaterPageCannotBeRead_Throws(string failure)
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/watchlistpage_v2/1"))
                return Json(HttpStatusCode.OK, "{\"totalPages\":2,\"items\":[{\"showId\":206828,\"seasonIds\":[]}]}");
            if (path.Contains("/watchlistpage_v2/2"))
                return failure switch
                {
                    "error" => Json(HttpStatusCode.BadGateway, "{}"),
                    "empty" => Json(HttpStatusCode.OK, "{\"totalPages\":2,\"items\":[]}"),
                    _ => Json(HttpStatusCode.OK, "{\"totalPages\":2}"),
                };
            return Json(HttpStatusCode.OK, "{\"seasons\":[]}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        // A short list would make the watchlist sync remove every show on page two.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWatchlistAsync());
    }

    [Theory]
    [InlineData("{\"totalPages\":0}")]
    [InlineData("{}")]
    public async Task GetWatchlist_EmptyFirstPageWithoutItems_ReturnsEmpty(string body)
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            return Json(HttpStatusCode.OK, body);
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        Assert.Empty(await client.GetWatchlistAsync());
    }

    [Fact]
    public async Task GetWatchlist_MultiplePages_StopsAtTotalPages()
    {
        var pagesRead = new List<string>();
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/watchlistpage_v2/"))
            {
                lock (pagesRead) pagesRead.Add(path);
                var show = path.EndsWith("/1") ? 206828 : 136315;
                return Json(HttpStatusCode.OK, $"{{\"totalPages\":2,\"items\":[{{\"showId\":{show},\"seasonIds\":[]}}]}}");
            }
            return Json(HttpStatusCode.OK, "{\"seasons\":[]}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var entries = await client.GetWatchlistAsync();

        Assert.Equal(new[] { 206828, 136315 }, entries.Select(e => e.ShowTmdbId).ToArray());
        Assert.Equal(2, pagesRead.Count);
    }

    [Fact]
    public async Task GetWatchlist_EmptyWatchlist_ReturnsEmpty()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            return Json(HttpStatusCode.OK, "{\"totalPages\":0,\"items\":[]}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        Assert.Empty(await client.GetWatchlistAsync());
    }

    [Fact]
    public async Task ExpiredToken_ReAuthenticatesAndRetries()
    {
        int logins = 0, addAttempts = 0;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
            {
                logins++;
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t" + logins + "\"}");
            }
            if (req.RequestUri.AbsolutePath.EndsWith("/episode_log/add"))
            {
                addAttempts++;
                // First attempt: token rejected. Second attempt (after re-login): OK.
                return addAttempts == 1
                    ? Json(HttpStatusCode.Unauthorized, "{\"message\":\"invalid token\"}")
                    : Json(HttpStatusCode.OK, "{\"message\":\"ok\"}");
            }
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.LogEpisodesAsync(1396, 3572, new[] { 1 }); // should not throw

        Assert.Equal(2, logins);      // initial + re-auth after 401
        Assert.Equal(2, addAttempts); // failed once, retried once
    }

    // ----- Failure paths -----

    [Fact]
    public async Task Login_ResponseMissingToken_Throws()
    {
        var handler = new ApiMockHandler(_ => Json(HttpStatusCode.OK, "{\"username\":\"u\"}"));

        using var client = new SerializdApiClient(Log, handler);
        var ex = await Assert.ThrowsAsync<Exception>(() => client.AuthenticateAsync("me@example.com", "pw"));
        Assert.Contains("no token", ex.Message);
    }

    [Fact]
    public async Task VerifyLogin_GoodCredentials_ReturnsUsername_BypassesTokenCache()
    {
        int logins = 0;
        var handler = new ApiMockHandler(req =>
        {
            logins++;
            return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"tok\"}");
        });

        using var c1 = new SerializdApiClient(Log, handler);
        await c1.AuthenticateAsync("verify@example.com", "pw"); // caches a token

        using var c2 = new SerializdApiClient(Log, handler);
        var username = await c2.VerifyLoginAsync("verify@example.com", "pw");

        Assert.Equal("8bitproxy", username);
        Assert.Equal(2, logins); // Verify always hits /login, ignoring the cached token from c1
    }

    [Fact]
    public async Task ResolveSeasonId_NonSuccessStatus_Throws()
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            return Json(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        // 500s exhaust the built-in retry (fast: SerializdApiConstants backoff is short in tests
        // only in that it's bounded, not mocked away), then the failure surfaces as an exception.
        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() => client.ResolveSeasonIdAsync(1396, 1));
        Assert.Contains("get-show", ex.Message);
    }

    [Fact]
    public async Task CreateEpisodeLog_NonSuccessStatus_Throws()
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            return Json(HttpStatusCode.BadRequest, "{\"message\":\"nope\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() =>
            client.CreateEpisodeLogAsync(1396, 3572, 1, DateTime.UtcNow, rating: null, isRewatch: false));
        Assert.Contains("/show/reviews/add", ex.Message);
    }

    [Fact]
    public async Task LogEpisodes_NonSuccessStatus_Throws()
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            return Json(HttpStatusCode.BadRequest, "{\"message\":\"nope\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() => client.LogEpisodesAsync(1396, 3572, new[] { 1 }));
        Assert.Contains("/episode_log/add", ex.Message);
    }

    [Fact]
    public async Task SetShowMeta_NonSuccessStatus_Throws()
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            return Json(HttpStatusCode.BadRequest, "{\"message\":\"nope\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() => client.SetShowMetaAsync(1396, rating: 5, like: false));
        Assert.Contains("show-meta", ex.Message);
    }

    // ----- Reviews -----

    [Fact]
    public async Task CreateShowReview_WithText_IsLogTrue()
    {
        string body = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":1,\"reviewText\":\"\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.CreateShowReviewAsync(1396, rating: 8, reviewText: "great show", containsSpoiler: true);

        Assert.Contains("\"is_log\":true", body);
        Assert.Contains("\"review_text\":\"great show\"", body);
        Assert.Contains("\"contains_spoiler\":true", body);
        Assert.Contains("\"season_id\":null", body);
    }

    [Fact]
    public async Task CreateShowReview_RatingOnly_IsLogFalse()
    {
        string body = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":1}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.CreateShowReviewAsync(1396, rating: 6, reviewText: null, containsSpoiler: false);

        Assert.Contains("\"is_log\":false", body);
        Assert.Contains("\"rating\":6", body);
    }

    [Fact]
    public async Task CreateShowReview_NonSuccessStatus_Throws()
    {
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            return Json(HttpStatusCode.BadRequest, "{\"message\":\"nope\"}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() =>
            client.CreateShowReviewAsync(1396, rating: 5, reviewText: null, containsSpoiler: false));
        Assert.Contains("Serializd review", ex.Message);
    }

    [Fact]
    public async Task CreateEpisodeReview_PostsTheResolvedSeasonId_WithoutReadingTheShowAgain()
    {
        string body = string.Empty;
        var showReads = 0;
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            if (path.Contains("/show/1396"))
            {
                showReads++;
                return Json(HttpStatusCode.OK, ShowJson);
            }
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":1}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        // The caller resolves the target (SerializdSeasonFallback), so the client posts it as is.
        await client.CreateEpisodeReviewAsync(1396, seasonId: 3572, episodeNumber: 4, rating: 9, reviewText: "good ep", containsSpoiler: false);

        Assert.Contains("\"season_id\":3572", body);
        Assert.Contains("\"episode_number\":4", body);
        Assert.Contains("\"is_log\":true", body);
        Assert.Equal(0, showReads);
    }

    // ----- Diary -----

    [Fact]
    public async Task GetDiaryEpisodes_ParsesAndDedupsEntries()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath + req.RequestUri.Query;
            if (path.Contains("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/diary?page=1"))
                return Json(HttpStatusCode.OK,
                    "{\"totalPages\":1,\"reviews\":[" +
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":4,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}," +
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":4,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}," +
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":5,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}]}");
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var diary = await client.GetDiaryEpisodesAsync();

        Assert.Equal(2, diary.Count); // the repeated (1396,1,4) entry is deduped
        Assert.Contains(diary, d => d.ShowTmdbId == 1396 && d.SeasonNumber == 1 && d.EpisodeNumber == 4);
        Assert.Contains(diary, d => d.ShowTmdbId == 1396 && d.SeasonNumber == 1 && d.EpisodeNumber == 5);
    }

    /// <summary>
    /// Regression test for issue #114: a real Serializd diary contains season-level and show-level
    /// reviews, where episodeNumber (and sometimes seasonId) is JSON null. JsonElement.TryGetInt32
    /// THROWS on a non-Number element rather than returning false, so a single such entry aborted
    /// the whole import with "The requested operation requires an element of type 'Number', but the
    /// target element has type 'Null'". Those entries are not episode watches, so the right
    /// behaviour is to skip them and keep importing the real ones.
    /// </summary>
    [Fact]
    public async Task GetDiaryEpisodes_NullNumericFields_SkipsEntryInsteadOfThrowing()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath + req.RequestUri.Query;
            if (path.Contains("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/diary?page=1"))
                return Json(HttpStatusCode.OK,
                    "{\"totalPages\":1,\"reviews\":[" +
                    // season-level review: no episode
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":null,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}," +
                    // show-level review: no season either
                    "{\"showId\":1396,\"seasonId\":null,\"episodeNumber\":null,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}," +
                    // null showId
                    "{\"showId\":null,\"seasonId\":3572,\"episodeNumber\":2,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}," +
                    // null inside showSeasons, which is a separate parse path
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":7,\"showSeasons\":[{\"id\":null,\"seasonNumber\":null},{\"id\":3572,\"seasonNumber\":1}]}," +
                    // the one genuine episode watch in the page
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":5,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}]}");
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var diary = await client.GetDiaryEpisodesAsync();

        // Episode 7 survives despite a null-bearing entry in its own showSeasons list, and 5 is the
        // plain case. The three entries with null showId/episodeNumber/seasonId are skipped.
        Assert.Equal(2, diary.Count);
        Assert.Contains(diary, d => d.ShowTmdbId == 1396 && d.SeasonNumber == 1 && d.EpisodeNumber == 7);
        Assert.Contains(diary, d => d.ShowTmdbId == 1396 && d.SeasonNumber == 1 && d.EpisodeNumber == 5);
    }

    /// <summary>A null totalPages must not throw either; it just ends pagination.</summary>
    [Fact]
    public async Task GetDiaryEpisodes_NullTotalPages_DoesNotThrow()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath + req.RequestUri.Query;
            if (path.Contains("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/diary?page=1"))
                return Json(HttpStatusCode.OK,
                    "{\"totalPages\":null,\"reviews\":[" +
                    "{\"showId\":1396,\"seasonId\":3572,\"episodeNumber\":5,\"showSeasons\":[{\"id\":3572,\"seasonNumber\":1}]}]}");
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var diary = await client.GetDiaryEpisodesAsync();

        Assert.Single(diary);
    }

    [Fact]
    public async Task GetDiaryEpisodes_PaginatesUntilLastPage()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath + req.RequestUri.Query;
            if (path.Contains("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/diary?page=1"))
                return Json(HttpStatusCode.OK,
                    "{\"totalPages\":2,\"reviews\":[{\"showId\":1,\"seasonId\":10,\"episodeNumber\":1,\"showSeasons\":[{\"id\":10,\"seasonNumber\":1}]}]}");
            if (path.Contains("/diary?page=2"))
                return Json(HttpStatusCode.OK,
                    "{\"totalPages\":2,\"reviews\":[{\"showId\":2,\"seasonId\":20,\"episodeNumber\":1,\"showSeasons\":[{\"id\":20,\"seasonNumber\":1}]}]}");
            return Json(HttpStatusCode.OK, "{\"reviews\":[]}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var diary = await client.GetDiaryEpisodesAsync();

        Assert.Equal(2, diary.Count);
        Assert.Contains(diary, d => d.ShowTmdbId == 1);
        Assert.Contains(diary, d => d.ShowTmdbId == 2);
    }

    [Fact]
    public async Task GetDiaryEpisodes_UsernameUnresolvable_Throws()
    {
        // Login response omits "username", and /validateauthtoken also fails to resolve
        // one, so EnsureUsernameAsync leaves _username empty.
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"token\":\"t\"}");
            if (path.EndsWith("/validateauthtoken"))
                return Json(HttpStatusCode.Unauthorized, "{}");
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var ex = await Assert.ThrowsAsync<Exception>(() => client.GetDiaryEpisodesAsync());
        Assert.Contains("username unknown", ex.Message);
    }

    // ----- Cached-token reuse resolves username via /validateauthtoken -----

    [Fact]
    public async Task GetWatchlist_ReusedCachedToken_ResolvesUsernameViaValidateAuthToken()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"tok\"}");
            if (path.EndsWith("/validateauthtoken"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\"}");
            if (path.Contains("/watchlistpage_v2/"))
                return Json(HttpStatusCode.OK, "{\"totalPages\":1,\"items\":[]}");
            return Json(HttpStatusCode.OK, "{}");
        });

        // c1 does the real login (caches the token); c2 reuses the cached token, so its own
        // _username field starts empty and GetWatchlistAsync must resolve it via /validateauthtoken.
        using var c1 = new SerializdApiClient(Log, handler);
        await c1.AuthenticateAsync("cache-reuse@example.com", "pw");

        using var c2 = new SerializdApiClient(Log, handler);
        await c2.AuthenticateAsync("cache-reuse@example.com", "pw");
        var entries = await c2.GetWatchlistAsync();

        Assert.Empty(entries);
    }

    // ----- SendAsync retry behaviour -----

    [Fact]
    public async Task RateLimited_WaitsRetryAfterThenRetries()
    {
        int attempts = 0;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            attempts++;
            if (attempts == 1)
            {
                var resp = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(30));
                return resp;
            }
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.SetShowMetaAsync(1396, rating: 5, like: false); // should not throw

        Assert.Equal(2, attempts); // rate-limited once, retried once
    }

    [Fact]
    public async Task RateLimited_ForLongerThanTheCap_FailsAtOnceWithoutRetrying()
    {
        int attempts = 0;
        var handler = LoginThen(_ =>
        {
            attempts++;
            var resp = Json(HttpStatusCode.TooManyRequests, "{}");
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return resp;
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SerializdRequestException>(() => client.SetShowMetaAsync(1396, rating: 5, like: false));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, attempts);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task RateLimitWait_StopsWhenTheSyncIsCancelled()
    {
        int attempts = 0;
        using var cts = new CancellationTokenSource();
        var handler = LoginThen(_ =>
        {
            attempts++;
            cts.Cancel(); // the sync is stopped while the 429 comes back
            var resp = Json(HttpStatusCode.TooManyRequests, "{}");
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(50));
            return resp;
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.LogEpisodesAsync(1396, 3572, new[] { 1 }, cts.Token));

        Assert.Equal(1, attempts);
        // An uncancellable wait would sit out the 50 s before the retry stopped at the request gate.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task ServerErrorBackoff_StopsWhenTheSyncIsCancelled()
    {
        int attempts = 0;
        using var cts = new CancellationTokenSource();
        var handler = LoginThen(_ =>
        {
            attempts++;
            cts.Cancel(); // the sync is stopped while the first attempt is answered (a write, so the send itself is never cut off)
            return Json(HttpStatusCode.ServiceUnavailable, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetShowMetaAsync(1396, rating: 5, like: false, cts.Token));

        Assert.Equal(1, attempts);
        // An uncancellable backoff also ends in a cancellation (the retry stops at the request
        // gate), so only the time tells them apart: the backoff is at least 500 ms, while a
        // cancelled one returns at once.
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(450), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task Read_CancelledByTheCaller_IsNotRetriedAsATimeout()
    {
        int reads = 0;
        using var cts = new CancellationTokenSource();
        var handler = new AsyncApiMockHandler(async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            reads++;
            cts.CancelAfter(50);
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, ShowJson);
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ResolveSeasonIdAsync(1396, 1, cts.Token));

        Assert.Equal(1, reads);
        // Cut off by the caller, not left to run into the 60 s request timeout.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task TransientServerError_RetriesThenSucceeds()
    {
        int attempts = 0;
        var handler = new ApiMockHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            attempts++;
            return attempts < 2
                ? Json(HttpStatusCode.ServiceUnavailable, "{}")
                : Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.SetShowMetaAsync(1396, rating: 5, like: false); // should not throw after one backoff+retry

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task GetWatchlist_ReusesTheCachedSeasonMap_RefetchingOnlyForAnUnknownSeason()
    {
        var showFetches = 0;
        var watchlistSeasonIds = "[515011]";
        var showJson = "{\"seasons\":[{\"id\":392941,\"seasonNumber\":3},{\"id\":515011,\"seasonNumber\":5}]}";
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"8bitproxy\",\"token\":\"t\"}");
            if (path.Contains("/watchlistpage_v2/1"))
                return Json(HttpStatusCode.OK,
                    $"{{\"totalPages\":1,\"items\":[{{\"showId\":136315,\"seasonIds\":{watchlistSeasonIds}}}]}}");
            if (path.Contains("/show/136315"))
            {
                showFetches++;
                return Json(HttpStatusCode.OK, showJson);
            }
            return Json(HttpStatusCode.OK, "{\"items\":[]}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        Assert.Equal(new[] { 5 }, Assert.Single(await client.GetWatchlistAsync()).SeasonNumbers);
        await client.GetWatchlistAsync();
        Assert.Equal(392941, await client.ResolveSeasonIdAsync(136315, 3)); // same cache as the scrobble path
        Assert.Equal(1, showFetches);

        // A newly aired season's id isn't in the cached map, so the show is fetched once more.
        watchlistSeasonIds = "[515011,600001]";
        showJson = "{\"seasons\":[{\"id\":515011,\"seasonNumber\":5},{\"id\":600001,\"seasonNumber\":6}]}";
        var entry = Assert.Single(await client.GetWatchlistAsync());

        Assert.Equal(new[] { 5, 6 }, entry.SeasonNumbers);
        Assert.Equal(2, showFetches);
    }

    // Render cold starts: no status code, just a failed connection or a hung request.
    // SocketsHttpHandler reports a refused connection as HttpRequestError.ConnectionError, and a
    // connection dropped after the request went out as ResponseEnded (or Unknown).

    private static HttpRequestException Refused()
        => new(HttpRequestError.ConnectionError, "Connection refused (api.serializd.example:443)");

    private static HttpRequestException ResetAfterSend()
        => new(HttpRequestError.ResponseEnded, "The response ended prematurely.");

    private static ApiMockHandler LoginThen(Func<HttpRequestMessage, HttpResponseMessage> rest)
        => new(req => req.RequestUri!.AbsolutePath.EndsWith("/login")
            ? Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}")
            : rest(req));

    [Fact]
    public async Task Write_ConnectionRefused_RetriesThenSucceeds()
    {
        int attempts = 0;
        var handler = LoginThen(_ =>
        {
            if (++attempts < 2) throw Refused();
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.SetShowMetaAsync(1396, rating: 5, like: false);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ConnectionRefused_ExhaustsRetriesThenThrows()
    {
        int attempts = 0;
        var handler = new ApiMockHandler(_ =>
        {
            attempts++;
            throw Refused();
        });

        using var client = new SerializdApiClient(Log, handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.AuthenticateAsync("me@example.com", "pw"));

        Assert.Equal(4, attempts); // initial try + 3 backoff retries
    }

    // A dropped connection after the POST went out may mean Serializd already logged it, so a
    // retry would log the episode twice.
    [Fact]
    public async Task Write_ResetAfterSend_IsNotRetried()
    {
        int posts = 0;
        var handler = LoginThen(_ =>
        {
            posts++;
            throw ResetAfterSend();
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CreateEpisodeLogAsync(1396, 3572, 1, DateTime.UtcNow, rating: null, isRewatch: false));

        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Write_Timeout_IsNotRetried()
    {
        var original = SerializdApiClient.RequestTimeout;
        SerializdApiClient.RequestTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            int posts = 0;
            var handler = new AsyncApiMockHandler(async (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                    return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
                posts++;
                await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, "{}");
            });

            using var client = new SerializdApiClient(Log, handler);
            await client.AuthenticateAsync("me@example.com", "pw");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetShowMetaAsync(1396, rating: 5, like: false));

            Assert.Equal(1, posts);
        }
        finally
        {
            SerializdApiClient.RequestTimeout = original;
        }
    }

    [Fact]
    public async Task Read_ResetConnection_IsRetried()
    {
        int reads = 0;
        var handler = LoginThen(_ =>
        {
            if (++reads < 2) throw ResetAfterSend();
            return Json(HttpStatusCode.OK, ShowJson);
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        Assert.Equal(3572, await client.ResolveSeasonIdAsync(1396, 1));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Read_HungRequest_TimesOutAndRetries()
    {
        var original = SerializdApiClient.RequestTimeout;
        SerializdApiClient.RequestTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            int reads = 0;
            var handler = new AsyncApiMockHandler(async (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/login"))
                    return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
                if (++reads == 1)
                    await Task.Delay(Timeout.Infinite, ct);
                return Json(HttpStatusCode.OK, ShowJson);
            });

            using var client = new SerializdApiClient(Log, handler);
            await client.AuthenticateAsync("me@example.com", "pw");

            Assert.Equal(3572, await client.ResolveSeasonIdAsync(1396, 1));
            Assert.Equal(2, reads);
        }
        finally
        {
            SerializdApiClient.RequestTimeout = original;
        }
    }

    [Fact]
    public async Task CancellationThatIsNotOurTimeout_IsNotRetried()
    {
        int attempts = 0;
        var handler = new ApiMockHandler(_ =>
        {
            attempts++;
            throw new TaskCanceledException();
        });

        using var client = new SerializdApiClient(Log, handler);
        await Assert.ThrowsAsync<TaskCanceledException>(() => client.AuthenticateAsync("me@example.com", "pw"));

        Assert.Equal(1, attempts);
    }

    // The production client is shared by every account, so a token must only ever ride on the
    // request it belongs to, never on the client's default headers.
    [Fact]
    public async Task TwoAccounts_EachRequestCarriesItsOwnToken_AndNoneIsADefaultHeader()
    {
        var seen = new List<(string Path, string? Token)>();
        HttpResponseMessage Respond(HttpRequestMessage req, string token)
        {
            seen.Add((req.RequestUri!.AbsolutePath, req.Headers.Authorization?.Parameter));
            return req.RequestUri.AbsolutePath.EndsWith("/login")
                ? Json(HttpStatusCode.OK, $"{{\"username\":\"u\",\"token\":\"{token}\"}}")
                : Json(HttpStatusCode.OK, ShowJson);
        }

        using var a = new SerializdApiClient(Log, new ApiMockHandler(r => Respond(r, "token-a")));
        using var b = new SerializdApiClient(Log, new ApiMockHandler(r => Respond(r, "token-b")));
        await a.AuthenticateAsync("a@example.com", "pw");
        await b.AuthenticateAsync("b@example.com", "pw");
        SerializdApiClient.ResetCachesForTesting();
        await a.ResolveSeasonIdAsync(1396, 1);
        SerializdApiClient.ResetCachesForTesting();
        await b.ResolveSeasonIdAsync(1396, 1);

        Assert.Equal(new string?[] { "token-a", "token-b" },
            seen.Where(s => s.Path.Contains("/show/1396")).Select(s => s.Token).ToArray());
        Assert.Null(a.HttpForTesting.DefaultRequestHeaders.Authorization);
        Assert.Null(b.HttpForTesting.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void ProductionClients_ShareOneHttpClient_AndDisposeLeavesItUsable()
    {
        var first = new SerializdApiClient(Log);
        var shared = first.HttpForTesting;
        first.Dispose();

        using var second = new SerializdApiClient(Log);

        Assert.Same(shared, second.HttpForTesting);
        shared.CancelPendingRequests(); // throws ObjectDisposedException if Dispose closed it
    }
}

internal class AsyncApiMockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public AsyncApiMockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
