using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// A sync run signs in once, and a long first export outlasts the hour an API token lives. The
/// client renews the session before the token runs out, renews and resends once on a 401, and
/// when renewal fails throws a block the runner pauses on, instead of every later film failing
/// with a 401 of its own.
/// </summary>
public class ApiClientSessionRenewalTests
{
    /// <summary>
    /// Mock Letterboxd API. The password grant issues "old" (lifetime <see cref="FirstLifetime"/>);
    /// each refresh grant issues "new-N". Diary reads and writes accept only the tokens in
    /// <see cref="Accepted"/> and answer 401 to anything else.
    /// </summary>
    private sealed class Api : HttpMessageHandler
    {
        public int FirstLifetime = 3600;
        public bool RefuseRefresh;
        public bool RateLimitRefresh;
        public Func<string, bool> Accepted = _ => true;
        public Action? OnRateLimited;
        public readonly ConcurrentQueue<string> Grants = new();
        public readonly ConcurrentQueue<string> BearersSeen = new();
        public readonly ConcurrentQueue<string> DiaryQueries = new();
        public int MeCalls;
        private int _refreshes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/token", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (body.Contains("grant_type=refresh_token", StringComparison.Ordinal))
                {
                    Grants.Enqueue("refresh");
                    if (RateLimitRefresh)
                    {
                        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                        OnRateLimited?.Invoke();
                        return limited;
                    }
                    if (RefuseRefresh)
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") };
                    var n = Interlocked.Increment(ref _refreshes);
                    return Json($"{{\"access_token\":\"new-{n}\",\"expires_in\":3600,\"refresh_token\":\"r-{n}\"}}");
                }

                Grants.Enqueue("password");
                return Json($"{{\"access_token\":\"old\",\"expires_in\":{FirstLifetime},\"refresh_token\":\"r-0\"}}");
            }

            if (path.EndsWith("/me", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref MeCalls);
                return Json("{\"member\":{\"id\":\"m-1\",\"username\":\"demo-cinephile\"}}");
            }

