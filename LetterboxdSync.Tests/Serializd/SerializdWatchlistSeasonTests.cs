using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

/// <summary>
/// Pins IncompleteWatchlistedSeasons: its virtual-item and aired-date rules decide
/// whether a season is re-requested on Seerr every sync, so a wrong answer either
/// requests forever (unaired episodes counted as missing) or never backfills a hole.
/// </summary>
public class SerializdWatchlistSeasonTests
{
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly SerializdWatchlistSyncRunner _runner;
    private readonly User _user = new("lachlan", "test-provider-id", "test-reset-id");
    private readonly Series _series = new() { Name = "Sample Show" };

    public SerializdWatchlistSeasonTests()
    {
        _runner = new SerializdWatchlistSyncRunner(
            NullLoggerFactory.Instance,
            _libraryManager,
            Substitute.For<IUserManager>(),
            Substitute.For<ICollectionManager>(),
            Substitute.For<IPlaylistManager>());
    }

    private static Episode Ep(int season, int number, bool onDisk, DateTime? premiere = null)
        => new()
        {
            Name = $"S{season:00}E{number:00}",
            ParentIndexNumber = season,
            IndexNumber = number,
            IsVirtualItem = !onDisk,
            // Aired unless the caller says otherwise; virtual episodes only count
            // toward a season's total once their premiere date has passed.
            PremiereDate = premiere ?? DateTime.UtcNow.AddYears(-1)
        };

    private void LibraryHas(params Episode[] eps)
        => _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem>(eps));

    [Fact]
    public void CompleteSeason_NotListed()
    {
        LibraryHas(Ep(1, 1, onDisk: true), Ep(1, 2, onDisk: true));

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, new[] { 1 });

        Assert.False(shouldRequest);
        Assert.Empty(seasons);
    }

    [Fact]
    public void MissingAiredEpisode_MakesSeasonIncomplete()
    {
        LibraryHas(Ep(1, 1, onDisk: true), Ep(1, 2, onDisk: false)); // aired but no file

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, new[] { 1 });

        Assert.True(shouldRequest);
        Assert.Equal(new[] { 1 }, seasons);
    }

    [Fact]
    public void UnairedMissingEpisode_DoesNotMakeOngoingSeasonIncomplete()
    {
        // The finale hasn't aired: it isn't grabbable, so it must not trigger a
        // pointless re-request every sync until it airs.
        LibraryHas(
            Ep(1, 1, onDisk: true),
            Ep(1, 2, onDisk: false, premiere: DateTime.UtcNow.AddMonths(1)));

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, new[] { 1 });

        Assert.False(shouldRequest);
        Assert.Empty(seasons);
    }

    [Fact]
    public void SeasonUnknownToJellyfin_TreatedAsIncomplete()
    {
        LibraryHas(Ep(1, 1, onDisk: true)); // library only knows season 1

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, new[] { 1, 3 });

        Assert.True(shouldRequest);
        Assert.Equal(new[] { 3 }, seasons);
    }

    [Fact]
    public void WholeShowWatchlist_ChecksEverySeasonExceptSpecials()
    {
        LibraryHas(
            Ep(0, 1, onDisk: false),               // specials: excluded from defaults
            Ep(1, 1, onDisk: true),                // complete
            Ep(2, 1, onDisk: false));              // incomplete, aired

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, Array.Empty<int>());

        Assert.True(shouldRequest);
        Assert.Equal(new[] { 2 }, seasons);
    }

    [Fact]
    public void WholeShowWatchlist_ZeroEpisodesIndexed_RequestsWholeShow()
    {
        // A Series item exists (e.g. an empty placeholder, or every episode file was
        // removed after the initial scan) but Jellyfin has zero episodes indexed for
        // it at all. There is no season metadata to enumerate, so this must not be
        // silently treated as "every season complete": it must ask for the whole show.
        LibraryHas();

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, Array.Empty<int>());

        Assert.True(shouldRequest);
        Assert.Empty(seasons); // empty seasons list = Seerr requests "all"
    }

    [Fact]
    public void ExplicitSeasonsWatchlisted_ZeroEpisodesIndexed_StillTreatedAsIncomplete()
    {
        // Same zero-episode series, but specific seasons were watchlisted: the existing
        // "unknown season = incomplete" rule already covers this without the new
        // whole-show special case, since targets come from watchlistedSeasons directly.
        LibraryHas();

        var (shouldRequest, seasons) = _runner.IncompleteWatchlistedSeasons(_user, _series, new[] { 1, 2 });

        Assert.True(shouldRequest);
        Assert.Equal(new[] { 1, 2 }, seasons);
    }
}
