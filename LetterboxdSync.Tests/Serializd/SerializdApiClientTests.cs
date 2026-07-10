using System;
using System.Collections.Generic;
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
    public async Task CreateEpisodeLog_OmitsRatingWhenUnrated()
    {
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

        Assert.DoesNotContain("\"rating\"", body);
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
}