            if (path.EndsWith("/log-entries", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                    DiaryQueries.Enqueue(request.RequestUri.Query);
                var bearer = request.Headers.Authorization?.Parameter ?? string.Empty;
                BearersSeen.Enqueue(bearer);
                if (!Accepted(bearer))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return request.Method == HttpMethod.Get ? Json("{\"items\":[]}") : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    // Each test signs in as its own account, so the shared token cache never links two tests.
    private static string NewUser() => "renewal-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task TokenAboutToExpire_IsRenewedBeforeTheRequest()
    {
        var api = new Api { FirstLifetime = 60 };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        await client.GetDiaryInfoAsync("lid", "demo-cinephile");

        Assert.Equal(new[] { "password", "refresh" }, api.Grants.ToArray());
        Assert.Equal(new[] { "new-1" }, api.BearersSeen.ToArray());
    }

    [Fact]
    public async Task AfterARenewal_LaterRequestsUseTheNewTokenWithoutRenewingAgain()
    {
        var api = new Api { FirstLifetime = 60 };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        await client.GetDiaryInfoAsync("lid", "demo-cinephile");
        await client.GetDiaryInfoAsync("lid", "demo-cinephile");

        Assert.Equal(1, api.Grants.Count(g => g == "refresh"));
        Assert.Equal(new[] { "new-1", "new-1" }, api.BearersSeen.ToArray());
    }

    [Fact]
    public async Task Renewal_KeepsTheMemberIdWithoutAskingAgain()
    {
        // An account's member id never changes. Asking /me again during a renewal could only
        // fail, leaving a token with no member id, so the renewal keeps the one it has.
        var api = new Api { FirstLifetime = 60 };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        await client.GetDiaryInfoAsync("lid", "demo-cinephile");

        Assert.Equal(1, api.MeCalls);
        Assert.Contains("member=m-1", Assert.Single(api.DiaryQueries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledBeforeRenewing_ThrowsWithoutSpendingTheRefreshToken()
    {
        var api = new Api { FirstLifetime = 60 };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetDiaryInfoAsync("lid", "demo-cinephile", cts.Token));

        Assert.DoesNotContain("refresh", api.Grants);
        Assert.Empty(api.BearersSeen);
    }

    [Fact]
    public async Task CancelledDuringARateLimitedRenewal_StopsWaitingWithoutRetryingTheRefresh()
    {
        // A 429 means the refresh token was not spent, so cancelling the wait is safe: shutdown
        // must not sit out the Retry-After.
        var api = new Api { FirstLifetime = 60, RateLimitRefresh = true };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");
        using var cts = new CancellationTokenSource();
        // Cancelled as the 429 comes back, so the cancellation always lands in the Retry-After
        // wait, never before the refresh is sent.
        api.OnRateLimited = cts.Cancel;

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetDiaryInfoAsync("lid", "demo-cinephile", cts.Token));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(1, api.Grants.Count(g => g == "refresh"));
        Assert.Empty(api.BearersSeen);
    }

    [Fact]
    public async Task SignInAfterARunRenewed_ReusesTheRenewedTokenWithoutAnotherGrant()
    {
        // A second sign-in (a playback event) while a run of the same account holds a renewed
        // token: it takes that token instead of spending the refresh token again.
        var api = new Api { FirstLifetime = 60 };
        var user = NewUser();
        using var run = new LetterboxdApiClient(NullLogger.Instance, api);
        await run.AuthenticateAsync(user, "pass");
        await run.GetDiaryInfoAsync("lid", "demo-cinephile");

        using var playback = new LetterboxdApiClient(NullLogger.Instance, api);
        await playback.AuthenticateAsync(user, "pass");
        await playback.GetDiaryInfoAsync("lid", "demo-cinephile");

        Assert.Equal(new[] { "password", "refresh" }, api.Grants.ToArray());
        Assert.Equal(new[] { "new-1", "new-1" }, api.BearersSeen.ToArray());
    }

    [Fact]
    public async Task SignInNearExpiry_RenewsWithTheCachedMemberIdInsteadOfAPasswordGrant()
    {
        // A playback event signs in while the run's cached token is inside the renewal window:
        // it spends the refresh token once, keeps the cached member id (no /me), and never falls
        // back to the password.
        var api = new Api { FirstLifetime = 60 };
        var user = NewUser();
        using var run = new LetterboxdApiClient(NullLogger.Instance, api);
        await run.AuthenticateAsync(user, "pass");

        using var playback = new LetterboxdApiClient(NullLogger.Instance, api);
        await playback.AuthenticateAsync(user, "pass");
        await playback.GetDiaryInfoAsync("lid", "demo-cinephile");

        Assert.Equal(new[] { "password", "refresh" }, api.Grants.ToArray());
        Assert.Equal(1, api.MeCalls);
        Assert.Equal(new[] { "new-1" }, api.BearersSeen.ToArray());
        Assert.Contains("member=m-1", Assert.Single(api.DiaryQueries), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInWhileARenewalWaitsOutARateLimit_UsesThePasswordInsteadOfHanging()
    {
        // A run's renewal sits out a 30 s Retry-After under the account's lock. A sign-in (a
        // review submit, a playback event) gives up on the lock after SignInLockWait.
        var api = new Api { FirstLifetime = 60, RateLimitRefresh = true };
        var user = NewUser();
        using var run = new LetterboxdApiClient(NullLogger.Instance, api);
        await run.AuthenticateAsync(user, "pass");
        using var cts = new CancellationTokenSource();
        var limited = new TaskCompletionSource();
        api.OnRateLimited = () => limited.TrySetResult();
        var renewing = run.GetDiaryInfoAsync("lid", "demo-cinephile", cts.Token);
        await limited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var started = DateTime.UtcNow;
        using var playback = new LetterboxdApiClient(NullLogger.Instance, api);
        await playback.AuthenticateAsync(user, "pass");
        var waited = DateTime.UtcNow - started;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renewing);

        Assert.True(waited < TimeSpan.FromSeconds(20), $"sign-in waited {waited}");
        Assert.Equal(new[] { "password", "refresh", "password" }, api.Grants.ToArray());
    }

    [Fact]
    public async Task RefreshTokenRefused_IsNotSentAgainByTheNextSignIn()
    {
        // Letterboxd refused the account's refresh token. A new client of the account (each
        // playback event builds one) must not send the same refused token again.
        var api = new Api { FirstLifetime = 60, RefuseRefresh = true };
        var user = NewUser();
        using var run = new LetterboxdApiClient(NullLogger.Instance, api);
        await run.AuthenticateAsync(user, "pass");
        await run.GetDiaryInfoAsync("lid", "demo-cinephile");

        using var playback = new LetterboxdApiClient(NullLogger.Instance, api);
        await playback.AuthenticateAsync(user, "pass");

        Assert.Equal(new[] { "password", "refresh", "password" }, api.Grants.ToArray());
    }

    [Fact]
    public async Task UnexpectedUnauthorized_RenewsAndResendsTheWriteOnce()
    {
        var api = new Api { Accepted = bearer => bearer != "old" };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        await client.MarkAsWatchedAsync("film", "lid", DateTime.Today, liked: false);

        Assert.Equal(new[] { "old", "new-1" }, api.BearersSeen.ToArray());
        Assert.Equal(1, api.Grants.Count(g => g == "refresh"));
    }

    [Fact]
    public async Task RenewalRefused_ThrowsABlockAndFailsLaterCallsWithoutSending()
    {
        var api = new Api { Accepted = _ => false, RefuseRefresh = true };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        var ex = await Assert.ThrowsAsync<LetterboxdSessionExpiredException>(
            () => client.MarkAsWatchedAsync("film", "lid", DateTime.Today, liked: false));
        Assert.True(SyncErrors.IsBlock(ex));
        Assert.Equal(TelemetryService.CatAuth, TelemetryService.Classify(ex.Message));

        var sent = api.BearersSeen.Count;
        var grants = api.Grants.Count;
        await Assert.ThrowsAsync<LetterboxdSessionExpiredException>(() => client.GetDiaryInfoAsync("lid", "demo-cinephile"));
        Assert.Equal(sent, api.BearersSeen.Count);
        Assert.Equal(grants, api.Grants.Count);
    }

    [Fact]
    public async Task RenewalAheadRefused_DoesNotRetryOnEveryRequestWhileTheTokenStillWorks()
    {
        // The token has a few minutes left and the refresh is refused: each film must not send
        // another refused refresh (60-100 of them at the runner's pace). The token keeps working.
        var api = new Api { FirstLifetime = 60, RefuseRefresh = true };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        for (var i = 0; i < 5; i++)
            await client.GetDiaryInfoAsync("lid", "demo-cinephile");

        Assert.Equal(1, api.Grants.Count(g => g == "refresh"));
        Assert.All(api.BearersSeen, b => Assert.Equal("old", b));
    }

    [Fact]
    public async Task ExpiredSessionNearItsEnd_FailsLaterCallsWithoutAnotherRefreshRequest()
    {
        // A token inside the renewal window must not send another refresh request once the
        // session is known to be over.
        var api = new Api { FirstLifetime = 60, Accepted = _ => false, RefuseRefresh = true };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");
        await Assert.ThrowsAsync<LetterboxdSessionExpiredException>(() => client.GetDiaryInfoAsync("lid", "demo-cinephile"));

        var grants = api.Grants.Count;
        await Assert.ThrowsAsync<LetterboxdSessionExpiredException>(() => client.GetDiaryInfoAsync("lid", "demo-cinephile"));

        Assert.Equal(grants, api.Grants.Count);
    }

    [Fact]
    public async Task RenewedTokenAlsoRefused_ThrowsAfterOneResend()
    {
        var api = new Api { Accepted = _ => false };
        using var client = new LetterboxdApiClient(NullLogger.Instance, api);
        await client.AuthenticateAsync(NewUser(), "pass");

        await Assert.ThrowsAsync<LetterboxdSessionExpiredException>(() => client.GetDiaryInfoAsync("lid", "demo-cinephile"));

        Assert.Equal(new[] { "old", "new-1" }, api.BearersSeen.ToArray());
    }

    [Fact]
    public async Task TwoClientsOfOneAccount_RenewOnceAndShareTheNewToken()
    {
        // A refresh token may be single-use: the second client must take the token the first one
        // renewed, not spend the refresh token again.
        var api = new Api { Accepted = bearer => bearer != "old" };
        var user = NewUser();
        using var first = new LetterboxdApiClient(NullLogger.Instance, api);
        using var second = new LetterboxdApiClient(NullLogger.Instance, api);
        await first.AuthenticateAsync(user, "pass");
        await second.AuthenticateAsync(user, "pass");

        await first.GetDiaryInfoAsync("lid", "demo-cinephile");
        await second.GetDiaryInfoAsync("lid", "demo-cinephile");

        // The second client takes the renewed token from the cache before sending, so it never
        // sends the old one to be refused.
        Assert.Equal(1, api.Grants.Count(g => g == "refresh"));
        Assert.Equal(new[] { "old", "new-1", "new-1" }, api.BearersSeen.ToArray());
    }
}
