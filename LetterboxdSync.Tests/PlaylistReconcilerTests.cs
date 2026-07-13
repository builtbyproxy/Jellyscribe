using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Direct coverage of the shared PlaylistReconciler, extracted from
/// WatchlistSyncRunner.UpdatePlaylistAsync and SerializdWatchlistSyncRunner.ReconcilePlaylistAsync.
/// A bug here would silently affect both the Letterboxd film watchlist and the Serializd
/// episode watchlist at once, so the create/add/remove/wipe-guard paths are pinned in isolation.
/// </summary>
public class PlaylistReconcilerTests
{
    private readonly IPlaylistManager _playlistManager = Substitute.For<IPlaylistManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly User _user = new("lachlan", "test-provider-id", "test-reset-id");

    private void LibraryHasPlaylist(Playlist? playlist)
        => _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(playlist == null ? new List<BaseItem>() : new List<BaseItem> { playlist });

    private static Playlist MakePlaylist(params Guid[] memberIds)
    {
        var p = new Playlist { Id = Guid.NewGuid(), Name = "Watchlist" };
        p.LinkedChildren = Array.ConvertAll(memberIds, id => new LinkedChild { ItemId = id });
        return p;
    }

    [Fact]
    public async Task NoExistingPlaylist_NonEmptyDesired_CreatesIt()
    {
        LibraryHasPlaylist(null);
        var desired = new HashSet<Guid> { Guid.NewGuid() };

        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, NullLogger.Instance, _user, "Watchlist", desired, sourceWasEmpty: false);

        await _playlistManager.Received(1).CreatePlaylist(Arg.Is<PlaylistCreationRequest>(
            r => r.Name == "Watchlist" && r.ItemIdList.Count == 1));
    }

    [Fact]
    public async Task NoExistingPlaylist_EmptyDesired_DoesNotCreate()
    {
        LibraryHasPlaylist(null);

        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, NullLogger.Instance, _user, "Watchlist",
            new HashSet<Guid>(), sourceWasEmpty: true);

        await _playlistManager.DidNotReceive().CreatePlaylist(Arg.Any<PlaylistCreationRequest>());
    }

    [Fact]
    public async Task ExistingPlaylist_AddsOnlyMissingItems()
    {
        var kept = Guid.NewGuid();
        var newItem = Guid.NewGuid();
        LibraryHasPlaylist(MakePlaylist(kept));

        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, NullLogger.Instance, _user, "Watchlist",
            new HashSet<Guid> { kept, newItem }, sourceWasEmpty: false);

        await _playlistManager.Received(1).AddItemToPlaylistAsync(
            Arg.Any<Guid>(), Arg.Is<Guid[]>(ids => ids.Length == 1 && ids[0] == newItem), _user.Id);
    }

    [Fact]
    public async Task ExistingPlaylist_RemovesStaleItems()
    {
        var stale = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var playlist = MakePlaylist(stale, kept);
        LibraryHasPlaylist(playlist);

        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, NullLogger.Instance, _user, "Watchlist",
            new HashSet<Guid> { kept }, sourceWasEmpty: false);

        await _playlistManager.Received(1).RemoveItemFromPlaylistAsync(
            playlist.Id.ToString("N"), Arg.Is<string[]>(ids => ids.Length == 1 && ids[0] == stale.ToString("N")));
    }

    [Fact]
    public async Task ExistingPlaylist_SourceWasEmpty_SkipsRemoval()
    {
        // A blocked scrape or failed fetch must never be allowed to wipe an existing
        // playlist just because the desired set came back empty this run.
        var member = Guid.NewGuid();
        LibraryHasPlaylist(MakePlaylist(member));

        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, NullLogger.Instance, _user, "Watchlist",
            new HashSet<Guid>(), sourceWasEmpty: true);

        await _playlistManager.DidNotReceive().RemoveItemFromPlaylistAsync(
            Arg.Any<string>(), Arg.Any<string[]>());
    }

    [Fact]
    public async Task ExistingPlaylist_AlreadyInSync_NoAddOrRemoveCalls()
    {
        var member = Guid.NewGuid();
        LibraryHasPlaylist(MakePlaylist(member));

        await PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, NullLogger.Instance, _user, "Watchlist",
            new HashSet<Guid> { member }, sourceWasEmpty: false);

        await _playlistManager.DidNotReceive().AddItemToPlaylistAsync(
            Arg.Any<Guid>(), Arg.Any<Guid[]>(), Arg.Any<Guid>());
        await _playlistManager.DidNotReceive().RemoveItemFromPlaylistAsync(
            Arg.Any<string>(), Arg.Any<string[]>());
    }
}
