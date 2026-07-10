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

        var name = account.GetWatchlistName();
        await ReconcileCollectionAsync(user, desiredShows, name).ConfigureAwait(false);
        await ReconcilePlaylistAsync(user, desiredEpisodes, name).ConfigureAwait(false);

        await SeerrIntegrationAsync(account, entries, seriesByTmdb, user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Seerr side of the Serializd watchlist sync, the TV counterpart to the Letterboxd runner:
    /// optionally mirror the watchlist into the Seerr user's own watchlist (as TV) and/or
    /// auto-request watchlisted shows (missing only, or the whole watchlist when backfilling).
    /// </summary>
    private async Task SeerrIntegrationAsync(SerializdAccount account, List<SerializdWatchlistEntry> entries,
        Dictionary<string, BaseItem> seriesByTmdb, User user, CancellationToken cancellationToken)
    {
        if (!account.AutoRequestWatchlist && !account.MirrorJellyseerrWatchlist) return;

        var cfg = Config;
        if (!SeerrClient.IsConfigured(cfg.JellyseerrUrl, cfg.JellyseerrApiKey))
        {
            _logger.LogWarning("Serializd watchlist: Seerr auto-request/mirror is on but Seerr isn't configured; skipping for {Username}", user.Username);
            return;
        }

        using var seerr = new SeerrClient(cfg.JellyseerrUrl!, cfg.JellyseerrApiKey!, _logger);
        var seerrUserId = await seerr.GetJellyseerrUserIdAsync(account.UserJellyfinId).ConfigureAwait(false);
        if (seerrUserId == null)
        {
            _logger.LogWarning("Serializd watchlist: no Seerr user linked to {Username}; skipping auto-request/mirror", user.Username);
            return;
        }

        var watchlistTmdbIds = entries.Select(e => e.ShowTmdbId).Distinct().ToList();

        if (account.MirrorJellyseerrWatchlist)
        {
            // The Seerr watchlist is keyed by Jellyfin user, not by Serializd account, so only
            // the primary Serializd account owns the TV mirror destination (otherwise two
            // accounts on one user would each wipe the other's diff). Film mirrors mediaType
            // "movie" and TV mirrors "tv", so the two are independent and never clobber.
            var primary = cfg.GetPrimarySerializdAccountForUser(account.UserJellyfinId);
            if (ReferenceEquals(primary, account))
                await MirrorSerializdWatchlistToSeerrAsync(seerr, seerrUserId.Value, watchlistTmdbIds, user.Username!, cancellationToken).ConfigureAwait(false);
            else
                _logger.LogInformation("Skipping Seerr watchlist mirror for {Email}: not the primary Serializd account for {Username}", account.Email, user.Username);
        }

        if (account.AutoRequestWatchlist)
        {
            // Default: only shows missing from the library. Backfill mode: the whole watchlist,
            // so already-available shows also get an attributed request (Seerr's already-exists
            // handling makes a duplicate a harmless no-op).
            var candidates = account.BackfillAvailableRequests
                ? entries
                : entries.Where(e => !seriesByTmdb.ContainsKey(e.ShowTmdbId.ToString())).ToList();

            int requested = 0, existing = 0, failed = 0;
            foreach (var entry in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await seerr.RequestSeriesAsync(entry.ShowTmdbId, seerrUserId.Value, entry.SeasonNumbers, account.BackfillAvailableRequests).ConfigureAwait(false);
                switch (result)
                {
                    case SeerrClient.RequestResult.Requested: requested++; break;
                    case SeerrClient.RequestResult.AlreadyExists: existing++; break;
                    default: failed++; break;
                }
            }

            if (requested + existing + failed > 0)
                _logger.LogInformation("Serializd watchlist Seerr auto-request for {Username} ({Mode}): {Requested} new, {Existing} already on Seerr, {Failed} failed",
                    user.Username, account.BackfillAvailableRequests ? "backfill" : "unmatched-only", requested, existing, failed);
        }
    }

    /// <summary>
    /// Mirrors the Serializd watchlist into the Seerr user's own watchlist as TV entries
    /// (add + remove to match). Only touches mediaType "tv" rows, so it never disturbs the
    /// film mirror. Refuses to run on an empty watchlist to avoid mass-deletion. TV counterpart
    /// of the Letterboxd <c>MirrorJellyseerrWatchlistAsync</c>.
    /// </summary>
    private async Task MirrorSerializdWatchlistToSeerrAsync(SeerrClient seerr, int seerrUserId,
        List<int> watchlistTmdbIds, string jellyfinUsername, CancellationToken cancellationToken)
    {
        if (watchlistTmdbIds.Count == 0)
        {
            _logger.LogWarning("Empty Serializd watchlist for {Username}; skipping Seerr TV mirror to avoid mass-deletion", jellyfinUsername);
            return;
        }

        HashSet<int> currentSeerr;
        try
        {
            currentSeerr = await seerr.GetUserWatchlistTmdbIdsAsync(seerrUserId, "tv").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch Seerr TV watchlist for {Username}: {Message}", jellyfinUsername, ex.Message);
            return;
        }

        var desired = new HashSet<int>(watchlistTmdbIds);
        var toAdd = desired.Where(id => !currentSeerr.Contains(id)).ToList();
        var toRemove = currentSeerr.Where(id => !desired.Contains(id)).ToList();

        int added = 0, removed = 0, addFailed = 0, removeFailed = 0;
        foreach (var tmdbId in toAdd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (await seerr.AddToWatchlistAsync(tmdbId, seerrUserId, "tv").ConfigureAwait(false)) added++; else addFailed++; }
            catch (Exception ex) { _logger.LogWarning("Seerr TV watchlist add errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message); addFailed++; }
        }

        foreach (var tmdbId in toRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (await seerr.RemoveFromWatchlistAsync(tmdbId, seerrUserId, "tv").ConfigureAwait(false)) removed++; else removeFailed++; }
            catch (Exception ex) { _logger.LogWarning("Seerr TV watchlist remove errored for TMDb {TmdbId}: {Message}", tmdbId, ex.Message); removeFailed++; }
        }

        _logger.LogInformation("Seerr TV watchlist mirror for {Username}: +{Added} -{Removed} (add failures {AddFailed}, remove failures {RemoveFailed})",
            jellyfinUsername, added, removed, addFailed, removeFailed);
    }

    private async Task ReconcileCollectionAsync(User user, HashSet<Guid> desired, string name)
    {
        var boxSet = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
        }).FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.Ordinal));

        if (boxSet == null)
        {
            if (desired.Count == 0) return;
            await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = name,
                ItemIdList = desired.Select(g => g.ToString("N")).ToList(),
                UserIds = new[] { user.Id },
            }).ConfigureAwait(false);
            _logger.LogInformation("Created '{Name}' collection with {Count} shows for {Username}", name, desired.Count, user.Username);
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

        _logger.LogInformation("'{Name}' collection for {Username}: +{Added} / -{Removed}", name, user.Username, toAdd.Count, toRemove.Count);
    }

    private async Task ReconcilePlaylistAsync(User user, HashSet<Guid> desired, string name)
    {
        var playlist = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
        }).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        if (playlist == null)
        {
            if (desired.Count == 0) return;
            await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                UserId = user.Id,
                MediaType = MediaType.Video,
                ItemIdList = desired.ToArray(),
            }).ConfigureAwait(false);
            _logger.LogInformation("Created '{Name}' playlist with {Count} episodes for {Username}", name, desired.Count, user.Username);
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

        _logger.LogInformation("'{Name}' playlist for {Username}: +{Added} / -{Removed}", name, user.Username, toAdd.Length, toRemove.Length);
    }
}
