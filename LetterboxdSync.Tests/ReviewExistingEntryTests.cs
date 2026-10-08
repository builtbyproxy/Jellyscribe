using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// POST Review for a film the plugin already logged: the review goes onto that diary entry instead
/// of a second entry dated today. The mocked service answers as the real ones do: the API client
/// reports Attached, NoEntry or AlreadyReviewed (see ApiClientReviewExistingEntryTests), and the
/// website session keeps the interface default, Unsupported.
/// </summary>
[Collection("Plugin")]
public class ReviewExistingEntryTests : IDisposable
{
    private const string UserId = "0123456789abcdef0123456789abcdef";
    // The harness has no Jellyfin users, so the controller names the caller by user id.
    private const string JellyfinUser = UserId;
    private const string Account = "demo-cinephile";
    private const int TmdbId = 389;
    private static readonly DateTime Watched = new(2024, 3, 9);
    private readonly string _historyPath = Path.Combine(Path.GetTempPath(), "lbs-review-entry-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public ReviewExistingEntryTests()
    {
        SyncHistory.DataPathOverride = _historyPath;
        SyncHistory.ResetForTesting();
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.OverrideForTesting = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        try { File.Delete(_historyPath); } catch { }
    }

    private static object? Prop(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);

    private static object AccountResult(IActionResult result)
        => ((System.Collections.IEnumerable)Prop(((ObjectResult)result).Value!, "accounts")!).Cast<object>().Single();

    private ControllerTestHarness Harness(ILetterboxdService service)
    {
        var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, Account);
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => Task.FromResult(service);
        return h;
    }

    private static void LoggedBySync(string account = Account, DateTime? viewed = null)
        => SyncHistory.Record(new SyncEvent
        {
            FilmTitle = "12 Angry Men",
            FilmSlug = "12-angry-men",
            TmdbId = TmdbId,
            Username = JellyfinUser,
            Account = account,
            Timestamp = DateTime.UtcNow.AddDays(-1),
            ViewingDate = viewed ?? Watched,
            Status = SyncStatus.Success,
        });

