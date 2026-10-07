using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class SeerrClientTests
{
    private const string BaseUrl = "http://jellyseerr.test";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task RequestMovieAsync_NoExistingMedia_PostsRequest()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100}"); // no mediaInfo → safe to request

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.True(posted, "request endpoint should have been called");
    }

    // ----- Request creation: who Seerr treats as the requester -----
    // FakeSeerr below models Seerr's real rules (seerr-team/seerr, server/middleware/auth.ts and
    // server/entity/MediaRequest.ts): an API-key call acts as user 1 (the owner admin) unless
    // X-API-User names someone else; a body userId from a caller without Manage Users and Manage
    // Requests is refused with 403; and a new request is APPROVED only when the ACTING user holds
    // Auto-Approve (or the media type's flag) or Manage Requests, otherwise PENDING.

    private const int SeerrAdmin = 2;
    private const int SeerrManageUsers = 8;
    private const int SeerrManageRequests = 16;
    private const int SeerrRequest = 32;
    private const int SeerrAutoApprove = 128;

    [Fact]
    public async Task RequestMovieAsync_ActsAsTheRequesterAndLeavesUserIdOutOfTheBody()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = SeerrRequest;

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, seerr, autoApprove: false);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        var post = Assert.Single(seerr.RequestPosts);
        Assert.Equal("7", post.ApiUser);
        Assert.DoesNotContain("userId", post.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The finding this pins: with the admin switch off, a Seerr user who needs approval must not
    /// get an approved request just because the plugin holds the admin API key. Before requests
    /// were made as the requester, Seerr saw the admin as the acting user and approved on creation.
    /// </summary>
    [Fact]
    public async Task RequestMovieAsync_UserWithoutAutoApprove_SwitchOff_StaysPendingAndWarns()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = SeerrRequest;

        var logs = new ListLogger();
        using var client = new SeerrClient(BaseUrl, ApiKey, logs, seerr, autoApprove: false);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        var created = Assert.Single(seerr.Created);
        Assert.Equal(7, created.RequestedBy);
        Assert.Equal(1, created.Status); // PENDING
        Assert.Equal(0, seerr.ApproveCalls);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("PENDING", StringComparison.Ordinal)
            && e.Message.Contains("Radarr", StringComparison.Ordinal));
    }

    /// <summary>A Seerr user with Auto-Approve is approved by Seerr itself, so no follow-up is needed.</summary>
    [Fact]
    public async Task RequestMovieAsync_UserWithAutoApprove_SwitchOff_ApprovedBySeerr()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = SeerrRequest | SeerrAutoApprove;

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, seerr, autoApprove: false);
        await client.RequestMovieAsync(100, 7);

        var created = Assert.Single(seerr.Created);
        Assert.Equal(2, created.Status); // APPROVED
        Assert.Equal(0, seerr.ApproveCalls);
    }

    /// <summary>
    /// Issue #110 guard: with the admin switch on (the default), a request that Seerr leaves
    /// PENDING for a user without Auto-Approve is approved with the admin key, so it reaches
    /// Radarr. The approve call must act as the admin, not as the requester, who cannot approve.
    /// </summary>
    [Fact]
    public async Task RequestMovieAsync_UserWithoutAutoApprove_SwitchOn_ApprovedWithTheAdminKey()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = SeerrRequest;

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, seerr);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        var created = Assert.Single(seerr.Created);
        Assert.Equal(2, created.Status);
        Assert.Equal(1, seerr.ApproveCalls);
    }

    /// <summary>A failed approve must not turn a created request into a reported failure.</summary>
    [Fact]
    public async Task RequestMovieAsync_ApproveFails_StillReportsRequested()
    {
        var seerr = new FakeSeerr { ApproveFails = true };
        seerr.Users[7] = SeerrRequest;

        var logs = new ListLogger();
        using var client = new SeerrClient(BaseUrl, ApiKey, logs, seerr);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Equal(1, Assert.Single(seerr.Created).Status);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("Could not approve", StringComparison.Ordinal));
    }

    /// <summary>A requester without request permission in Seerr is refused, and that is a failure.</summary>
    [Fact]
    public async Task RequestMovieAsync_UserWithoutRequestPermission_IsRefusedAsFailed()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = 0;

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, seerr);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Failed, result);
        Assert.Empty(seerr.Created);
    }

    /// <summary>The TV path creates requests the same way, as the requester.</summary>
    [Fact]
    public async Task RequestSeriesAsync_UserWithoutAutoApprove_SwitchOff_StaysPending()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = SeerrRequest;

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, seerr, autoApprove: false);
        var (result, _) = await client.RequestSeriesAsync(200, 7, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        var post = Assert.Single(seerr.RequestPosts);
        Assert.Equal("7", post.ApiUser);
        Assert.DoesNotContain("userId", post.Body, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(seerr.Created).Status);
        Assert.Equal(0, seerr.ApproveCalls);
    }

    [Fact]
    public async Task RequestSeriesAsync_UserWithoutAutoApprove_SwitchOn_ApprovedWithTheAdminKey()
    {
        var seerr = new FakeSeerr();
        seerr.Users[7] = SeerrRequest;

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, seerr);
        var (result, _) = await client.RequestSeriesAsync(200, 7, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Equal(2, Assert.Single(seerr.Created).Status);
        Assert.Equal(1, seerr.ApproveCalls);
    }

    /// <summary>
    /// The scenario from issue #110 as the reporter actually experiences it: requests were already
    /// created by an earlier version and are stranded at PENDING. RequestMovieAsync skips them
    /// (Seerr reports the media as already requested), so the approve-on-create path never runs.
    /// The reconcile pass must find and approve them.
    /// </summary>
    [Fact]
    public async Task ApprovePendingForUserAsync_ApprovesStrandedBacklog()
    {
        var approvedIds = new List<string>();
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
                return JsonResponse(@"{""results"":[
                    {""id"":501,""status"":1,""type"":""movie"",""requestedBy"":{""id"":7},""media"":{""tmdbId"":9962}},
                    {""id"":502,""status"":1,""type"":""movie"",""requestedBy"":{""id"":7},""media"":{""tmdbId"":27176}}
                ]}");

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/approve"))
            {
                approvedIds.Add(req.RequestUri.AbsolutePath);
                return JsonResponse("{\"id\":0,\"status\":2}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (approved, failed) = await client.ApprovePendingForUserAsync(7, new[] { 9962, 27176 });

        Assert.Equal(2, approved);
        Assert.Equal(0, failed);
        Assert.Contains("/api/v1/request/501/approve", approvedIds);
        Assert.Contains("/api/v1/request/502/approve", approvedIds);
    }

    /// <summary>
    /// Scope guard: a pending request belonging to someone else, or for a title outside the synced
    /// watchlist, must never be swept up. Approving another user's moderation queue would be a
    /// serious overreach.
    /// </summary>
    [Fact]
    public async Task ApprovePendingForUserAsync_IgnoresOtherUsersAndUnrelatedTitles()
    {
        var approvedIds = new List<string>();
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
                return JsonResponse(@"{""results"":[
                    {""id"":601,""status"":1,""type"":""movie"",""requestedBy"":{""id"":99},""media"":{""tmdbId"":9962}},
                    {""id"":602,""status"":1,""type"":""movie"",""requestedBy"":{""id"":7},""media"":{""tmdbId"":55555}},
                    {""id"":603,""status"":2,""type"":""movie"",""requestedBy"":{""id"":7},""media"":{""tmdbId"":9962}},
                    {""id"":604,""status"":1,""type"":""tv"",""requestedBy"":{""id"":7},""media"":{""tmdbId"":9962}},
                    {""id"":605,""status"":1,""type"":""movie"",""requestedBy"":{""id"":7},""media"":{""tmdbId"":9962}}
                ]}");

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/approve"))
            {
                approvedIds.Add(req.RequestUri.AbsolutePath);
                return JsonResponse("{\"id\":0,\"status\":2}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (approved, _) = await client.ApprovePendingForUserAsync(7, new[] { 9962 });

        // Only 605 qualifies: pending + this user + movie + in the watchlist.
        Assert.Equal(1, approved);
        Assert.Single(approvedIds);
        Assert.Contains("/api/v1/request/605/approve", approvedIds);
    }

    /// <summary>With auto-approve off the backlog is deliberately left alone.</summary>
    [Fact]
    public async Task ApprovePendingForUserAsync_AutoApproveDisabled_DoesNothing()
    {
        var calls = 0;
        var handler = new SeerrHandler(req => { calls++; return new HttpResponseMessage(HttpStatusCode.NotFound); });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler, autoApprove: false);
        var (approved, failed) = await client.ApprovePendingForUserAsync(7, new[] { 9962 });

        Assert.Equal(0, approved);
        Assert.Equal(0, failed);
        Assert.Equal(0, calls);
    }

    /// <summary>A Seerr outage during reconcile is reported, never thrown at the caller.</summary>
    [Fact]
    public async Task ApprovePendingForUserAsync_LookupFails_ReturnsZeroWithoutThrowing()
    {
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (approved, failed) = await client.ApprovePendingForUserAsync(7, new[] { 9962 });

        Assert.Equal(0, approved);
        Assert.Equal(0, failed);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task RequestMovieAsync_TitleInResponseBody_IsResolvedAtNoExtraCall()
    {
        var movieLookups = 0;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
            {
                movieLookups++;
                return JsonResponse("{\"id\":100,\"title\":\"Inception\"}");
            }

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
                return new HttpResponseMessage(HttpStatusCode.Created);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, title) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Equal("Inception", title);
        Assert.Equal(1, movieLookups); // status pre-check and title resolution share one GET
    }

    [Fact]
    public async Task RequestMovieAsync_LookupFails_TitleIsNullButRequestStillProceeds()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, title) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Null(title);
        Assert.True(posted, "a failed title lookup must not block the request itself");
    }

    [Theory]
    [InlineData(2)] // PENDING
    [InlineData(3)] // PROCESSING
    [InlineData(4)] // PARTIALLY_AVAILABLE
    [InlineData(5)] // AVAILABLE
    [InlineData(6)] // BLOCKLISTED
    public async Task RequestMovieAsync_AlreadyKnownStatus_DoesNotPost(int status)
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse($"{{\"id\":100,\"mediaInfo\":{{\"status\":{status}}}}}");

            if (req.Method == HttpMethod.Post)
                posted = true;

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
        Assert.False(posted, "request endpoint must not be called when media is already known");
    }

    [Fact]
    public async Task RequestMovieAsync_DeletedStatus_RequestsAgain()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100,\"mediaInfo\":{\"status\":7}}"); // DELETED

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.True(posted);
    }

    [Fact]
    public async Task RequestMovieAsync_PostReturns409_TreatsAsAlreadyExists()
    {
        // Belt-and-braces: if the status pre-check misses (e.g. 404 lookup) and Seerr
        // does return its own 409, we should still classify it as AlreadyExists so the
        // count of "new requests" stays accurate.
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (req.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("{\"message\":\"REQUEST_EXISTS\"}")
                };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7);

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RequestMovieAsync_Backfill_AvailableWithNoRequest_PostsAttributedRequest()
    {
        // The backfill case: media is AVAILABLE (status 5) but nobody has a request for it
        // (entered the library outside Seerr). Backfill mode must still POST so a requester
        // trail exists. Default mode would have skipped on status 5.
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100,\"mediaInfo\":{\"status\":5,\"requests\":[]}}");

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7, backfillAvailable: true);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.True(posted, "backfill should request an available title that has no request");
    }

    [Fact]
    public async Task RequestMovieAsync_Backfill_UserAlreadyHasRequest_DoesNotPost()
    {
        // Per-user dedup: if THIS user (7) already holds a request, backfill must not pile on.
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100,\"mediaInfo\":{\"status\":5,\"requests\":[{\"requestedBy\":{\"id\":7}}]}}");

            if (req.Method == HttpMethod.Post) posted = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7, backfillAvailable: true);

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
        Assert.False(posted, "backfill must not duplicate this user's own request");
    }

    [Fact]
    public async Task RequestMovieAsync_Backfill_AvailableButOtherUserRequested_PostsForThisUser()
    {
        // Another user (9) has a request; user 7 still gets their own attributed request when
        // Seerr accepts it. (If Seerr's single-active-request rule rejects it, the
        // POST 409 path classifies it AlreadyExists, covered separately.)
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100,\"mediaInfo\":{\"status\":5,\"requests\":[{\"requestedBy\":{\"id\":9}}]}}");

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7, backfillAvailable: true);

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.True(posted);
    }

    [Fact]
    public async Task RequestMovieAsync_Backfill_Blocklisted_DoesNotPost()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/movie/100"))
                return JsonResponse("{\"id\":100,\"mediaInfo\":{\"status\":6,\"requests\":[]}}"); // BLOCKLISTED

            if (req.Method == HttpMethod.Post) posted = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestMovieAsync(100, 7, backfillAvailable: true);

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
        Assert.False(posted, "blocklisted media must never be requested");
    }

    [Fact]
    public async Task GetUserWatchlistTmdbIdsAsync_PaginatesViaPageParamAndFiltersToMovies()
    {
        // Seerr paginates the user-watchlist endpoint with `page=N`, NOT take/skip
        // (which it 400s on). Drive two pages, with totalPages=2 telling us when to stop.
        var seenPageQueries = new List<string>();
        var handler = new SeerrHandler(req =>
        {
            seenPageQueries.Add(req.RequestUri!.Query);
            var path = req.RequestUri!.AbsolutePath;
            if (!path.EndsWith("/watchlist")) return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (req.RequestUri!.Query.Contains("page=1"))
                return JsonResponse(
                    "{\"page\":1,\"totalPages\":2,\"totalResults\":3,\"results\":[" +
                    "{\"tmdbId\":1,\"mediaType\":\"movie\"}," +
                    "{\"tmdbId\":\"2\",\"mediaType\":\"movie\"}," +
                    "{\"tmdbId\":3,\"mediaType\":\"tv\"}" +
                    "]}");
            if (req.RequestUri!.Query.Contains("page=2"))
                return JsonResponse(
                    "{\"page\":2,\"totalPages\":2,\"totalResults\":3,\"results\":[" +
                    "{\"tmdbId\":4,\"mediaType\":\"movie\"}" +
                    "]}");
            return JsonResponse("{\"page\":99,\"totalPages\":2,\"results\":[]}");
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var ids = await client.GetUserWatchlistTmdbIdsAsync(7);

        Assert.Equal(new HashSet<int> { 1, 2, 4 }, ids);
        Assert.Contains(seenPageQueries, q => q.Contains("page=1"));
        Assert.Contains(seenPageQueries, q => q.Contains("page=2"));
        // Crucially, no take/skip, those would 400.
        Assert.DoesNotContain(seenPageQueries, q => q.Contains("take=") || q.Contains("skip="));
    }

    [Fact]
    public async Task GetUserWatchlistTmdbIdsAsync_SendsXApiUserHeader()
    {
        // The endpoint requires acting as the user being queried, without the impersonation
        // header, Seerr returns the calling key's default user (admin), which silently
        // returns the wrong watchlist.
        string? sentHeader = null;
        var handler = new SeerrHandler(req =>
        {
            sentHeader = req.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
            return JsonResponse("{\"page\":1,\"totalPages\":1,\"totalResults\":0,\"results\":[]}");
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        await client.GetUserWatchlistTmdbIdsAsync(42);
        Assert.Equal("42", sentHeader);
    }

    [Fact]
    public async Task AddToWatchlistAsync_SendsXApiUserHeaderAndCorrectBody()
    {
        string? sentBody = null;
        string? sentHeader = null;

        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/watchlist"))
            {
                sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                sentHeader = req.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var ok = await client.AddToWatchlistAsync(42, 9);

        Assert.True(ok);
        Assert.Equal("9", sentHeader);
        Assert.Contains("\"tmdbId\":42", sentBody);
        Assert.Contains("\"mediaType\":\"movie\"", sentBody);
    }

    [Fact]
    public async Task GetJellyseerrUserIdAsync_FoundOnFirstPage_ReturnsId()
    {
        // The user-id mapping endpoint paginates with take/skip. First call returns
        // a couple of mapped users, second call returns an empty list to terminate.
        int callCount = 0;
        var handler = new SeerrHandler(req =>
        {
            callCount++;
            if (callCount == 1)
                return JsonResponse(@"{ ""results"": [
                    { ""id"": 1, ""jellyfinUserId"": ""abc123"" },
                    { ""id"": 7, ""jellyfinUserId"": ""def456"" }
                ] }");
            return JsonResponse(@"{ ""results"": [] }");
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var id = await client.GetJellyseerrUserIdAsync("def456");

        Assert.Equal(7, id);
    }

    [Fact]
    public async Task GetJellyseerrUserIdAsync_NormalisesDashesAndCase()
    {
        // Map stores normalised IDs (no dashes, lowercase). Looking up the same user
        // via either format should resolve to the same Seerr user id.
        var handler = new SeerrHandler(req =>
            JsonResponse(@"{ ""results"": [{ ""id"": 42, ""jellyfinUserId"": ""ABC123"" }] }"));

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);

        Assert.Equal(42, await client.GetJellyseerrUserIdAsync("ABC123"));
        Assert.Equal(42, await client.GetJellyseerrUserIdAsync("abc-1-2-3"));
        Assert.Equal(42, await client.GetJellyseerrUserIdAsync("abc123"));
    }

    [Fact]
    public async Task GetJellyseerrUserIdAsync_UnknownUser_ReturnsNull()
    {
        var handler = new SeerrHandler(req =>
            JsonResponse(@"{ ""results"": [{ ""id"": 1, ""jellyfinUserId"": ""onlyuser"" }] }"));

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.Null(await client.GetJellyseerrUserIdAsync("missing"));
    }

    [Fact]
    public async Task GetJellyseerrUserIdAsync_PaginatesUntilEmpty()
    {
        // Should walk pages of size 100 until an empty results array signals the end.
        // 250 users → page 0 (100), page 1 (100), page 2 (50), page 3 (empty stop).
        int callCount = 0;
        var handler = new SeerrHandler(req =>
        {
            callCount++;
            if (callCount <= 3)
            {
                var items = string.Join(",",
                    System.Linq.Enumerable.Range(0, callCount == 3 ? 50 : 100)
                        .Select(i => $"{{\"id\":{(callCount - 1) * 100 + i + 1},\"jellyfinUserId\":\"u{(callCount - 1) * 100 + i + 1}\"}}"));
                return JsonResponse($"{{ \"results\": [{items}] }}");
            }
            return JsonResponse(@"{ ""results"": [] }");
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);

        Assert.Equal(1, await client.GetJellyseerrUserIdAsync("u1"));
        Assert.Equal(150, await client.GetJellyseerrUserIdAsync("u150"));
        Assert.Equal(250, await client.GetJellyseerrUserIdAsync("u250"));
        Assert.Null(await client.GetJellyseerrUserIdAsync("u9999"));

        // After the first lookup the map is cached on the client; subsequent lookups
        // shouldn't issue more HTTP calls. The pagination short-circuits when a page
        // returns fewer than `take` results, so we don't need a fourth empty call,
        // total = 3 paged calls.
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task GetJellyseerrUserIdAsync_SkipsResultsWithoutJellyfinId()
    {
        // Seerr admin accounts can have a null jellyfinUserId; they shouldn't
        // appear in the map. Looking up a real user should still work.
        var handler = new SeerrHandler(_ =>
            JsonResponse(@"{ ""results"": [
                { ""id"": 1 },
                { ""id"": 2, ""jellyfinUserId"": null },
                { ""id"": 3, ""jellyfinUserId"": """" },
                { ""id"": 4, ""jellyfinUserId"": ""real"" }
            ] }"));

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.Equal(4, await client.GetJellyseerrUserIdAsync("real"));
        Assert.Null(await client.GetJellyseerrUserIdAsync("nonexistent"));
    }

    [Fact]
    public async Task AddToWatchlistAsync_409Conflict_IsTreatedAsSuccess()
    {
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"message\":\"already exists on watchlist\"}")
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.True(await client.AddToWatchlistAsync(42, 9));
    }

    [Fact]
    public async Task RemoveFromWatchlistAsync_404IsSuccess()
    {
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.True(await client.RemoveFromWatchlistAsync(42, 9));
    }

    [Fact]
    public async Task RemoveFromWatchlistAsync_SendsCorrectUrlAndHeader()
    {
        string? sentPath = null;
        string? sentQuery = null;
        string? sentHeader = null;

        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                sentPath = req.RequestUri!.AbsolutePath;
                sentQuery = req.RequestUri!.Query;
                sentHeader = req.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.True(await client.RemoveFromWatchlistAsync(42, 9));
        Assert.Equal("/api/v1/watchlist/42", sentPath);
        Assert.Contains("mediaType=movie", sentQuery);
        Assert.Equal("9", sentHeader);
    }

    [Fact]
    public async Task AddToWatchlistAsync_ServerError_ReturnsFalse()
    {
        // A non-conflict failure (500) is a genuine failure, not an idempotent dup.
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.False(await client.AddToWatchlistAsync(42, 9));
    }

    [Fact]
    public async Task RemoveFromWatchlistAsync_ServerError_ReturnsFalse()
    {
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.False(await client.RemoveFromWatchlistAsync(42, 9));
    }

    [Fact]
    public async Task GetUserWatchlistTmdbIdsAsync_FetchFails_ReturnsEmpty()
    {
        // A failed page fetch must not throw; it returns whatever was gathered (nothing).
        var handler = new SeerrHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("nope")
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var ids = await client.GetUserWatchlistTmdbIdsAsync(9);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetMovieMediaStatusAsync_TransportThrows_ReturnsNull()
    {
        // Network-layer exceptions are swallowed so a status pre-check never breaks a run.
        var handler = new SeerrHandler(_ => throw new HttpRequestException("connection reset"));

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        Assert.Null(await client.GetMovieMediaStatusAsync(100));
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // ----- RequestSeriesAsync (TV): outcome classification -----
    // These pin the three body-classification paths of a 2xx response (per-season
    // detail, media-level fallback, unparseable body) plus the 409 and failure
    // paths. Wrong classification either re-requests content forever or silently
    // hides a real grab from the caller's tally.

    private static async Task<(SeerrClient.RequestResult Result, string? PostedBody)> RunSeriesRequest(
        string responseBody, System.Net.HttpStatusCode status, IReadOnlyList<int> seasons)
    {
        string? postedBody = null;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                postedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, _) = await client.RequestSeriesAsync(500, 7, seasons);
        return (result, postedBody);
    }

    [Fact]
    public async Task RequestSeries_NewShow_PostsSeasonsAndReturnsRequested()
    {
        var (result, body) = await RunSeriesRequest(
            "{\"media\":{\"seasons\":[{\"seasonNumber\":1,\"status\":2},{\"seasonNumber\":2,\"status\":2}]}}",
            HttpStatusCode.Created, new[] { 1, 2 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Contains("\"mediaType\":\"tv\"", body);
        Assert.Contains("\"seasons\":[1,2]", body);
    }

    [Fact]
    public async Task RequestSeriesAsync_NameInLookupBody_IsResolvedAsTitle()
    {
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/tv/500"))
                return JsonResponse("{\"id\":500,\"name\":\"Better Call Saul\"}");

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, title) = await client.RequestSeriesAsync(500, 7, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Equal("Better Call Saul", title);
    }

    [Fact]
    public async Task RequestSeriesAsync_LookupFails_TitleIsNullButRequestStillProceeds()
    {
        var posted = false;
        var handler = new SeerrHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/tv/500"))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"))
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        var (result, title) = await client.RequestSeriesAsync(500, 7, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
        Assert.Null(title);
        Assert.True(posted, "a failed title lookup must not block the request itself");
    }

    [Fact]
    public async Task RequestSeries_NoSeasonsGiven_RequestsAll()
    {
        var (_, body) = await RunSeriesRequest("{}", HttpStatusCode.Created, Array.Empty<int>());

        Assert.Contains("\"seasons\":\"all\"", body);
    }

    [Fact]
    public async Task RequestSeries_AllRequestedSeasonsAvailable_ClassifiedAsNoOp()
    {
        // Jellyseerr returns 2xx even when everything is already AVAILABLE (status 5).
        var (result, _) = await RunSeriesRequest(
            "{\"media\":{\"seasons\":[{\"seasonNumber\":1,\"status\":5},{\"seasonNumber\":2,\"status\":5}]}}",
            HttpStatusCode.OK, new[] { 1, 2 });

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RequestSeries_OneSeasonNotAvailable_StillCountsAsRequested()
    {
        var (result, _) = await RunSeriesRequest(
            "{\"media\":{\"seasons\":[{\"seasonNumber\":1,\"status\":5},{\"seasonNumber\":2,\"status\":3}]}}",
            HttpStatusCode.OK, new[] { 1, 2 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
    }

    [Fact]
    public async Task RequestSeries_MediaLevelStatusFallback_WhenNoSeasonDetail()
    {
        var (result, _) = await RunSeriesRequest(
            "{\"media\":{\"status\":5}}", HttpStatusCode.OK, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RequestSeries_UnparseableBody_TreatedAsRealRequest()
    {
        // Parse failure must never hide a genuine grab; classification falls back
        // to Requested.
        var (result, _) = await RunSeriesRequest("not json at all", HttpStatusCode.OK, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.Requested, result);
    }

    [Fact]
    public async Task RequestSeries_Conflict409_ReturnsAlreadyExists()
    {
        var (result, _) = await RunSeriesRequest("{\"message\":\"Request for this media already exists\"}",
            HttpStatusCode.Conflict, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.AlreadyExists, result);
    }

    [Fact]
    public async Task RequestSeries_ServerError_ReturnsFailed()
    {
        var (result, _) = await RunSeriesRequest("{\"message\":\"boom\"}",
            HttpStatusCode.InternalServerError, new[] { 1 });

        Assert.Equal(SeerrClient.RequestResult.Failed, result);
    }

    /// <summary>
    /// A small model of Seerr's request endpoints, following seerr-team/seerr:
    /// server/middleware/auth.ts (an API-key call acts as user 1, or as the X-API-User id),
    /// server/entity/MediaRequest.ts (a body userId needs Manage Users and Manage Requests; the
    /// requester needs request permission; a new request is APPROVED only when the ACTING user
    /// holds Auto-Approve, the media type's Auto-Approve or Manage Requests) and
    /// server/routes/request.ts (approve needs Manage Requests; permission errors are 403).
    /// Admin (2) passes every check, as Seerr's hasPermission does. The movie and TV lookups
    /// answer with no media info, so every title is new.
    /// </summary>
    private sealed class FakeSeerr : HttpMessageHandler
    {
        private const int AdminBit = 2;
        private const int AutoApproveMovieBit = 256;
        private const int AutoApproveTvBit = 512;
        private const int RequestMovieBit = 262144;
        private const int RequestTvBit = 524288;

        public Dictionary<int, int> Users { get; } = new() { [1] = AdminBit };
        public List<(string? ApiUser, string Body)> RequestPosts { get; } = new();
        public List<(int Id, int RequestedBy, int Status)> Created { get; } = new();
        public int ApproveCalls { get; private set; }
        public bool ApproveFails { get; set; }

        private bool Has(int userId, params int[] any)
        {
            var value = Users.TryGetValue(userId, out var v) ? v : 0;
            if ((value & AdminBit) != 0) return true;
            foreach (var p in any)
                if ((value & p) != 0) return true;
            return false;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var apiUser = request.Headers.TryGetValues("X-API-User", out var v) ? string.Join(",", v) : null;
            var acting = apiUser == null ? 1 : int.Parse(apiUser, System.Globalization.CultureInfo.InvariantCulture);

            if (request.Method == HttpMethod.Get && (path.Contains("/api/v1/movie/") || path.Contains("/api/v1/tv/")))
                return JsonResponse("{\"id\":1}");

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/request"))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                RequestPosts.Add((apiUser, body));
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var isTv = doc.RootElement.GetProperty("mediaType").GetString() == "tv";

                var requester = acting;
                if (doc.RootElement.TryGetProperty("userId", out var uid))
                {
                    if (!(Has(acting, SeerrManageUsers) && Has(acting, SeerrManageRequests)))
                        return Forbidden("You do not have permission to modify the request user.");
                    requester = uid.GetInt32();
                }

                if (!Has(requester, SeerrRequest, isTv ? RequestTvBit : RequestMovieBit))
                    return Forbidden("You do not have permission to make requests.");

                var approved = Has(acting, SeerrAutoApprove, isTv ? AutoApproveTvBit : AutoApproveMovieBit, SeerrManageRequests);
                var id = 40 + Created.Count;
                var status = approved ? 2 : 1;
                Created.Add((id, requester, status));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"id\":" + id + ",\"status\":" + status + "}", System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/approve"))
            {
                ApproveCalls++;
                if (ApproveFails || !Has(acting, SeerrManageRequests))
                    return Forbidden("You do not have permission to access this endpoint");
                var id = int.Parse(path.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);
                var i = Created.FindIndex(c => c.Id == id);
                if (i < 0) return new HttpResponseMessage(HttpStatusCode.NotFound);
                Created[i] = (id, Created[i].RequestedBy, 2);
                return JsonResponse("{\"id\":" + id + ",\"status\":2}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Forbidden(string message)
            => new(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"" + message + "\"}", System.Text.Encoding.UTF8, "application/json")
            };
    }


    [Fact]
    public async Task InjectedHandler_IsUsed_AndEveryRequestCarriesTheApiKey()
    {
        var seen = new List<HttpRequestMessage>();
        var handler = new SeerrHandler(req =>
        {
            seen.Add(req);
            if (req.Method == HttpMethod.Get)
                return JsonResponse("{\"id\":100}");
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        using var client = new SeerrClient(BaseUrl, ApiKey, NullLogger.Instance, handler);
        await client.RequestMovieAsync(100, 7);
        await client.AddToWatchlistAsync(100, 7);

        Assert.Equal(3, seen.Count);
        Assert.All(seen, req =>
        {
            Assert.Equal(ApiKey, Assert.Single(req.Headers.GetValues("X-Api-Key")));
            Assert.Contains(req.Headers.Accept, a => a.MediaType == "application/json");
        });
        Assert.Equal("7", Assert.Single(seen[2].Headers.GetValues("X-API-User")));
    }

    [Fact]
    public void ProductionClients_ShareOneHttpClient_WithoutAnApiKeyDefault_AndDisposeLeavesItUsable()
    {
        var first = new SeerrClient(BaseUrl, "key-a", NullLogger.Instance);
        var shared = first.HttpForTesting;
        first.Dispose();

        using var second = new SeerrClient("http://other.test", "key-b", NullLogger.Instance);

        Assert.Same(shared, second.HttpForTesting);
        Assert.False(shared.DefaultRequestHeaders.Contains("X-Api-Key"));
        shared.CancelPendingRequests();
    }

    private class SeerrHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public SeerrHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
