using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Create-or-update a named Jellyfin playlist to match a caller-computed desired set of media
/// item ids. Shared by <see cref="WatchlistSyncRunner"/> (films) and
/// <see cref="Serializd.SerializdWatchlistSyncRunner"/> (episodes), which independently
/// reimplemented the same logic; a prior duplicate-add bug here (comparing playlist-entry ids
/// against item ids instead of the wrapped item ids) is exactly the class of bug this exists
/// to keep from being fixed in only one of the two places next time.
/// </summary>
internal static class PlaylistReconciler
{
    public static async Task ReconcileAsync(
        IPlaylistManager playlistManager, ILibraryManager libraryManager, ILogger logger,
        User user, string playlistName, HashSet<Guid> desired, bool sourceWasEmpty)
    {
        var playlist = libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        }).FirstOrDefault(p => string.Equals(p.Name, playlistName, StringComparison.Ordinal));

        if (playlist == null)
        {
            if (desired.Count == 0) return;

            await playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = desired.ToArray(),
            }).ConfigureAwait(false);

            logger.LogInformation("Created playlist '{Name}' with {Count} item(s) for {Username}",
                playlistName, desired.Count, user.Username);
            return;
        }

        // Source of truth: Playlist.LinkedChildren contains the wrapped media item ids.
        // Querying ParentId returns playlist *entries* whose BaseItem.Id is the entry guid, not
        // the wrapped item's guid; comparing that against desired item ids never matches and
        // creates duplicates on every run.
        var existing = ((Playlist)playlist).LinkedChildren
            .Where(lc => lc.ItemId.HasValue)
            .Select(lc => lc.ItemId!.Value)
            .ToHashSet();

        var toAdd = desired.Where(id => !existing.Contains(id)).ToArray();
        if (toAdd.Length > 0)
            await playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, user.Id).ConfigureAwait(false);

        // sourceWasEmpty signals the caller's own upstream fetch came back empty (a blocked
        // scrape, a failed API call), not that the user genuinely emptied their watchlist;
        // skip removal so a transient failure can't gut an existing playlist.
        var toRemove = (sourceWasEmpty && existing.Count > 0)
            ? Array.Empty<string>()
            : existing.Where(id => !desired.Contains(id)).Select(id => id.ToString("N")).ToArray();
        if (toRemove.Length > 0)
            await playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), toRemove).ConfigureAwait(false);

        if (toAdd.Length > 0 || toRemove.Length > 0)
            logger.LogInformation("Playlist '{Name}' for {Username}: +{Added} / -{Removed}",
                playlistName, user.Username, toAdd.Length, toRemove.Length);
        else
            logger.LogInformation("Playlist '{Name}' already up to date for {Username}", playlistName, user.Username);
    }
}