    private static ILetterboxdService Service(ReviewAttachResult outcome)
    {
        var service = Substitute.For<ILetterboxdService>();
        service.AddReviewToDiaryEntryAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<double?>())
            .Returns(outcome);
        return service;
    }

    private static ReviewRequest Review(bool rewatch = false, string? date = null) => new()
    {
        FilmSlug = "12-angry-men",
        ReviewText = "A tense room.",
        ContainsSpoilers = true,
        Rating = 4.5,
        TmdbId = TmdbId,
        IsRewatch = rewatch,
        Date = date,
    };

    private static SyncEvent ReviewRow() => SyncHistory.GetPage(0, 50).Events.Single(e => e.Source == "review");

    [Fact]
    public async Task LoggedFilm_ReviewGoesOnThatEntry_AndNoNewEntryIsPosted()
    {
        var service = Service(ReviewAttachResult.Attached);
        using var h = Harness(service);
        LoggedBySync();

        var result = await h.Controller.PostReview(Review());

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).AddReviewToDiaryEntryAsync(TmdbId, Watched, "A tense room.", true, 4.5);
        await service.DidNotReceiveWithAnyArgs().PostReviewAsync(default!, default, default, default, default, default, default);
        Assert.Equal(true, Prop(AccountResult(result), "addedToEntry"));
        Assert.Null(Prop(AccountResult(result), "note"));

        var row = ReviewRow();
        Assert.Equal(TmdbId, row.TmdbId);
        Assert.Equal(Watched, row.ViewingDate);
        Assert.Equal(Account, row.Account);
        Assert.Equal(SyncStatus.Success, row.Status);
    }

    [Fact]
    public async Task LoggedFilm_NoEntryOnLetterboxd_IsPostedOnTheViewingDate_NotToday()
    {
        var service = Service(ReviewAttachResult.NoEntry);
        using var h = Harness(service);
        LoggedBySync();

        var result = await h.Controller.PostReview(Review());

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).PostReviewAsync("12-angry-men", "A tense room.", true, false, "2024-03-09", 4.5, TmdbId);
        Assert.Equal(false, Prop(AccountResult(result), "addedToEntry"));
        Assert.Contains("2024-03-09", (string)Prop(AccountResult(result), "note")!);
        Assert.Equal(Watched, ReviewRow().ViewingDate);
    }

    [Fact]
    public async Task WebsiteSession_CannotEditAnEntry_PostsOnTheViewingDate_AndSaysSo()
    {
        // No setup for AddReviewToDiaryEntryAsync: the website session's answer is the interface default.
        var service = Substitute.For<ILetterboxdService>();
        using var h = Harness(service);
        LoggedBySync();

        var result = await h.Controller.PostReview(Review());

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).PostReviewAsync("12-angry-men", "A tense room.", true, false, "2024-03-09", 4.5, TmdbId);
        var note = (string)Prop(AccountResult(result), "note")!;
        Assert.Contains("website login", note);
        Assert.Contains("2024-03-09", note);
    }

    [Fact]
    public async Task EntryAlreadyReviewed_IsLeftAlone_AndReportedWithoutAFailedRow()
    {
        var service = Service(ReviewAttachResult.AlreadyReviewed);
        using var h = Harness(service);
        LoggedBySync();

        var result = await h.Controller.PostReview(Review());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("already has a review", (string)Prop(bad.Value!, "error")!);
        await service.DidNotReceiveWithAnyArgs().PostReviewAsync(default!, default, default, default, default, default, default);
        // Only the sync's own row: nothing was sent, so the film's diary sync rules see no failure.
        Assert.Single(SyncHistory.GetPage(0, 50).Events);
    }

    [Fact]
    public async Task Rewatch_IsAlwaysANewEntry()
    {
        var service = Service(ReviewAttachResult.Attached);
        using var h = Harness(service);
        LoggedBySync();

        await h.Controller.PostReview(Review(rewatch: true, date: "2026-05-01"));

        await service.DidNotReceiveWithAnyArgs().AddReviewToDiaryEntryAsync(default, default, default!, default, default);
        await service.Received(1).PostReviewAsync("12-angry-men", "A tense room.", true, true, "2026-05-01", 4.5, TmdbId);
        var row = ReviewRow();
        Assert.Equal(SyncStatus.Rewatch, row.Status);
        Assert.Equal(new DateTime(2026, 5, 1), row.ViewingDate);
    }

    [Fact]
    public async Task AnotherAccountsEntry_IsNotUsed()
    {
        var service = Service(ReviewAttachResult.Attached);
        using var h = Harness(service);
        LoggedBySync(account: "someone-else");

        await h.Controller.PostReview(Review());

        await service.DidNotReceiveWithAnyArgs().AddReviewToDiaryEntryAsync(default, default, default!, default, default);
        await service.Received(1).PostReviewAsync("12-angry-men", "A tense room.", true, false,
            Arg.Is<string?>(d => d == DateTime.Now.ToString("yyyy-MM-dd")), 4.5, TmdbId);
    }

    [Fact]
    public async Task NeverLogged_IsANewEntryToday_RecordedWithItsFilmAndDate()
    {
        var service = Service(ReviewAttachResult.Attached);
        using var h = Harness(service);

        await h.Controller.PostReview(Review());

        await service.DidNotReceiveWithAnyArgs().AddReviewToDiaryEntryAsync(default, default, default!, default, default);
        var row = ReviewRow();
        Assert.Equal(TmdbId, row.TmdbId);
        Assert.Equal(DateTime.Now.Date, row.ViewingDate);
        Assert.Equal(Account, row.Account);
        // The next sync's duplicate check now sees today's entry.
        Assert.True(SyncHistory.WasSuccessfullySynced(JellyfinUser, TmdbId, DateTime.Now.Date, Account));
    }

    [Fact]
    public async Task RatingOnlyReview_StillSetsTheFilmRating_WithoutTouchingTheEntry()
    {
        var service = Service(ReviewAttachResult.Attached);
        service.LookupFilmByTmdbIdAsync(TmdbId).Returns(new FilmResult("12-angry-men", "2b8k", null));
        using var h = Harness(service);
        LoggedBySync();

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "12-angry-men", Rating = 4, TmdbId = TmdbId });

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).SetFilmRatingAsync("12-angry-men", "2b8k", 4);
        await service.DidNotReceiveWithAnyArgs().AddReviewToDiaryEntryAsync(default, default, default!, default, default);
        await service.DidNotReceiveWithAnyArgs().PostReviewAsync(default!, default, default, default, default, default, default);
    }
}
