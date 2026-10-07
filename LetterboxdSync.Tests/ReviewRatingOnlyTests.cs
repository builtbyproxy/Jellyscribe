using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// A review with only a star rating (no text, not a rewatch). Letterboxd has no empty diary review,
/// so the endpoint sets the member's film rating instead, as RatingSyncHandler does. The mocked
/// service models both real implementations: the lookup returns the instance's own film id (a LID
/// on the API path), and only that id is valid for SetFilmRatingAsync.
/// </summary>
[Collection("Plugin")]
public class ReviewRatingOnlyTests : IDisposable
{
    private const string UserId = "0123456789abcdef0123456789abcdef";
    private readonly string _historyPath = Path.Combine(Path.GetTempPath(), "lbs-review-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly string _pushPath = Path.Combine(Path.GetTempPath(), "lbs-pushes-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public ReviewRatingOnlyTests()
    {
        SyncHistory.DataPathOverride = _historyPath;
        SyncHistory.ResetForTesting();
        RatingPushStore.DataPathOverride = _pushPath;
        RatingPushStore.ResetForTesting();
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        RatingPushStore.DataPathOverride = null;
        RatingPushStore.ResetForTesting();
        try { File.Delete(_historyPath); } catch { }
        try { File.Delete(_pushPath); } catch { }
    }

    private static object? Prop(IActionResult result, string name)
    {
        var value = ((ObjectResult)result).Value!;
        return value.GetType().GetProperty(name)?.GetValue(value);
    }

    private static ILetterboxdService ApiLikeService()
    {
        var service = Substitute.For<ILetterboxdService>();
        service.LookupFilmByTmdbIdAsync(238).Returns(new FilmResult("the-godfather", "2aMS", null));
        return service;
    }

    [Fact]
    public async Task RatingOnly_SetsTheFilmRating_AndPostsNoDiaryEntry()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var service = ApiLikeService();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "the-godfather",
            Rating = 4.5,
            TmdbId = 238,
            Title = "The Godfather"
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(true, Prop(result, "ratedOnly"));
        await service.Received(1).SetFilmRatingAsync("the-godfather", "2aMS", 4.5);
        await service.DidNotReceiveWithAnyArgs().PostReviewAsync(default!, default, default, default, default, default, default);
        await service.DidNotReceiveWithAnyArgs().MarkAsWatchedAsync(default!, default!, default, default, default, default, default);
    }

    [Fact]
    public async Task RatingOnly_IsRecordedAsRated_AndAsTheLastPushedRating()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var service = ApiLikeService();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", Rating = 4, TmdbId = 238, Title = "The Godfather" });

