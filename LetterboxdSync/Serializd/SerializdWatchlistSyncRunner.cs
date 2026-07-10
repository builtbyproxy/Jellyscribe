using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Mirrors a user's Serializd watchlist into a Jellyfin playlist ("Serializd Watchlist"),
/// the TV counterpart to the Letterboxd watchlist→playlist sync. Only shows that exist in
/// the Jellyfin library are added; matched by the watchlist item's TMDb show id.
/// Opt-in per account via <see cref="SerializdAccount.SyncWatchlist"/>.
/// </summary>
public class SerializdWatchlistSyncRunner
{
    private const string PlaylistName = "Serializd Watchlist";

    private readonly ILogger<SerializdWatchlistSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IPlaylistManager _playlistManager;

    public SerializdWatchlistSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IPlaylistManager playlistManager)
    {
        _logger = loggerFactory.CreateLogger<SerializdWatchlistSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _playlistManager = playlistManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public async Task RunForAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pairs = _userManager.GetUsers()
            .SelectMany(u => Config.GetEnabledSerializdAccountsForUser(u.Id.ToString("N"))
                .Where(a => a.SyncWatchlist)
                .Select(a => (User: u, Account: a)))
            .ToList();

        var processed = 0;
        foreach (var (user, account) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncOneAsync(user, account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd watchlist sync failed for {Username} as {Email}: {Message}",
                    user.Username, account.Email, ex.Message);
            }

            processed++;
            if (pairs.Count > 0)
                progress.Report((double)processed / pairs.Count * 100);
        }

        progress.Report(100);
    }

    public async Task<bool> TryRunForUserAsync(string userJellyfinId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
        if (user == null) return false;

        var accounts = Config.GetEnabledSerializdAccountsForUser(userJellyfinId).Where(a => a.SyncWatchlist).ToList();
        if (accounts.Count == 0) return false;

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncOneAsync(user, account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd watchlist sync failed for {Username} as {Email}: {Message}",
                    user.Username, account.Email, ex.Message);
            }
        }

        return true;
    }

    private async Task SyncOneAsync(User user, SerializdAccount account, CancellationToken cancellationToken)
    {
        List<int> tmdbIds;
        using (var service = await SerializdServiceFactory
                   .CreateAuthenticatedAsync(account.Email, account.Password, _logger).ConfigureAwait(false))
        {
            tmdbIds = await service.GetWatchlistShowTmdbIdsAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Serializd watchlist: {Count} shows for {Username} as {Email}",
            tmdbIds.Count, user.Username, account.Email);

        // Match watchlist shows to Series already in the Jellyfin library, by TMDb id.
        var seriesById = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true,
        });

        var wanted = new HashSet<Guid>();
        foreach (var tmdbId in tmdbIds)
        {
            var match = seriesById.FirstOrDefault(s => s.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString());
            if (match != null) wanted.Add(match.Id);
        }

        _logger.LogInformation("Serializd watchlist: matched {Matched}/{Total} shows to the Jellyfin library for {Username}",
            wanted.Count, tmdbIds.Count, user.Username);

        await UpdatePlaylistAsync(user, wanted, tmdbIds.Count).ConfigureAwait(false);
    }

    private async Task UpdatePlaylistAsync(User user, HashSet<Guid> wantedItemIds, int watchlistCount)
    {
        var existing = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        });
        var playlist = existing.FirstOrDefault(p => p.Name == PlaylistName);

        if (playlist == null)
        {
            if (wantedItemIds.Count == 0) return;
            await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = PlaylistName,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = wantedItemIds.ToArray(),
            }).ConfigureAwait(false);
            _logger.LogInformation("Created '{Name}' with {Count} shows for {Username}", PlaylistName, wantedItemIds.Count, user.Username);
            return;
        }

        var playlistObj = (Playlist)playlist;
        var existingIds = playlistObj.LinkedChildren
            .Where(lc => lc.ItemId.HasValue)
            .Select(lc => lc.ItemId!.Value)
            .ToHashSet();

        var toAdd = wantedItemIds.Where(id => !existingIds.Contains(id)).ToArray();
        if (toAdd.Length > 0)
            await _playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, user.Id).ConfigureAwait(false);

        // An empty read likely means an API hiccup, not a wiped watchlist; don't gut the playlist.
        var toRemove = (watchlistCount == 0 && existingIds.Count > 0)
            ? Array.Empty<string>()
            : existingIds.Where(id => !wantedItemIds.Contains(id)).Select(id => id.ToString("N")).ToArray();
        if (toRemove.Length > 0)
            await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), toRemove).ConfigureAwait(false);

        _logger.LogInformation("Serializd watchlist playlist for {Username}: +{Added} / -{Removed}",
            user.Username, toAdd.Length, toRemove.Length);
    }
}
