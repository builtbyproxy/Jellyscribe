using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class RetryAfterLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoHeader_WaitsTheDefault()
        => Assert.Equal(RetryAfterLimit.Default, RetryAfterLimit.Wait(null, Now));

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    public void SecondsUpToTheCap_AreHonoured(int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds),
            RetryAfterLimit.Wait(new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)), Now));

    [Theory]
    [InlineData(61)]
    [InlineData(3600)]
    public void SecondsPastTheCap_AreNotWaitedFor(int seconds)
        => Assert.Null(RetryAfterLimit.Wait(new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)), Now));

    [Fact]
    public void AnHttpDate_WaitsUntilThen()
        => Assert.Equal(TimeSpan.FromSeconds(20),
            RetryAfterLimit.Wait(new RetryConditionHeaderValue(Now.AddSeconds(20)), Now));

    [Fact]
    public void AnHttpDateAlreadyPast_WaitsNothing()
        => Assert.Equal(TimeSpan.Zero, RetryAfterLimit.Wait(new RetryConditionHeaderValue(Now.AddMinutes(-1)), Now));

    [Fact]
    public void AnHttpDatePastTheCap_IsNotWaitedFor()
        => Assert.Null(RetryAfterLimit.Wait(new RetryConditionHeaderValue(Now.AddMinutes(5)), Now));
}

public class ApiClientRetryAfterTests
{
    private const int TmdbId = 4242;
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    public ApiClientRetryAfterTests() => LetterboxdApiClient.ResetFilmCacheForTesting(TmdbId);

    private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return resp;
    }

    private static HttpResponseMessage OneFilm() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            items = new[] { new { id = "abc", link = "https://letterboxd.com/film/test/", links = Array.Empty<object>() } }
        }))
    };

    /// <summary>
    /// What the API checks: the signature is the HMAC of method, the URL up to the signature,
    /// and the body, under the API secret.
    /// </summary>
    private static bool IsSignedCorrectly(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        var at = url.LastIndexOf("&signature=", StringComparison.Ordinal);
        if (at < 0) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(LetterboxdApiConstants.ApiSecret));
        var expected = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{request.Method.Method}\0{url[..at]}\0")));
        return url[(at + "&signature=".Length)..] == expected;
    }

    private static string Nonce(Uri uri)
        => uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("nonce=", StringComparison.Ordinal))["nonce=".Length..];

    [Fact]
    public async Task RateLimited_Retry_IsSignedAfreshWithANewNonce()
    {
        var filmRequests = new List<HttpRequestMessage>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") != true) return null;
            filmRequests.Add(request);
            return filmRequests.Count == 1 ? RateLimited(TimeSpan.FromMilliseconds(50)) : OneFilm();
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var film = await client.LookupFilmByTmdbIdAsync(TmdbId);

        Assert.Equal("abc", film.FilmId);
        Assert.Equal(2, filmRequests.Count);
        Assert.All(filmRequests, r => Assert.True(IsSignedCorrectly(r), r.RequestUri!.ToString()));
        Assert.NotEqual(Nonce(filmRequests[0].RequestUri!), Nonce(filmRequests[1].RequestUri!));
    }

    [Fact]
    public async Task RateLimited_ForLongerThanTheCap_FailsAtOnceWithoutRetrying()
    {
        var filmRequests = 0;
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") != true) return null;
            filmRequests++;
            return RateLimited(TimeSpan.FromMinutes(10));
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.LookupFilmByTmdbIdAsync(TmdbId));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, filmRequests);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"waited {clock.Elapsed}");
    }

    [Fact]
    public async Task DiaryCheck_RateLimitedForLongerThanTheCap_IsABlock_SoTheRunStopsSoon()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: request =>
            request.RequestUri?.AbsolutePath.EndsWith("/log-entries") == true ? RateLimited(TimeSpan.FromMinutes(10)) : null);

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        var ex = await Assert.ThrowsAsync<DiaryCheckFailedException>(() => client.GetDiaryInfoAsync("abc", "user"));

        Assert.True(ex.Blocked);
        Assert.True(SyncErrors.IsBlock(ex));
    }

    [Fact]
    public async Task RateLimitWait_StopsWhenTheSyncIsCancelled()
    {
        var posts = 0;
        using var cts = new CancellationTokenSource();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/log-entries") != true) return null;
            posts++;
            cts.Cancel(); // the sync is stopped while the 429 comes back
            return RateLimited(TimeSpan.FromSeconds(50));
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        // A write: its send is never cut off, so the cancellation can only land on the wait. An
        // uncancellable wait would sit out the 50 s and post again.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.MarkAsWatchedAsync(
            "test", "abc", new DateTime(2026, 10, 1), liked: false, cancellationToken: cts.Token));

        Assert.Equal(1, posts);
    }
}