        var evt = Assert.Single(SyncHistory.GetPage(0, 10).Events);
        Assert.Equal(SyncStatus.Rated, evt.Status);
        Assert.Equal(SyncEventSources.Rating, evt.Source);
        Assert.Equal("The Godfather · Rated 4 stars", evt.FilmTitle);
        Assert.Equal(238, evt.TmdbId);
        Assert.Equal("demo-cinephile", evt.Account);
        // RatingSyncHandler compares against this, so it will not push the same rating again.
        Assert.Equal(4.0, RatingPushStore.GetLastPushed(UserId, "demo-cinephile", 238));
    }

    [Fact]
    public async Task RatingOnly_Failure_ReturnsTheServiceError_AndRecordsNoFailedEvent()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var service = ApiLikeService();
        service.SetFilmRatingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .ThrowsAsync(new Exception("Letterboxd rejected rating 4.5 for the-godfather: InvalidRatingValue"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", Rating = 4.5, TmdbId = 238 });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("InvalidRatingValue", (string)Prop(result, "error")!);
        // Failed rows feed the diary sync's per-film rules; a rating that did not land leaves them alone.
        Assert.Empty(SyncHistory.GetPage(0, 10).Events);
        Assert.Null(RatingPushStore.GetLastPushed(UserId, "demo-cinephile", 238));
        // Nothing landed on Letterboxd, so the Jellyfin rating is left alone too.
        h.UserDataManager.DidNotReceiveWithAnyArgs().SaveUserData(default!, default!, default!, default, default);
    }

    [Theory]
    [InlineData(4.3)]
    [InlineData(0)]
    [InlineData(5.5)]
    [InlineData(double.NaN)]
    public async Task RatingOnly_OffTheHalfStarScale_IsRefusedBeforeAnyLogin(double rating)
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var logins = 0;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => { logins++; return Task.FromResult(ApiLikeService()); };

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", Rating = rating, TmdbId = 238 });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, logins);
    }

    [Fact]
    public async Task RatingOnly_Failure_ReplyCarriesOneShortLine()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var service = ApiLikeService();
        service.SetFilmRatingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>())
            .ThrowsAsync(new Exception("Failed to rate the-godfather: Forbidden\n<html>" + new string('x', 400) + "</html>"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", Rating = 3, TmdbId = 238 });

        var error = (string)Prop(result, "error")!;
        Assert.StartsWith("Failed to rate the-godfather: Forbidden", error);
        Assert.DoesNotContain("\n", error);
        Assert.True(error.Length <= 161, error);
    }

    [Fact]
    public async Task DiaryReviewFailure_ReplyCarriesOneShortLine_Too()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var service = ApiLikeService();
        service.PostReviewAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<double?>(), Arg.Any<int?>())
            .ThrowsAsync(new Exception("Review failed: 403\r\n<html>" + new string('x', 400) + "</html>"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", ReviewText = "great", TmdbId = 238 });

        var error = (string)Prop(result, "error")!;
        Assert.StartsWith("Review failed: 403", error);
        Assert.DoesNotContain("\n", error);
        Assert.True(error.Length <= 161, error);
    }

    [Fact]
    public async Task FilmSlugWithAControlCharacter_IsRefusedBeforeAnyLogin()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var logins = 0;
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => { logins++; return Task.FromResult(ApiLikeService()); };

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather\r\nFAKE LOG LINE", ReviewText = "great", TmdbId = 238 });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, logins);
    }

    [Fact]
    public async Task RatingOnly_Title_IsStoredAsOnePlainLine()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        var service = ApiLikeService();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);

        await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", Rating = 4, TmdbId = 238, Title = "The\r\nGodfather · Success" });

        Assert.Equal("The Godfather - Success · Rated 4 stars", Assert.Single(SyncHistory.GetPage(0, 10).Events).FilmTitle);
    }

    [Fact]
    public async Task RatingOnly_WithoutATmdbId_IsRefused()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", Rating = 4 });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("TMDb", (string)Prop(result, "error")!);
    }

    [Fact]
    public async Task ReviewToTwoAccounts_OneFailing_ReportsEachAccount()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "demo-cinephile");
        h.AddAccount(UserId, "demo-second");
        var good = ApiLikeService();
        var bad = ApiLikeService();
        bad.PostReviewAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<double?>(), Arg.Any<int?>())
            .ThrowsAsync(new Exception("Cloudflare 403"));
        LetterboxdServiceFactory.OverrideForTesting = (user, _, _, _, _) => Task.FromResult(user == "demo-second" ? bad : good);

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "the-godfather", ReviewText = "great", TmdbId = 238 });

        Assert.IsType<OkObjectResult>(result);
        var accounts = ((IEnumerable)Prop(result, "accounts")!).Cast<object>().ToList();
        Assert.Equal(2, accounts.Count);
        var failed = accounts.Single(a => (bool)a.GetType().GetProperty("success")!.GetValue(a)! == false);
        Assert.Equal("demo-second", failed.GetType().GetProperty("letterboxdUsername")!.GetValue(failed));
        Assert.Equal("Cloudflare 403", failed.GetType().GetProperty("error")!.GetValue(failed));
    }
}
