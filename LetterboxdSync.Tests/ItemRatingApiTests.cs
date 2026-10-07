using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Tests for GET ItemRating: the review modal's pre-fill source. The endpoint
/// must always answer 200 with null fields for anything it can't resolve, so
/// the modal never breaks on the lookup; only a missing user identity is an error.
/// </summary>
[Collection("Plugin")]
public class ItemRatingApiTests
{
    private static (double? Rating, double? Stars) ReadPayload(ActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var t = ok.Value!.GetType();
        var rating = (double?)t.GetProperty("rating", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value);
        var stars = (double?)t.GetProperty("stars", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value);
        return (rating, stars);
    }

    /// <summary>
    /// ControllerTestHarness.SetUsers can't proxy the concrete User class, so we
    /// build a real User (same pattern as LetterboxdControllerTests) and derive
    /// the harness's currentUserId from its generated Id.
    /// </summary>
    private static (ControllerTestHarness Harness, User User) MakeHarness()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var h = new ControllerTestHarness(currentUserId: user.Id.ToString("N"));
        h.UserManager.GetUsers().Returns(new List<User> { user });
        return (h, user);
    }

    [Fact]
    public void RatedMovie_ReturnsRatingAndHalfStars()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var movie = MakeMovie(603);
        SetLibrary(h, movie);
        h.UserDataManager.GetUserData(user, movie).Returns(new UserItemData { Key = "k", Rating = 7 });

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 603));

        Assert.Equal(7, rating);
        Assert.Equal(3.5, stars);
    }

    [Fact]
    public void UnratedMovie_ReturnsNulls()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var movie = MakeMovie(603);
        SetLibrary(h, movie);
        h.UserDataManager.GetUserData(user, movie).Returns(new UserItemData { Key = "k" });

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 603));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void MissingOrNonPositiveTmdbId_ReturnsNulls(int? tmdbId)
    {
        var (h, _) = MakeHarness();
        using var __ = h;

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: tmdbId));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Fact]
    public void UnknownTmdbId_ReturnsNulls()
    {
        var (h, _) = MakeHarness();
        using var __ = h;
        SetLibrary(h, MakeMovie(603));

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 550));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Fact]
    public void EpisodeRating_ResolvedBySeriesTmdbAndNumbers()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        var episode = new Episode { Name = "Ozymandias", ParentIndexNumber = 5, IndexNumber = 14, Id = System.Guid.NewGuid(), SeriesId = series.Id };
        SetLibrary(h, series, episode);
        h.UserDataManager.GetUserData(user, episode).Returns(new UserItemData { Key = "k", Rating = 8 });

        var (rating, stars) = ReadPayload(
            h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 5, episodeNumber: 14));

        Assert.Equal(8, rating);
        Assert.Equal(4, stars);
    }

    // The library stub ignores the query's filters, so this is the "looser query" case: an
    // episode with the right numbers from another show must never be taken for this one.
    [Fact]
    public void EpisodeOfAnotherSeries_WithTheSameNumbers_IsNeverResolved()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        var other = new Episode { Name = "Other show", ParentIndexNumber = 5, IndexNumber = 14, Id = System.Guid.NewGuid(), SeriesId = System.Guid.NewGuid() };
        SetLibrary(h, series, other);
        h.UserDataManager.GetUserData(user, other).Returns(new UserItemData { Key = "k", Rating = 8 });

        var (rating, _) = ReadPayload(
            h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 5, episodeNumber: 14));

        Assert.Null(rating);
    }

    [Fact]
    public void ShowLevelRating_ResolvedFromSeries()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        SetLibrary(h, series);
        h.UserDataManager.GetUserData(user, series).Returns(new UserItemData { Key = "k", Rating = 9 });

        var (rating, stars) = ReadPayload(h.Controller.GetItemRating(tmdbId: 1396, isShow: true));

        Assert.Equal(9, rating);
        Assert.Equal(4.5, stars);
    }

    [Fact]
    public void EpisodeRequested_ButOnlySeriesRated_ReturnsNulls()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        var episode = new Episode { Name = "Pilot", ParentIndexNumber = 1, IndexNumber = 1, Id = System.Guid.NewGuid() };
        SetLibrary(h, series, episode);
        h.UserDataManager.GetUserData(user, series).Returns(new UserItemData { Key = "k", Rating = 9 });

        var (rating, stars) = ReadPayload(
            h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 1, episodeNumber: 1));

        Assert.Null(rating);
        Assert.Null(stars);
    }

    [Fact]
    public void MovieLookup_FiltersByTmdbIdInTheQuery_ForTheCallingUser()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var queries = CaptureQueries(h);

        h.Controller.GetItemRating(tmdbId: 603);

        var query = Assert.Single(queries);
        Assert.Same(user, query.User);
        Assert.Equal(new[] { BaseItemKind.Movie }, query.IncludeItemTypes);
        Assert.Equal("603", query.HasAnyProviderId?[MetadataProvider.Tmdb.ToString()]);
    }

    [Fact]
    public void EpisodeLookup_ResolvesSeriesByTmdbId_ThenQueriesItsEpisodesByNumber()
    {
        var (h, user) = MakeHarness();
        using var _ = h;
        var series = MakeSeries(1396);
        SetLibrary(h, series);
        var queries = CaptureQueries(h);

        h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 5, episodeNumber: 14);

        Assert.Equal(2, queries.Count);
        Assert.Equal(new[] { BaseItemKind.Series }, queries[0].IncludeItemTypes);
        Assert.Equal("1396", queries[0].HasAnyProviderId?[MetadataProvider.Tmdb.ToString()]);
        Assert.Same(user, queries[1].User);
        Assert.Equal(new[] { BaseItemKind.Episode }, queries[1].IncludeItemTypes);
        Assert.Equal(new[] { series.Id }, queries[1].AncestorIds);
        Assert.Equal(5, queries[1].ParentIndexNumber);
        Assert.Equal(14, queries[1].IndexNumber);
    }

    [Fact]
    public void EpisodeLookup_SeriesNotInLibrary_SkipsTheEpisodeQuery()
    {
        var (h, _) = MakeHarness();
        using var __ = h;
        var queries = CaptureQueries(h);

        var (rating, _) = ReadPayload(
            h.Controller.GetItemRating(tmdbId: 1396, isShow: true, seasonNumber: 5, episodeNumber: 14));

        Assert.Null(rating);
        Assert.Equal(new[] { BaseItemKind.Series }, Assert.Single(queries).IncludeItemTypes);
    }

    [Fact]
    public void NoUserIdentity_IsRejected()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.GetItemRating(tmdbId: 603);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static Movie MakeMovie(int tmdbId, string name = "Sinners")
    {
        var movie = new Movie { Name = name, Id = System.Guid.NewGuid() };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return movie;
    }

    private static Series MakeSeries(int tmdbId, string name = "Breaking Bad")
    {
        var series = new Series { Name = name, Id = System.Guid.NewGuid() };
        series.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        return series;
    }

    private static List<InternalItemsQuery> CaptureQueries(ControllerTestHarness h)
    {
        var queries = new List<InternalItemsQuery>();
        h.LibraryManager.When(m => m.GetItemList(Arg.Any<InternalItemsQuery>()))
            .Do(call => queries.Add(call.Arg<InternalItemsQuery>()));
        return queries;
    }

    /// <summary>
    /// Sets the harness's mocked library list. (ControllerTestHarness.AddMovie substitutes
    /// Movie and stubs GetProviderId, which NSubstitute can't intercept; real entities avoid
    /// that.) All items go in one call: re-reading the stub to append keeps only the last item.
    /// </summary>
    private static void SetLibrary(ControllerTestHarness h, params BaseItem[] items)
        => h.LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>(items));
}
