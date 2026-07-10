using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Scheduled catch-up: logs Jellyfin-watched TV episodes to Serializd for every enabled
/// Serializd account, skipping episodes already logged (real-time or a prior run). The TV
/// counterpart to <see cref="LetterboxdSyncRunner"/>, but simpler: no rewatch logic, no
/// Cloudflare, and dedup is a local key set rather than a round trip.
///
/// Uses its own gate (<see cref="SerializdSyncGate"/>) so a Serializd run never
/// false-serialises against a Letterboxd run, the two hit different origins.
/// </summary>
public class SerializdSyncRunner
{
    private readonly ILogger<SerializdSyncRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public SerializdSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SerializdSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Reads the parent series' TMDb id from an episode. Overridable so the runner's tests
    /// can supply ids without wiring Jellyfin's library-parent graph. Production reads the
    /// resolved Series entity (populated at runtime).
    /// </summary>
    internal static Func<Episode, int?> SeriesTmdbIdReader { get; set; } = DefaultReadSeriesTmdbId;

    private static int? DefaultReadSeriesTmdbId(Episode episode)
    {
        var s = episode.Series?.GetProviderId(MetadataProvider.Tmdb);
        return int.TryParse(s, out var id) ? id : (int?)null;
    }

    public async Task RunForAllAsync(IProgress<double> progress, string source, CancellationToken cancellationToken)
    {
        if (!await SerializdSyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Serializd sync already running, skipping {Source} run", source);
            return;
        }

        try
        {
            var pairs = _userManager.GetUsers()
                .SelectMany(u => Config.GetEnabledSerializdAccountsForUser(u.Id.ToString("N"))
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
                    _logger.LogError("Serializd catch-up failed for {Username} as {Email}: {Message}",
                        user.Username, account.Email, ex.Message);
                }

                processed++;
                if (pairs.Count > 0)
                    progress.Report((double)processed / pairs.Count * 100);
            }

            progress.Report(100);
        }
        finally
        {
            SerializdSyncGate.Instance.Release();
        }
    }

    /// <summary>Runs the catch-up for a single Jellyfin user across all their enabled Serializd accounts.</summary>
    public async Task<bool> TryRunForUserAsync(string userJellyfinId, string source, CancellationToken cancellationToken)
    {
        if (!await SerializdSyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Serializd sync already running, refusing user-triggered start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
            if (user == null) return false;

            var accounts = Config.GetEnabledSerializdAccountsForUser(userJellyfinId).ToList();
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
                    _logger.LogError("Serializd catch-up failed for {Username} as {Email}: {Message}",
                        user.Username, account.Email, ex.Message);
                }
            }

            return true;
        }
        finally
        {
            SerializdSyncGate.Instance.Release();
        }
    }

    private async Task SyncOneAsync(User user, SerializdAccount account, CancellationToken cancellationToken)
    {
        var userId = user.Id.ToString("N");

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            IsPlayed = true,
        });

        // Map played episodes to per-episode records carrying the real watch date + rating,
        // dropping anything we can't key (no series TMDb id / season / episode number).
        var records = new List<EpisodePlay>();
        foreach (var item in episodes)
        {
            if (item is not Episode ep) continue;
            var epRef = SerializdEpisodeMapper.Build(
                SeriesTmdbIdReader(ep), ep.ParentIndexNumber, ep.IndexNumber, ep.IndexNumberEnd);
            if (epRef == null) continue;

            var ud = _userDataManager.GetUserData(user, ep);
            var watchedAt = ud?.LastPlayedDate?.ToUniversalTime() ?? DateTime.UtcNow;
            var rating = SerializdRating.FromJellyfin(ud?.Rating);
            foreach (var n in epRef.EpisodeNumbers)
                records.Add(new EpisodePlay(epRef.ShowTmdbId, epRef.SeasonNumber, n, watchedAt, rating));
        }

        // Anything new to do? (either watched-marking or a dated log)
        var needsWatched = GroupNewEpisodes(
            records.Select(r => (r.Show, r.Season, r.Episode)),
            (s, se, e) => SerializdSyncHistory.Has(userId, s, se, e, SerializdSyncHistory.KindWatched));
        var needsLog = records
            .Where(r => !SerializdSyncHistory.Has(userId, r.Show, r.Season, r.Episode, SerializdSyncHistory.KindLog))
            .ToList();

        if (needsWatched.Count == 0 && needsLog.Count == 0)
        {
            _logger.LogDebug("Serializd catch-up: nothing new for {Username} as {Email}", user.Username, account.Email);
            return;
        }

        using var service = await SerializdServiceFactory
            .CreateAuthenticatedAsync(account.Email, account.Password, _logger)
            .ConfigureAwait(false);

        // 1. Watched-status marking, batched per (show, season).
        foreach (var ((show, season), epNums) in needsWatched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var seasonId = await service.ResolveSeasonIdAsync(show, season).ConfigureAwait(false);
                if (seasonId == null)
                {
                    _logger.LogWarning("Serializd has no season {Season} for TMDb show {Show}, skipping", season, show);
                    continue;
                }

                await service.LogEpisodesAsync(show, seasonId.Value, epNums).ConfigureAwait(false);
                foreach (var n in epNums)
                    SerializdSyncHistory.Record(userId, show, season, n);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd catch-up: failed marking watched TMDb {Show} S{Season} for {Username}: {Message}",
                    show, season, user.Username, ex.Message);
            }
        }

        // 2. Dated Diary logs, one per episode, backdated to the real watch date.
        var logged = 0;
        foreach (var r in needsLog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var seasonId = await service.ResolveSeasonIdAsync(r.Show, r.Season).ConfigureAwait(false);
                if (seasonId == null) continue;

                await service.CreateEpisodeLogAsync(r.Show, seasonId.Value, r.Episode, r.WatchedAtUtc, r.Rating, isRewatch: false)
                    .ConfigureAwait(false);
                SerializdSyncHistory.Record(userId, r.Show, r.Season, r.Episode, SerializdSyncHistory.KindLog);
                logged++;

                // Be polite during a large first-time backfill.
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd catch-up: failed logging TMDb {Show} S{Season}E{Episode} for {Username}: {Message}",
                    r.Show, r.Season, r.Episode, user.Username, ex.Message);
            }
        }

        if (logged > 0)
            _logger.LogInformation("Serializd catch-up: created {Count} dated diary logs for {Username} as {Email}",
                logged, user.Username, account.Email);
    }

    private readonly record struct EpisodePlay(int Show, int Season, int Episode, DateTime WatchedAtUtc, int? Rating);

    /// <summary>
    /// Pure: collapse a flat list of (show, season, episode) plays into per-(show, season)
    /// episode lists, dropping any already logged and de-duplicating. Exposed for tests.
    /// </summary>
    internal static Dictionary<(int Show, int Season), List<int>> GroupNewEpisodes(
        IEnumerable<(int Show, int Season, int Episode)> plays,
        Func<int, int, int, bool> alreadyLogged)
    {
        var result = new Dictionary<(int, int), List<int>>();
        var seen = new HashSet<(int, int, int)>();

        foreach (var (show, season, ep) in plays)
        {
            if (alreadyLogged(show, season, ep)) continue;
            if (!seen.Add((show, season, ep))) continue;

            var key = (show, season);
            if (!result.TryGetValue(key, out var list))
            {
                list = new List<int>();
                result[key] = list;
            }

            list.Add(ep);
        }

        foreach (var list in result.Values)
            list.Sort();

        return result;
    }
}
