using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// A signed-in client always has the member id. The token cache once stored the token before
/// /me had answered, so a second sign-in of the same account in that window (or for an hour
/// after a failed /me) reused the token with no member id. Its diary reads then sent "member="
/// and Letterboxd answered 404, which is how the review live test failed in CI while parallel
/// live tests signed in to the same account.
/// </summary>
public class ApiClientMemberIdCacheTests
{
    /// <summary>
    /// Mock of the auth endpoints plus GET /log-entries, which (like Letterboxd) answers 404 to a
    /// request without a member. The first /me can be held until the test releases it.
    /// </summary>
    private sealed class Handler : HttpMessageHandler
    {
        public readonly TaskCompletionSource FirstMeReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFirstMe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldFirstMe;
        public int FailMeTimes;
        public int TokenGrants;
        public readonly ConcurrentQueue<string> LogEntryQueries = new();
        private int _meCalls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref TokenGrants);
                return Json("{\"access_token\":\"t\",\"expires_in\":3600,\"refresh_token\":\"r\"}");
            }

            if (path.EndsWith("/me", StringComparison.Ordinal))
            {
                var call = Interlocked.Increment(ref _meCalls);
                if (call == 1 && HoldFirstMe)
                {
                    FirstMeReached.TrySetResult();
                    await ReleaseFirstMe.Task.ConfigureAwait(false);
                }

                if (call <= FailMeTimes)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return Json("{\"member\":{\"id\":\"m-42\",\"username\":\"demo-cinephile\"}}");
            }

            if (path.EndsWith("/log-entries", StringComparison.Ordinal))
            {
                var query = request.RequestUri.Query;
                LogEntryQueries.Enqueue(query);
                return query.Contains("member=m-42", StringComparison.Ordinal)
                    ? Json("{\"items\":[]}")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static string NewUser() => "member-cache-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ASecondSignInWhileTheFirstAwaitsMe_StillGetsTheMemberId()
    {
        var handler = new Handler { HoldFirstMe = true };
        var user = NewUser();

        using var first = new LetterboxdApiClient(NullLogger.Instance, handler);
        var firstSignIn = first.AuthenticateAsync(user, "pass");
        await handler.FirstMeReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The first client has its token but not yet its member id.
        using var second = new LetterboxdApiClient(NullLogger.Instance, handler);
        await second.AuthenticateAsync(user, "pass");
        handler.ReleaseFirstMe.SetResult();
        await firstSignIn;

        Assert.Equal("m-42", second.MemberIdForTesting);
        Assert.Equal("m-42", first.MemberIdForTesting);
        // The read that 404'd in CI.
        await second.GetDiaryInfoAsync("2b8k", user);
        Assert.All(handler.LogEntryQueries, q => Assert.Contains("member=m-42", q));
    }

    [Fact]
    public async Task AFailedMe_LeavesNoCachedToken_SoTheNextSignInStartsAgain()
    {
        var handler = new Handler { FailMeTimes = 1 };
        var user = NewUser();

        using (var first = new LetterboxdApiClient(NullLogger.Instance, handler))
            await Assert.ThrowsAnyAsync<Exception>(() => first.AuthenticateAsync(user, "pass"));

        using var second = new LetterboxdApiClient(NullLogger.Instance, handler);
        await second.AuthenticateAsync(user, "pass");

        Assert.Equal(2, handler.TokenGrants);
        Assert.Equal("m-42", second.MemberIdForTesting);
        await second.GetDiaryInfoAsync("2b8k", user);
    }

    [Fact]
    public async Task ACachedTokenWithoutAMemberId_IsNotReused()
    {
        var handler = new Handler();
        var user = NewUser();
        LetterboxdApiClient.SeedTokenCacheForTesting(user, "pass",
            new LetterboxdApiClient.TokenInfo("old", "r", DateTime.UtcNow.AddHours(1), string.Empty));

        using var client = new LetterboxdApiClient(NullLogger.Instance, handler);
        await client.AuthenticateAsync(user, "pass");

        Assert.Equal("m-42", client.MemberIdForTesting);
        await client.GetDiaryInfoAsync("2b8k", user);
        Assert.All(handler.LogEntryQueries, q => Assert.Contains("member=m-42", q));
    }

    [Fact]
    public async Task ACachedTokenWithItsMemberId_IsReusedWithoutAnyRequest()
    {
        var handler = new Handler();
        var user = NewUser();
        LetterboxdApiClient.SeedTokenCacheForTesting(user, "pass",
            new LetterboxdApiClient.TokenInfo("old", "r", DateTime.UtcNow.AddHours(1), "m-42"));

        using var client = new LetterboxdApiClient(NullLogger.Instance, handler);
        await client.AuthenticateAsync(user, "pass");

        Assert.Equal(0, handler.TokenGrants);
        Assert.Equal("m-42", client.MemberIdForTesting);
    }
}
