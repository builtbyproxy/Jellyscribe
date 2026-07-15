using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        "{\"seasons\":[{\"id\":3577,\"seasonNumber\":0},{\"id\":3572,\"seasonNumber\":1},{\"id\":3573,\"seasonNumber\":2}]}";

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
    public async Task Authenticate_BadCredentials_Throws()
    {
        var handler = new ApiMockHandler(_ =>
            Json(HttpStatusCode.Unauthorized, "{\"message\":\"Incorrect password.\"}"));

        using var client = new SerializdApiClient(Log, handler);
        var ex = await Assert.ThrowsAsync<Exception>(() => client.AuthenticateAsync("me@example.com", "wrong"));
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
        var ex = await Assert.ThrowsAsync<Exception>(() => client.ResolveSeasonIdAsync(1396, 1));
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

        var ex = await Assert.ThrowsAsync<Exception>(() =>
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

        var ex = await Assert.ThrowsAsync<Exception>(() => client.LogEpisodesAsync(1396, 3572, new[] { 1 }));
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

        var ex = await Assert.ThrowsAsync<Exception>(() => client.SetShowMetaAsync(1396, rating: 5, like: false));
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

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.CreateShowReviewAsync(1396, rating: 5, reviewText: null, containsSpoiler: false));
        Assert.Contains("Serializd review", ex.Message);
    }

    [Fact]
    public async Task CreateEpisodeReview_ResolvesSeasonId_PostsScopedPayload()
    {
        string body = string.Empty;
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            if (path.Contains("/show/1396"))
                return Json(HttpStatusCode.OK, ShowJson);
            body = ReadBody(req);
            return Json(HttpStatusCode.OK, "{\"id\":1}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");
        await client.CreateEpisodeReviewAsync(1396, seasonNumber: 1, episodeNumber: 4, rating: 9, reviewText: "good ep", containsSpoiler: false);

        Assert.Contains("\"season_id\":3572", body);
        Assert.Contains("\"episode_number\":4", body);
        Assert.Contains("\"is_log\":true", body);
    }

    [Fact]
    public async Task CreateEpisodeReview_UnknownSeason_Throws()
    {
        var handler = new ApiMockHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/login"))
                return Json(HttpStatusCode.OK, "{\"username\":\"u\",\"token\":\"t\"}");
            if (path.Contains("/show/1396"))
                return Json(HttpStatusCode.OK, ShowJson);
            return Json(HttpStatusCode.OK, "{}");
        });

        using var client = new SerializdApiClient(Log, handler);
        await client.AuthenticateAsync("me@example.com", "pw");

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            client.CreateEpisodeReviewAsync(1396, seasonNumber: 99, episodeNumber: 1, rating: 5, reviewText: null, containsSpoiler: false));
        Assert.Contains("no season", ex.Message);
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
}
