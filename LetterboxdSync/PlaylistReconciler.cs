using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Create-or-update a named Jellyfin container (playlist or collection) to match a
/// caller-computed desired set of media item ids. Shared by <see cref="WatchlistSyncRunner"/>
/// (playlist of films) and <see cref="Serializd.SerializdWatchlistSyncRunner"/> (playlist of
/// episodes, collection of shows), which independently reimplemented the same find/create/diff
/// logic per container kind; a prior duplicate-add bug here (comparing playlist-entry ids
/// against item ids instead of the wrapped item ids) is exactly the class of bug this exists
/// to keep from being fixed in only one of the reimplementations next time.
/// </summary>
internal static class PlaylistReconciler
{
    public static Task ReconcileAsync(
        IPlaylistManager playlistManager, ILibraryManager libraryManager, ILogger logger,
        User user, string playlistName, HashSet<Guid> desired, bool sourceWasEmpty)
    {
        return ReconcileContainerAsync(
            containerLabel: "Playlist",
            containerName: playlistName,
            username: user.Username,
            desired: desired,
            sourceWasEmpty: sourceWasEmpty,
            logger: logger,
            findExisting: () => libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist },
                Recursive = true,
            }).FirstOrDefault(p => string.Equals(p.Name, playlistName, StringComparison.Ordinal)),
            create: ids => playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = ids.ToArray(),
            }),
            // Source of truth: Playlist.LinkedChildren contains the wrapped media item ids.
            // Querying ParentId returns playlist *entries* whose BaseItem.Id is the entry guid,
            // not the wrapped item's guid; comparing that against desired item ids never
            // matches and creates duplicates on every run.
            getExistingMembers: container => ((Playlist)container).LinkedChildren
                .Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToHashSet(),
            add: (container, toAdd) => playlistManager.AddItemToPlaylistAsync(container.Id, toAdd, user.Id),
            remove: (container, toRemove) => playlistManager.RemoveItemFromPlaylistAsync(
                container.Id.ToString("N"), toRemove.Select(id => id.ToString("N")).ToArray()));
    }

    public static Task ReconcileCollectionAsync(
        ICollectionManager collectionManager, ILibraryManager libraryManager, ILogger logger,
        User user, string collectionName, HashSet<Guid> desired, bool sourceWasEmpty)
    {
        return ReconcileContainerAsync(
            containerLabel: "Collection",
            containerName: collectionName,
            username: user.Username,
            desired: desired,
            sourceWasEmpty: sourceWasEmpty,
            logger: logger,
            findExisting: () => libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true,
            }).FirstOrDefault(b => string.Equals(b.Name, collectionName, StringComparison.Ordinal)),
            create: ids => collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = collectionName,
                ItemIdList = ids.Select(g => g.ToString("N")).ToList(),
                UserIds = new[] { user.Id },
            }),
            getExistingMembers: container => ((Folder)container).LinkedChildren
                .Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToHashSet(),
            add: (container, toAdd) => collectionManager.AddToCollectionAsync(container.Id, toAdd),
            remove: (container, toRemove) => collectionManager.RemoveFromCollectionAsync(container.Id, toRemove));
    }

    private static async Task ReconcileContainerAsync(
        string containerLabel, string containerName, string? username,
        HashSet<Guid> desired, bool sourceWasEmpty, ILogger logger,
        Func<BaseItem?> findExisting,
        Func<HashSet<Guid>, Task> create,
        Func<BaseItem, HashSet<Guid>> getExistingMembers,
        Func<BaseItem, Guid[], Task> add,
        Func<BaseItem, IEnumerable<Guid>, Task> remove)
    {
        var container = findExisting();

        if (container == null)
        {
            if (desired.Count == 0) return;

            await create(desired).ConfigureAwait(false);
            logger.LogInformation("Created {Label} '{Name}' with {Count} item(s) for {Username}",
                containerLabel, containerName, desired.Count, username);
            return;
        }

        var existing = getExistingMembers(container);

        var toAdd = desired.Where(id => !existing.Contains(id)).ToArray();
        if (toAdd.Length > 0)
            await add(container, toAdd).ConfigureAwait(false);

        // sourceWasEmpty signals the caller's own upstream fetch came back empty (a blocked
        // scrape, a failed API call), not that the user genuinely emptied their watchlist;
        // skip removal so a transient failure can't gut an existing container.
        var toRemove = (sourceWasEmpty && existing.Count > 0)
            ? Array.Empty<Guid>()
            : existing.Where(id => !desired.Contains(id)).ToArray();
        if (toRemove.Length > 0)
            await remove(container, toRemove).ConfigureAwait(false);

        if (toAdd.Length > 0 || toRemove.Length > 0)
            logger.LogInformation("{Label} '{Name}' for {Username}: +{Added} / -{Removed}",
                containerLabel, containerName, username, toAdd.Length, toRemove.Length);
        else
            logger.LogInformation("{Label} '{Name}' already up to date for {Username}", containerLabel, containerName, username);
    }
}
