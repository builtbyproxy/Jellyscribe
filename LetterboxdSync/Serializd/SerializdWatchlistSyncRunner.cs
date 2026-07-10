using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Mirrors a user's Serializd watchlist into Jellyfin, two ways (parity with the Letterboxd
/// watchlist feature, adapted to TV's show→season→episode hierarchy):
/// <list type="bullet">
/// <item>a <b>collection</b> "Serializd Watchlist" of the watchlisted <i>shows</i> (browse), and</item>
/// <item>a <b>playlist</b> "Serializd Watchlist" of the <i>episodes</i> of the specific seasons
/// you watchlisted (a play-queue; this is where season accuracy lives, since a playlist is
/// episode-level anyway).</item>
/// </list>
/// Both reconcile (add + remove) to match the current watchlist. Opt-in per account via
/// <see cref="SerializdAccount.SyncWatchlist"/>.
/// </summary>
public class SerializdWatchlistSyncRunner
{
    private const string Name = "Serializd Watchlist";

    private readonly ILogger<SerializdWatchlistSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IPlaylistManager _playlistManager;

    public SerializdWatchlistSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ICollectionManager collectionManager,
        IPlaylistManager playlistManager)
    {
        _logger = loggerFactory.CreateLogger<SerializdWatchlistSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _collectionManager = collectionManager;
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
        List<SerializdWatchlistEntry> entries;
        using (var service = await SerializdServiceFactory
                   .CreateAuthenticatedAsync(account.Email, account.Password, _logger).ConfigureAwait(false))
        {
            entries = await service.GetWatchlistAsync().ConfigureAwait(false);
        }

        var seriesByTmdb = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true,
        }).Where(s => int.TryParse(s.GetProviderId(MetadataProvider.Tmdb), out _))
          .GroupBy(s => s.GetProviderId(MetadataProvider.Tmdb)!)
          .ToDictionary(g => g.Key, g => g.First());

        var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Episode>().ToList();

        var desiredShows = new HashSet<Guid>();
        var desiredEpisodes = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (!seriesByTmdb.TryGetValue(entry.ShowTmdbId.ToString(), out var series)) continue;
            desiredShows.Add(series.Id);

            // Empty SeasonNumbers = no season detail on the watchlist item ⇒ the whole show.
            var seasonFilter = entry.SeasonNumbers.Count > 0 ? new HashSet<int>(entry.SeasonNumbers) : null;
            foreach (var ep in allEpisodes)
            {
                if (ep.SeriesId != series.Id) continue;
                if (seasonFilter != null && !seasonFilter.Contains(ep.ParentIndexNumber ?? -1)) continue;
                desiredEpisodes.Add(ep.Id);
            }
        }

        _logger.LogInformation(
            "Serializd watchlist for {Username}: {Shows} shows, {Episodes} episodes (from watchlisted seasons)",
            user.Username, desiredShows.Count, desiredEpisodes.Count);

        await ReconcileCollectionAsync(user, desiredShows).ConfigureAwait(false);
        await ReconcilePlaylistAsync(user, desiredEpisodes).ConfigureAwait(false);
    }

    private async Task ReconcileCollectionAsync(User user, HashSet<Guid> desired)
    {
        var boxSet = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
        }).FirstOrDefault(b => string.Equals(b.Name, Name, StringComparison.Ordinal));

        if (boxSet == null)
        {
            if (desired.Count == 0) return;
            await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = Name,
                ItemIdList = desired.Select(g => g.ToString("N")).ToList(),
                UserIds = new[] { user.Id },
            }).ConfigureAwait(false);
            _logger.LogInformation("Created '{Name}' collection with {Count} shows for {Username}", Name, desired.Count, user.Username);
            return;
        }

        var members = ((Folder)boxSet).LinkedChildren
            .Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToHashSet();

        var toAdd = desired.Where(id => !members.Contains(id)).ToList();
        if (toAdd.Count > 0)
            await _collectionManager.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);

        // Empty desired with existing members likely means an API hiccup; don't wipe.
        var toRemove = (desired.Count == 0 && members.Count > 0)
            ? new List<Guid>()
            : members.Where(id => !desired.Contains(id)).ToList();
        if (toRemove.Count > 0)
            await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, toRemove).ConfigureAwait(false);

        _logger.LogInformation("'{Name}' collection for {Username}: +{Added} / -{Removed}", Name, user.Username, toAdd.Count, toRemove.Count);
    }

    private async Task ReconcilePlaylistAsync(User user, HashSet<Guid> desired)
    {
        var playlist = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        }).FirstOrDefault(p => string.Equals(p.Name, Name, StringComparison.Ordinal));

        if (playlist == null)
        {
            if (desired.Count == 0) return;
            await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = Name,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = desired.ToArray(),
            }).ConfigureAwait(false);
            _logger.LogInformation("Created '{Name}' playlist with {Count} episodes for {Username}", Name, desired.Count, user.Username);
            return;
        }

        var existing = ((Playlist)playlist).LinkedChildren
            .Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToHashSet();

        var toAdd = desired.Where(id => !existing.Contains(id)).ToArray();
        if (toAdd.Length > 0)
            await _playlistManager.AddItemToPlaylistAsync(playlist.Id, toAdd, user.Id).ConfigureAwait(false);

        var toRemove = (desired.Count == 0 && existing.Count > 0)
            ? Array.Empty<string>()
            : existing.Where(id => !desired.Contains(id)).Select(id => id.ToString("N")).ToArray();
        if (toRemove.Length > 0)
            await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), toRemove).ConfigureAwait(false);

        _logger.LogInformation("'{Name}' playlist for {Username}: +{Added} / -{Removed}", Name, user.Username, toAdd.Length, toRemove.Length);
    }
}
