using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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

    // ===== Collections (Serializd.SerializdWatchlistSyncRunner.ReconcileCollectionAsync) =====
    // Same shared core as the playlist tests above; a fix to the find/create/diff/wipe-guard
    // logic now applies to both container kinds at once instead of needing to land twice.

    private readonly ICollectionManager _collectionManager = Substitute.For<ICollectionManager>();

    private void LibraryHasCollection(BoxSet? boxSet)
        => _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(boxSet == null ? new List<BaseItem>() : new List<BaseItem> { boxSet });

    private static BoxSet MakeCollection(params Guid[] memberIds)
    {
        var b = new BoxSet { Id = Guid.NewGuid(), Name = "Serializd Watchlist" };
        b.LinkedChildren = Array.ConvertAll(memberIds, id => new LinkedChild { ItemId = id });
        return b;
    }

    [Fact]
    public async Task NoExistingCollection_NonEmptyDesired_CreatesIt()
    {
        LibraryHasCollection(null);
        var desired = new HashSet<Guid> { Guid.NewGuid() };

        await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _libraryManager, NullLogger.Instance, _user, "Serializd Watchlist", desired, sourceWasEmpty: false);

        await _collectionManager.Received(1).CreateCollectionAsync(Arg.Is<CollectionCreationOptions>(
            o => o.Name == "Serializd Watchlist" && o.ItemIdList.Count == 1));
    }

    [Fact]
    public async Task NoExistingCollection_EmptyDesired_DoesNotCreate()
    {
        LibraryHasCollection(null);

        await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _libraryManager, NullLogger.Instance, _user, "Serializd Watchlist",
            new HashSet<Guid>(), sourceWasEmpty: true);

        await _collectionManager.DidNotReceive().CreateCollectionAsync(Arg.Any<CollectionCreationOptions>());
    }

    [Fact]
    public async Task ExistingCollection_AddsOnlyMissingItems()
    {
        var kept = Guid.NewGuid();
        var newItem = Guid.NewGuid();
        LibraryHasCollection(MakeCollection(kept));

        await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _libraryManager, NullLogger.Instance, _user, "Serializd Watchlist",
            new HashSet<Guid> { kept, newItem }, sourceWasEmpty: false);

        await _collectionManager.Received(1).AddToCollectionAsync(
            Arg.Any<Guid>(), Arg.Is<IEnumerable<Guid>>(ids => new List<Guid>(ids).Count == 1 && new List<Guid>(ids)[0] == newItem));
    }

    [Fact]
    public async Task ExistingCollection_RemovesStaleItems()
    {
        var stale = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var boxSet = MakeCollection(stale, kept);
        LibraryHasCollection(boxSet);

        await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _libraryManager, NullLogger.Instance, _user, "Serializd Watchlist",
            new HashSet<Guid> { kept }, sourceWasEmpty: false);

        await _collectionManager.Received(1).RemoveFromCollectionAsync(
            boxSet.Id, Arg.Is<IEnumerable<Guid>>(ids => new List<Guid>(ids).Count == 1 && new List<Guid>(ids)[0] == stale));
    }

    [Fact]
    public async Task ExistingCollection_SourceWasEmpty_SkipsRemoval()
    {
        // A failed Serializd fetch must never be allowed to wipe an existing collection just
        // because the desired set came back empty this run.
        var member = Guid.NewGuid();
        LibraryHasCollection(MakeCollection(member));

        await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _libraryManager, NullLogger.Instance, _user, "Serializd Watchlist",
            new HashSet<Guid>(), sourceWasEmpty: true);

        await _collectionManager.DidNotReceive().RemoveFromCollectionAsync(
            Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>());
    }

    [Fact]
    public async Task ExistingCollection_AlreadyInSync_NoAddOrRemoveCalls()
    {
        var member = Guid.NewGuid();
        LibraryHasCollection(MakeCollection(member));

        await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _libraryManager, NullLogger.Instance, _user, "Serializd Watchlist",
            new HashSet<Guid> { member }, sourceWasEmpty: false);

        await _collectionManager.DidNotReceive().AddToCollectionAsync(
            Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>());
        await _collectionManager.DidNotReceive().RemoveFromCollectionAsync(
            Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>());
    }
}
