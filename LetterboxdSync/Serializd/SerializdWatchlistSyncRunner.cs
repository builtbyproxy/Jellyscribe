using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Mirrors a user's Serializd watchlist into Jellyfin, two ways (parity with the Letterboxd
/// watchlist feature, adapted to TV's show→season→episode hierarchy):
/// <list type="bullet">
/// <item>a <b>collection</b> "Serializd Watchlist (username)" of the watchlisted <i>shows</i>
/// (browse), one per account and tracked by id because collections are server-wide, and</item>
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

    /// <summary>How long a scheduled run waits for a manual run to finish. Tests shorten it.</summary>
    internal static TimeSpan ScheduledGateWait { get; set; } = TimeSpan.FromMinutes(15);

    public async Task RunForAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // A user's "Sync watchlist now" may hold the gate; it covers only that user, so the
        // scheduled run waits for it (bounded) instead of skipping everyone else until the
        // next trigger.
        if (!await SerializdWatchlistSyncGate.Instance.WaitAsync(ScheduledGateWait, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Serializd watchlist sync still running after {Minutes} minutes, skipping scheduled run",
                ScheduledGateWait.TotalMinutes);
            return;
        }

        try
        {
            await RunForAllCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SerializdWatchlistSyncGate.Instance.Release();
        }
    }

    private async Task RunForAllCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pairs = _userManager.GetUsers()
            .SelectMany(u => Config.GetEnabledSerializdAccountsForUser(u.Id.ToString("N"))
                .Where(a => a.SyncWatchlist)
                .Select(a => (User: u, Account: a)))
            .ToList();

        SyncProgress.Start(SyncProgress.TrackSerializd, "Serializd watchlist sync", "Starting");
        SyncProgress.SetTotal(SyncProgress.TrackSerializd, pairs.Count);
        try
        {
            var processed = 0;
            foreach (var (user, account) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyncProgress.SetPhase(SyncProgress.TrackSerializd, "Syncing watchlist");
                try
                {
                    await SyncOneAsync(user, account, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError("Serializd watchlist sync failed for {Username} as {Account}: {Message}",
                        user.Username, LogRedaction.AccountTag(account.Email), ex.Message);
                    // No SyncEvent is recorded on this path; hook telemetry directly.
                    TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
                }

                processed++;
                SyncProgress.IncrementProcessed(SyncProgress.TrackSerializd);
                if (pairs.Count > 0)
                    progress.Report((double)processed / pairs.Count * 100);
            }

            progress.Report(100);
        }
        finally
        {
            SyncProgress.Complete(SyncProgress.TrackSerializd);
        }
    }

    /// <summary>
    /// Runs the watchlist sync for one Jellyfin user. Returns false when another watchlist run
    /// holds the gate, the user is unknown, or none of their accounts has watchlist sync on.
    /// </summary>
    public async Task<bool> TryRunForUserAsync(string userJellyfinId, CancellationToken cancellationToken)
    {
        if (!await SerializdWatchlistSyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Serializd watchlist sync already running, refusing user-triggered start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            return await TryRunForUserCoreAsync(userJellyfinId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SerializdWatchlistSyncGate.Instance.Release();
        }
    }

    private async Task<bool> TryRunForUserCoreAsync(string userJellyfinId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
        if (user == null) return false;

        var accounts = Config.GetEnabledSerializdAccountsForUser(userJellyfinId).Where(a => a.SyncWatchlist).ToList();
        if (accounts.Count == 0) return false;

        SyncProgress.Start(SyncProgress.TrackSerializd, "Serializd watchlist sync", "Syncing watchlist");
        SyncProgress.SetTotal(SyncProgress.TrackSerializd, accounts.Count);
        try
        {
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SyncOneAsync(user, account, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError("Serializd watchlist sync failed for {Username} as {Account}: {Message}",
                        user.Username, LogRedaction.AccountTag(account.Email), ex.Message);
                    // No SyncEvent is recorded on this path; hook telemetry directly.
                    TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
                }

                SyncProgress.IncrementProcessed(SyncProgress.TrackSerializd);
            }

            return true;
        }
        finally
        {
            SyncProgress.Complete(SyncProgress.TrackSerializd);
        }
    }

    private async Task SyncOneAsync(User user, SerializdAccount account, CancellationToken cancellationToken)
    {
        List<SerializdWatchlistEntry> entries;
        using (var service = await SerializdServiceFactory
                   .CreateAuthenticatedAsync(account.Email, account.Password, _logger).ConfigureAwait(false))
        {
            entries = await service.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
        }

        WatchlistStats.SetTv(user.Id.ToString("N"), account.Email, entries.Count);

        var seriesByTmdb = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true,
        }).Where(s => int.TryParse(s.GetProviderId(MetadataProvider.Tmdb), out _))
          .GroupBy(s => s.GetProviderId(MetadataProvider.Tmdb)!)
          .ToDictionary(g => g.Key, g => g.First());

        var episodesBySeries = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Episode>().ToLookup(e => e.SeriesId);

        var desiredShows = new HashSet<Guid>();
        var desiredEpisodes = new HashSet<Guid>();

        foreach (var entry in entries)
        {
            if (!seriesByTmdb.TryGetValue(entry.ShowTmdbId.ToString(), out var series)) continue;
            desiredShows.Add(series.Id);

            // Empty SeasonNumbers = no season detail on the watchlist item ⇒ the whole show.
            var seasonFilter = entry.SeasonNumbers.Count > 0 ? new HashSet<int>(entry.SeasonNumbers) : null;
            foreach (var ep in episodesBySeries[series.Id])
            {
                if (seasonFilter != null && !seasonFilter.Contains(ep.ParentIndexNumber ?? -1)) continue;
                desiredEpisodes.Add(ep.Id);
            }
        }

        _logger.LogInformation(
            "Serializd watchlist for {Username}: {Shows} shows, {Episodes} episodes (from watchlisted seasons)",
            user.Username, desiredShows.Count, desiredEpisodes.Count);

        // Distinguishes "the Serializd fetch itself came back empty" (a transient failure we must
        // not treat as "the user emptied their watchlist") from "the fetch had entries but none
        // resolved to a desired show/episode" (a real state, safe to reconcile away).
        var sourceWasEmpty = entries.Count == 0;

        await ReconcileCollectionAsync(user, account, desiredShows, sourceWasEmpty, cancellationToken).ConfigureAwait(false);
        await ReconcilePlaylistAsync(user, desiredEpisodes, account.GetWatchlistName(), sourceWasEmpty).ConfigureAwait(false);

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

        using var seerr = CreateSeerrClient(cfg);
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
                _logger.LogInformation("Skipping Seerr watchlist mirror for {Account}: not the primary Serializd account for {Username}", LogRedaction.AccountTag(account.Email), user.Username);
        }

        if (account.AutoRequestWatchlist)
        {
            // Build the request list per watchlisted show. The default is season-completeness
            // aware: a show entirely absent is requested whole; a show that's only partially in
            // the library is requested for JUST the watchlisted seasons that are still missing
            // episodes, so Seerr/Sonarr fill the gaps (e.g. you have S1E2 → S1 is re-requested and
            // Sonarr searches the rest). A season already complete on disk is skipped. Backfill
            // mode ignores completeness and requests the watchlisted seasons regardless (requester
            // trail); Seerr's already-exists handling makes a redundant request a harmless no-op.
            var requests = new List<(int Tmdb, IReadOnlyList<int> Seasons)>();
            foreach (var entry in entries)
            {
                var inLibrary = seriesByTmdb.TryGetValue(entry.ShowTmdbId.ToString(), out var series);
                if (account.BackfillAvailableRequests || !inLibrary)
                {
                    requests.Add((entry.ShowTmdbId, entry.SeasonNumbers));
                }
                else
                {
                    var (shouldRequest, incomplete) = IncompleteWatchlistedSeasons(user, series!, entry.SeasonNumbers);
                    if (shouldRequest)
                        requests.Add((entry.ShowTmdbId, incomplete));
                }
            }

            int requested = 0, existing = 0, failed = 0;
            foreach (var (tmdb, seasons) in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var (result, title) = await seerr.RequestSeriesAsync(tmdb, seerrUserId.Value, seasons, account.BackfillAvailableRequests).ConfigureAwait(false);
                    switch (result)
                    {
                        case SeerrClient.RequestResult.Requested:
                            requested++;
                            SyncHistory.Record(new SyncEvent
                            {
                                FilmTitle = title ?? $"TMDb {tmdb}",
                                TmdbId = tmdb,
                                Username = user.Username!,
                                Timestamp = DateTime.UtcNow,
                                Status = SyncStatus.Requested,
                                Source = SyncEventSources.SeerrAutoRequestTv
                            });
                            break;
                        case SeerrClient.RequestResult.AlreadyExists:
                            existing++;
                            break;
                        default:
                            failed++;
                            SyncHistory.Record(new SyncEvent
                            {
                                FilmTitle = title ?? $"TMDb {tmdb}",
                                TmdbId = tmdb,
                                Username = user.Username!,
                                Timestamp = DateTime.UtcNow,
                                Status = SyncStatus.Failed,
                                Source = SyncEventSources.SeerrAutoRequestTv,
                                Error = $"Seerr TV request failed for TMDb {tmdb}"
                            });
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Seerr TV request errored for TMDb {TmdbId}: {Message}", tmdb, ex.Message);
                    failed++;
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = $"TMDb {tmdb}",
                        TmdbId = tmdb,
                        Username = user.Username!,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Source = SyncEventSources.SeerrAutoRequestTv,
                        Error = ex.Message
                    });
                }
            }

            if (requested + existing + failed > 0)
                _logger.LogInformation("Serializd watchlist Seerr auto-request for {Username} ({Mode}): {Requested} new, {Existing} already on Seerr, {Failed} failed",
                    user.Username, account.BackfillAvailableRequests ? "backfill" : "incomplete-seasons", requested, existing, failed);

            // Seerr failures surface as return values, not SyncEvents; count the
            // batch once (not per show) so one outage doesn't inflate the error counter.
            if (failed > 0)
                TelemetryService.RecordError(TelemetryService.CatJellyseerr);
        }
    }

    /// <summary>
    /// Test-only override. When set, CreateSeerrClient delegates to this so tests can inject a
    /// SeerrClient bound to a mock HttpMessageHandler. Production never assigns it. Mirrors
    /// <see cref="WatchlistSyncRunner.JellyseerrClientFactoryOverride"/>.
    /// </summary>
    internal static Func<string, string, ILogger, SeerrClient?>? SeerrClientFactoryOverride;

    private SeerrClient CreateSeerrClient(PluginConfiguration cfg)
    {
        if (SeerrClientFactoryOverride != null)
            return SeerrClientFactoryOverride(cfg.JellyseerrUrl!, cfg.JellyseerrApiKey!, _logger)!;

        return new SeerrClient(cfg.JellyseerrUrl!, cfg.JellyseerrApiKey!, _logger,
            autoApprove: cfg.AutoApproveJellyseerrRequests);
    }

    /// <summary>
    /// The watchlisted seasons of a show that are NOT fully present on disk (missing ≥1 episode),
    /// so Seerr/Sonarr can be asked to fill just those. Completeness is judged against Jellyfin's
    /// own episode list for the series: episodes it knows about but has no file for are virtual
    /// (missing) items, so a season with any virtual episode is incomplete. An unknown season
    /// (not in Jellyfin's metadata) is treated as incomplete so it's still requested. When the
    /// entry names no seasons (whole-show watchlist), every season the show has is considered.
    /// <see cref="ShouldRequest"/> is false only when every watchlisted season is confirmed
    /// complete; when the whole show has zero episodes indexed (a placeholder Series, or every
    /// file removed after the initial scan), there is no season data to enumerate at all, so this
    /// reports "request the whole show" (ShouldRequest=true, empty Seasons = Seerr's "all")
    /// instead of silently treating "we know nothing" as "everything is complete".
    /// </summary>
    /// <remarks>Internal (not private) for unit tests: the virtual-item and aired-date
    /// rules here decide real download requests, so each path is pinned by a test.</remarks>
    internal (bool ShouldRequest, List<int> Seasons) IncompleteWatchlistedSeasons(
        User user, BaseItem series, IReadOnlyList<int> watchlistedSeasons)
    {
        var eps = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
            Recursive = true,
            // IsVirtualItem left unset → returns both on-disk and missing (virtual) episodes.
        }).OfType<Episode>().ToList();

        // season number -> (episodes present on disk, total grabbable episodes). A missing
        // (virtual) episode counts toward the total only once it has aired: an unaired future
        // episode isn't grabbable, so it must not make an ongoing season look "incomplete"
        // forever and trigger a pointless re-request every sync.
        var bySeason = new Dictionary<int, (int Present, int Total)>();
        foreach (var ep in eps)
        {
            var s = ep.ParentIndexNumber ?? -1;
            if (s < 0) continue; // skip specials / unparented
            var onDisk = !ep.IsVirtualItem;
            var aired = ep.PremiereDate.HasValue && ep.PremiereDate.Value.ToUniversalTime() <= DateTime.UtcNow;
            if (!onDisk && !aired) continue; // unaired, not yet grabbable
            bySeason.TryGetValue(s, out var c);
            bySeason[s] = (c.Present + (onDisk ? 1 : 0), c.Total + 1);
        }

        if (watchlistedSeasons.Count == 0 && bySeason.Count == 0)
            return (true, new List<int>());

        var targets = watchlistedSeasons.Count > 0
            ? watchlistedSeasons
            : bySeason.Keys.Where(s => s > 0).ToList();

        var incomplete = new List<int>();
        foreach (var s in targets)
        {
            if (!bySeason.TryGetValue(s, out var c) || c.Present < c.Total)
                incomplete.Add(s);
        }

        return (incomplete.Count > 0, incomplete);
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

    /// <summary>
    /// Reconciles this account's own collection. Collections are visible server-wide and Jellyfin
    /// allows any number with the same name, so the collection is the one recorded in
    /// <see cref="SerializdCollectionStore"/>, never one found by name. Without a record (first run,
    /// or the recorded collection was deleted) a new collection is created under a name no other
    /// collection uses: Jellyfin derives a collection's folder, and so its item id, from the name,
    /// so creating a same-named one would land in the existing collection.
    /// </summary>
    private async Task ReconcileCollectionAsync(User user, SerializdAccount account, HashSet<Guid> desired, bool sourceWasEmpty,
        CancellationToken cancellationToken)
    {
        var userId = user.Id.ToString("N");
        var configuredName = account.GetWatchlistCollectionName(user.Username);

        SerializdCollectionStore.Entry? tracked;
        try
        {
            tracked = SerializdCollectionStore.Get(userId, account.Email);
        }
        catch (Exception ex)
        {
            // Without the record we cannot tell which collection is ours; skipping is safer than
            // creating a duplicate or guessing by name.
            _logger.LogError("Skipping the Serializd watchlist collection for {Username}: could not read which collection is theirs ({Message})",
                user.Username, ex.Message);
            return;
        }

        BoxSet? collection = null;
        if (tracked != null)
        {
            collection = _libraryManager.GetItemById(tracked.CollectionId) as BoxSet;
            if (collection == null)
                _logger.LogInformation("Serializd watchlist collection for {Username} no longer exists; a new one will be created", user.Username);
        }
        else
        {
            // An adopted collection keeps its name: recording the configured name as applied means
            // only a later change to the setting renames it.
            collection = AdoptLegacyCollection(user, account);
            if (collection != null)
                SaveTracked(user, account, collection.Id, configuredName);
        }

        // The resolved name changed since it was last applied: the admin changed the setting, or
        // the Jellyfin username in the default name changed. A rename made in Jellyfin is kept.
        if (collection != null && tracked != null && !string.Equals(tracked.Name, configuredName, StringComparison.Ordinal))
        {
            var others = AllCollections().Where(b => b.Id != collection.Id).Select(b => b.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await RenameCollectionAsync(user, account, collection, UniqueName(configuredName, others), configuredName, cancellationToken).ConfigureAwait(false);
        }

        var createName = configuredName;
        if (collection == null)
        {
            var taken = AllCollections().Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (taken.Contains(configuredName) || taken.Contains(SerializdAccountExtensions.LegacyWatchlistName))
                _logger.LogInformation(
                    "Leaving existing collections named '{Name}' as they are: nothing shows they hold {Username}'s Serializd watchlist alone. "
                    + "Creating this account's own collection instead; delete the old one in Jellyfin once it is no longer needed.",
                    taken.Contains(configuredName) ? configuredName : SerializdAccountExtensions.LegacyWatchlistName, user.Username);
            createName = UniqueName(configuredName, taken);
        }

        var id = await PlaylistReconciler.ReconcileCollectionAsync(
            _collectionManager, _logger, user, collection, createName, desired, sourceWasEmpty).ConfigureAwait(false);

        if (collection == null && id.HasValue)
            SaveTracked(user, account, id.Value, configuredName);
    }

    private List<BoxSet> AllCollections()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
        }).OfType<BoxSet>().ToList();

    /// <summary>
    /// The one-off upgrade rule. Before collections were tracked by id, every account using the
    /// default name wrote to the single collection named "Serializd Watchlist". That collection is
    /// adopted only when it provably belonged to this account alone: this account uses the
    /// default name (a custom name could have been pointed at any collection, so it proves
    /// nothing), it is the only account with watchlist sync on the default name, enabled or not,
    /// since a disabled account may have synced into it earlier (so no one else's shows are in it), exactly one collection has that name, and no account tracks it already.
    /// Otherwise the old collection is left untouched and the caller creates a new one.
    /// </summary>
    private BoxSet? AdoptLegacyCollection(User user, SerializdAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.WatchlistName))
            return null;

        var sharers = Config.SerializdAccounts.Count(a => a.SyncWatchlist && string.IsNullOrWhiteSpace(a.WatchlistName));
        if (sharers != 1)
            return null;

        var legacy = AllCollections()
            .Where(b => string.Equals(b.Name, SerializdAccountExtensions.LegacyWatchlistName, StringComparison.Ordinal))
            .ToList();
        if (legacy.Count != 1)
            return null;

        if (SerializdCollectionStore.IsTrackedByAnotherAccount(legacy[0].Id, user.Id.ToString("N"), account.Email))
            return null;

        _logger.LogInformation("Serializd watchlist: keeping the existing '{Name}' collection as {Username}'s, the only account that could have synced into it",
            legacy[0].Name, user.Username);
        return legacy[0];
    }

    /// <summary>Applies an admin's changed collection name; a rename made in Jellyfin itself is otherwise left alone.</summary>
    private async Task RenameCollectionAsync(User user, SerializdAccount account, BoxSet collection, string name, string configuredName,
        CancellationToken cancellationToken)
    {
        try
        {
            collection.Name = name;
            await _libraryManager.UpdateItemAsync(collection, collection.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
            SaveTracked(user, account, collection.Id, configuredName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not rename the Serializd watchlist collection for {Username} to '{Name}': {Message}",
                user.Username, name, ex.Message);
        }
    }

    private void SaveTracked(User user, SerializdAccount account, Guid collectionId, string appliedName)
    {
        try
        {
            SerializdCollectionStore.Set(user.Id.ToString("N"), account.Email, collectionId, appliedName);
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not record the Serializd watchlist collection for {Username}; the next run may create another one ({Message})",
                user.Username, ex.Message);
        }
    }

    /// <summary>"name", or "name 2", "name 3", ... when a collection already uses it (case-insensitively, as folder names may be).</summary>
    internal static string UniqueName(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private Task ReconcilePlaylistAsync(User user, HashSet<Guid> desired, string name, bool sourceWasEmpty)
        => PlaylistReconciler.ReconcileAsync(
            _playlistManager, _libraryManager, _logger, user, name, desired, sourceWasEmpty);
}
