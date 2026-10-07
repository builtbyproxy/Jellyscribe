using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class PlaybackHandler : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackHandler> _logger;
    private readonly MediaBrowser.Model.Activity.IActivityManager? _activityManager;

    // activityManager is optional so existing construction sites (and tests) keep
    // working; a null only skips the auth-breaker admin notification.
    public PlaybackHandler(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILogger<PlaybackHandler> logger,
        MediaBrowser.Model.Activity.IActivityManager? activityManager = null)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _activityManager = activityManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            await HandlePlaybackStoppedAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling playback stopped event");
        }
    }

    // internal (not private) so tests can drive the handler without going through
    // the ISessionManager event raise machinery; production callers still go via OnPlaybackStopped.
    internal async Task HandlePlaybackStoppedAsync(PlaybackStopEventArgs e)
    {
        if (e.Item == null)
            return;

        if (!e.PlayedToCompletion)
            return;

        if (e.Users == null || e.Users.Count == 0)
            return;

        // TV episodes scrobble to Serializd; films fall through to the Letterboxd path below.
        // The two are fully isolated: a Serializd failure never touches Letterboxd and vice versa.
        if (e.Item is Episode episode)
        {
            await HandleEpisodeAsync(episode, e).ConfigureAwait(false);
            return;
        }

        if (!e.Item.IsMovie())
            return;

        foreach (var user in e.Users)
        {
            var tmdbIdStr = e.Item.GetProviderId(MetadataProvider.Tmdb);
            if (!int.TryParse(tmdbIdStr, out var tmdbId))
            {
                _logger.LogWarning("Movie {Title} has no TMDb ID, skipping Letterboxd sync", e.Item.Name);
                continue;
            }

            // Fan out across all enabled Letterboxd accounts for this Jellyfin user.
            // Shared TV-user case (e.g. Lachlan + Deb both on the same Jellyfin login)
            // means each LB account independently gets the diary entry. One failing
            // account never blocks the others; rating mirroring is identical for both
            // since Jellyfin only stores one rating per (user, film).
            var accounts = Config.GetEnabledAccountsForUser(user.Id.ToString("N")).ToList();
            if (accounts.Count == 0)
                continue;

            foreach (var account in accounts)
            {
                if (LibraryExclusion.IsExcluded(_libraryManager, e.Item, account.ExcludedLibraryIds, _logger))
                {
                    _logger.LogInformation(
                        "Skipping real-time sync of {Title} for {LbUser}: in a library this account excludes",
                        e.Item.Name, account.LetterboxdUsername);
                    continue;
                }

                var breakerUserId = user.Id.ToString("N");
                if (AuthBreaker.BlocksLogin(breakerUserId, account.LetterboxdUsername))
                {
                    _logger.LogInformation(
                        "Skipping real-time sync of {Title} for {LbUser}: auth breaker open; the scheduled task catches up once credentials are re-saved",
                        e.Item.Name, account.LetterboxdUsername);
                    // Record the skip so sync history explains why this watch has no
                    // real-time entry, matching the scheduled runner's behavior.
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Skipped,
                        Error = $"Sync paused for {account.LetterboxdUsername}: login failing; re-save credentials to resume",
                        Source = "playback",
                        Account = account.LetterboxdUsername
                    });
                    continue;
                }

                _logger.LogInformation("Syncing {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} as {LbUser}",
                    e.Item.Name, tmdbId, user.Username, account.LetterboxdUsername);

                // Authentication gets its own try/catch so only genuine login failures
                // feed the breaker; errors later in the sync (lookups, Cloudflare) fall
                // through to the existing catch below and never count against auth.
                ILetterboxdService authedService;
                try
                {
                    authedService = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                        account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Auth failed for {Username} as {LbUser}: {Message}",
                        user.Username, account.LetterboxdUsername, ex.Message);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        // Sanitized: auth error messages can echo response-body fragments,
                        // and sync history renders in the dashboard.
                        Error = AuthBreaker.Sanitize(ex.Message),
                        Source = "playback",
                        Account = account.LetterboxdUsername
                    });
                    if (AuthBreaker.RecordFailure(breakerUserId, account.LetterboxdUsername, ex.Message))
                        await AuthBreaker.NotifyOpenedAsync(_activityManager, user.Id, account.LetterboxdUsername, _logger).ConfigureAwait(false);
                    continue;
                }

                AuthBreaker.RecordSuccess(breakerUserId, account.LetterboxdUsername);

                try
                {
                    using var service = authedService;
                    await SyncFilmAsync(service, user, account, e.Item, tmdbId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} as {LbUser}: {Message}",
                        e.Item.Name, tmdbId, user.Username, account.LetterboxdUsername, ex.Message);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = e.Item.Name,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Account = account.LetterboxdUsername,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Error = ex.Message,
                        Source = "playback",
                        PermanentFailure = ex is FilmNotFoundException
                    });
                }
            }
        }
    }

    /// <summary>
    /// Logs one finished film to one account, under the same per-film lock and the same duplicate
    /// rules as the scheduled sync: the viewing date is today in the server's time zone (the day
    /// the scheduled sync derives from LastPlayedDate), an entry already on the diary for that day
    /// is a skip, and a recent sync in local history suppresses a second entry even when the diary
    /// does not show it yet.
    /// </summary>
    private async Task SyncFilmAsync(ILetterboxdService service, Jellyfin.Database.Implementations.Entities.User user,
        Account account, MediaBrowser.Controller.Entities.BaseItem item, int tmdbId)
    {
        var lbAccount = account.LetterboxdUsername;
        var username = user.Username ?? string.Empty;
        using var filmLock = await FilmSyncLock.AcquireAsync(user.Id.ToString("N"), lbAccount, tmdbId).ConfigureAwait(false);

        var film = await service.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
        var viewingDate = Helpers.ToLocalViewingDate(DateTime.UtcNow);
        var viewingDateStr = viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Throws when the check fails; the caller records a retryable failure instead of posting.
        var diaryInfo = await service.GetDiaryInfoAsync(film.FilmId, lbAccount).ConfigureAwait(false);

        if (Helpers.IsDuplicate(diaryInfo.LastDate, viewingDate))
        {
            _logger.LogInformation("{Title} already logged on Letterboxd ({LbUser}) for {Date}, skipping",
                item.Name, lbAccount, viewingDateStr);
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = item.Name,
                FilmSlug = film.Slug,
                TmdbId = tmdbId,
                Username = username,
                Account = lbAccount,
                Timestamp = DateTime.UtcNow,
                ViewingDate = viewingDate,
                Status = SyncStatus.Skipped,
                Error = SyncHistory.AlreadyOnDiaryError,
                Source = "playback"
            });
            return;
        }

        var localLastSync = SyncHistory.GetLastSuccessfulSyncDate(username, tmdbId, lbAccount);
        if (localLastSync.HasValue && !Helpers.IsRewatch(localLastSync, viewingDate))
        {
            var lastSyncStr = localLastSync.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _logger.LogInformation("{Title}: local history shows a sync to {LbUser} on {Date}, suppressing potential duplicate",
                item.Name, lbAccount, lastSyncStr);
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = item.Name,
                FilmSlug = film.Slug,
                TmdbId = tmdbId,
                Username = username,
                Account = lbAccount,
                Timestamp = DateTime.UtcNow,
                ViewingDate = viewingDate,
                Status = SyncStatus.Skipped,
                Error = $"{SyncHistory.BackstopErrorPrefix}{lastSyncStr}, suppressing potential duplicate",
                Source = "playback"
            });
            return;
        }

        bool isRewatch = Helpers.IsRewatch(diaryInfo.LastDate, viewingDate);
        var userData = _userDataManager.GetUserData(user, item);
        bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);
        double? lbRating = Helpers.MapRating(userData?.Rating);

        await service.MarkAsWatchedAsync(film.Slug, film.FilmId, viewingDate, liked,
            film.ProductionId, isRewatch, lbRating).ConfigureAwait(false);

        var action = isRewatch ? "Logged rewatch of" : "Logged";
        _logger.LogInformation("{Action} {Title} to Letterboxd diary for {Username} as {LbUser}",
            action, item.Name, username, lbAccount);
        SyncHistory.Record(new SyncEvent
        {
            FilmTitle = item.Name,
            FilmSlug = film.Slug,
            TmdbId = tmdbId,
            Username = username,
            Account = lbAccount,
            Timestamp = DateTime.UtcNow,
            ViewingDate = viewingDate,
            Status = isRewatch ? SyncStatus.Rewatch : SyncStatus.Success,
            Source = "playback"
        });
    }

    // internal so tests can drive the episode path directly, same as HandlePlaybackStoppedAsync.
    internal async Task HandleEpisodeAsync(Episode episode, PlaybackStopEventArgs e)
    {
        var seriesTmdbId = SeriesTmdbIdReader(episode);
        var epRef = SerializdEpisodeMapper.Build(
            seriesTmdbId, episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd);

        if (epRef == null)
        {
            _logger.LogWarning(
                "Episode {Series} S{Season}E{Episode} lacks a series TMDb id / season / episode number, skipping Serializd sync",
                episode.SeriesName ?? episode.Name, episode.ParentIndexNumber, episode.IndexNumber);
            return;
        }

        foreach (var user in e.Users)
        {
            var accounts = Config.GetEnabledSerializdAccountsForUser(user.Id.ToString("N")).ToList();
            if (accounts.Count == 0)
                continue;

            foreach (var account in accounts)
            {
                if (LibraryExclusion.IsExcluded(_libraryManager, episode, account.ExcludedLibraryIds, _logger))
                {
                    _logger.LogInformation(
                        "Skipping real-time Serializd sync of {Series} S{Season}E{Episode} for {Account}: in a library this account excludes",
                        episode.SeriesName ?? episode.Name, epRef.SeasonNumber, episode.IndexNumber, LogRedaction.AccountTag(account.Email));
                    continue;
                }

                try
                {
                    using var service = await SerializdServiceFactory
                        .CreateAuthenticatedAsync(account.Email, account.Password, _logger)
                        .ConfigureAwait(false);

                    var target = await SerializdSeasonFallback
                        .ResolveAsync(service, epRef.ShowTmdbId, epRef.SeasonNumber,
                            () => SerializdSeasonFallback.SeasonLengthsReader(episode.Series))
                        .ConfigureAwait(false);

                    if (target == null)
                    {
                        _logger.LogWarning(
                            "Serializd has no season {Season} for TMDb show {TmdbId} ({Series}), skipping",
                            epRef.SeasonNumber, epRef.ShowTmdbId, episode.SeriesName);
                        continue;
                    }

                    if (epRef.EpisodeNumbers.Any(n => target.EpisodeFor(n) == null))
                    {
                        _logger.LogWarning(
                            "Serializd's season 1 of {Series} (TMDb {TmdbId}) is too short for S{Season}E{Episode}, skipping",
                            episode.SeriesName, epRef.ShowTmdbId, epRef.SeasonNumber, epRef.EpisodeNumbers[^1]);
                        continue;
                    }

                    var seasonId = target.SeasonId;
                    var serializdEpisodes = epRef.EpisodeNumbers.Select(n => target.EpisodeFor(n)!.Value).ToList();
                    if (target.EpisodeOffset > 0)
                        _logger.LogInformation(
                            "Serializd lists {Series} as a single season; logging S{Season}E{Episode} as S1E{Absolute}",
                            episode.SeriesName, epRef.SeasonNumber, epRef.EpisodeNumbers[0], serializdEpisodes[0]);

                    var userId = user.Id.ToString("N");

                    // 1. Mark the episodes watched (populates Shows/Stats).
                    await service.LogEpisodesAsync(epRef.ShowTmdbId, seasonId, serializdEpisodes)
                        .ConfigureAwait(false);
                    foreach (var n in epRef.EpisodeNumbers)
                        SerializdSyncHistory.Record(userId, account.Email, epRef.ShowTmdbId, epRef.SeasonNumber, n);

                    // 2. Create a dated Diary log per episode, stamped now (the watch just
                    // finished), carrying the episode's Jellyfin rating if it has one. A second
                    // finish of the same episode is logged as a rewatch.
                    var rating = SerializdRating.FromJellyfin(_userDataManager.GetUserData(user, episode)?.Rating);
                    foreach (var n in epRef.EpisodeNumbers)
                    {
                        var isRewatch = SerializdSyncHistory.Has(
                            userId, account.Email, epRef.ShowTmdbId, epRef.SeasonNumber, n, SerializdSyncHistory.KindLog);
                        await service.CreateEpisodeLogAsync(
                            epRef.ShowTmdbId, seasonId, target.EpisodeFor(n)!.Value, DateTime.UtcNow, rating, isRewatch)
                            .ConfigureAwait(false);
                        SerializdSyncHistory.Record(
                            userId, account.Email, epRef.ShowTmdbId, epRef.SeasonNumber, n, SerializdSyncHistory.KindLog);

                        SerializdActivity.Record(new SyncEvent
                        {
                            FilmTitle = $"{episode.SeriesName} · S{epRef.SeasonNumber}E{n}",
                            TmdbId = epRef.ShowTmdbId,
                            Username = user.Username ?? string.Empty,
                            Timestamp = DateTime.UtcNow,
                            // UTC, matching SerializdSyncRunner's WatchedAtUtc: a local-date stamp
                            // here would show a different calendar day near midnight than the
                            // catch-up path records for the same logical watch.
                            ViewingDate = DateTime.UtcNow.Date,
                            Status = isRewatch ? SyncStatus.Rewatch : SyncStatus.Success,
                            Source = "playback",
                        });
                    }

                    _logger.LogInformation(
                        "Logged {Series} S{Season} episodes {Episodes} (TMDb:{TmdbId}) to Serializd for {Username} as {Account}",
                        episode.SeriesName, epRef.SeasonNumber, string.Join(",", epRef.EpisodeNumbers),
                        epRef.ShowTmdbId, user.Username, LogRedaction.AccountTag(account.Email));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Failed to log {Series} S{Season}E{Episode} (TMDb:{TmdbId}) to Serializd for {Username}: {Message}",
                        episode.SeriesName, epRef.SeasonNumber, episode.IndexNumber, epRef.ShowTmdbId,
                        user.Username, ex.Message);
                    SerializdActivity.Record(new SyncEvent
                    {
                        FilmTitle = episode.SeriesName ?? episode.Name ?? "Episode",
                        TmdbId = epRef.ShowTmdbId,
                        Username = user.Username ?? string.Empty,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Failed,
                        Error = ex.Message,
                        Source = "playback",
                    });
                }
            }
        }
    }

    // Reads the parent series' TMDb id from an episode. Overridable so tests can supply
    // it without wiring Jellyfin's library-parent graph (mirrors the factory OverrideForTesting
    // convention); production delegates to the shared reader on SerializdSyncRunner so the
    // real-time and catch-up paths use identical logic.
    internal static Func<Episode, int?> SeriesTmdbIdReader { get; set; } =
        Serializd.SerializdSyncRunner.ReadSeriesTmdbId;

    public void Dispose()
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }
}

internal static class ItemExtensions
{
    public static bool IsMovie(this MediaBrowser.Controller.Entities.BaseItem item)
        => item.GetBaseItemKind() == BaseItemKind.Movie;
}
