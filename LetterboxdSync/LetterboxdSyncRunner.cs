using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Performs Letterboxd diary sync. Used by both the scheduled task and the user-triggered
/// API endpoint, with a global semaphore so SyncProgress can only be driven by one caller
/// at a time.
/// </summary>
public class LetterboxdSyncRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LetterboxdSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly MediaBrowser.Model.Activity.IActivityManager? _activityManager;

    // activityManager is optional so existing construction sites (and tests) keep
    // working; the server's DI supplies the real one, and a null just skips the
    // auth-breaker admin notification, never the breaker itself.
    public LetterboxdSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        MediaBrowser.Model.Activity.IActivityManager? activityManager = null)
    {
        _activityManager = activityManager;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LetterboxdSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    public static bool IsRunning => SyncGate.IsRunning;

    /// <summary>
    /// After this many consecutive permanent failures for the same film (Letterboxd has no film
    /// for its TMDb id), stop retrying it. Transient errors (Cloudflare 403, rate limits, an
    /// outage) never count, see <see cref="SyncHistory.GetConsecutiveFailureCount(string, int, string?)"/>.
    /// </summary>
    internal const int MaxConsecutiveSyncFailures = 3;

    /// <summary>
    /// A film whose other failures (an error from Letterboxd on that film alone, while other films
    /// sync) keep coming back is also given up on, but only after this many in a row spread over
    /// at least <see cref="MinTransientFailureDays"/> distinct days, so a bad week of Letterboxd
    /// trouble cannot do it. Failures from outage runs never count.
    /// </summary>
    internal const int MaxTransientSyncFailures = 10;

    internal const int MinTransientFailureDays = 7;

    /// <summary>
    /// A run in which at least this many films were tried, every one failed, and at least one
    /// failure was transient (a block, an error status) is treated as an account or service
    /// outage: none of its failures count toward abandonment. A run whose films all failed only
    /// with "not found" is not an outage; it is the usual state once everything findable has
    /// synced, and those films must still reach the abandonment threshold.
    /// </summary>
    internal const int OutageMinAttempts = 2;

    /// <summary>
    /// After this many films in a row are refused with a block (a Cloudflare 403 or challenge),
    /// the rest of the account's run waits for the next one. Each blocked film already costs
    /// about a minute of backoff, so going on would hold the sync for hours and fail every film.
    /// </summary>
    internal const int MaxConsecutiveBlocks = 3;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public async Task RunForAllAsync(IProgress<double> progress, string source, CancellationToken cancellationToken)
    {
        if (!await SyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Sync already running, skipping scheduled run");
            return;
        }

        try
        {
            // Fan out across every (user, account) pair. Each account's sync is
            // independent so one failing or rate-limited account never blocks the others.
            var pairs = _userManager.GetUsers()
                .SelectMany(u => Config.GetEnabledAccountsForUser(u.Id.ToString("N"))
                    .Select(a => (User: u, Account: a)))
                .ToList();

            var processed = 0;
            foreach (var (user, account) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOneUserAsync(user, account, source, cancellationToken).ConfigureAwait(false);
                processed++;
                if (pairs.Count > 0)
                    progress.Report((double)processed / pairs.Count * 100);
            }

            progress.Report(100);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    /// <summary>
    /// Run sync for a single user. When letterboxdUsername is null/empty, fan out
    /// across all enabled accounts for the user. Otherwise target only that account.
    /// Returns false if another sync is already running, the user is unknown, or
    /// they have no matching enabled account.
    /// </summary>
    public async Task<bool> TryRunForUserAsync(
        string userJellyfinId,
        string source,
        IProgress<double> progress,
        CancellationToken cancellationToken,
        string? letterboxdUsername = null)
    {
        if (!await SyncGate.Instance.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Sync already running, refusing user-triggered start for {UserId}", userJellyfinId);
            return false;
        }

        try
        {
            var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found, cannot start sync", userJellyfinId);
                return false;
            }

            List<Account> accounts;
            if (!string.IsNullOrEmpty(letterboxdUsername))
            {
                var single = Config.FindAccount(userJellyfinId, letterboxdUsername);
                if (single == null)
                {
                    _logger.LogWarning("No enabled Letterboxd account {LbUser} for {Username}, cannot start sync",
                        letterboxdUsername, user.Username);
                    return false;
                }
                accounts = new List<Account> { single };
            }
            else
            {
                accounts = Config.GetEnabledAccountsForUser(userJellyfinId).ToList();
                if (accounts.Count == 0)
                {
                    _logger.LogWarning("No enabled Letterboxd accounts for {Username}, cannot start sync", user.Username);
                    return false;
                }
            }

            var processed = 0;
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOneUserAsync(user, account, source, cancellationToken).ConfigureAwait(false);
                processed++;
                progress.Report((double)processed / accounts.Count * 100);
            }
            return true;
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    private async Task SyncOneUserAsync(User user, Account account, string source, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Letterboxd sync for {Username} (source={Source})", user.Username, source);
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Letterboxd Sync", "Authenticating");

        List<BaseItem> movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            IsPlayed = true,
        }).ToList();

        if (movies.Count == 0)
        {
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        if (account.EnableDateFilter)
        {
            var cutoff = DateTime.UtcNow.AddDays(-account.DateFilterDays);
            movies = movies.Where(m =>
            {
                var ud = _userDataManager.GetUserData(user, m);
                return ud?.LastPlayedDate.HasValue == true && ud.LastPlayedDate!.Value >= cutoff;
            }).ToList();
        }

        if (movies.Count == 0)
        {
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        // Drop films in libraries this account excludes (issue #124). Runs after the date
        // filter so the library lookup only touches the narrowed set. Logged, not recorded in
        // sync history: an excluded library is the user's choice, not a failure.
        if (account.ExcludedLibraryIds.Count > 0)
        {
            var skippedExcluded = 0;
            movies = movies.Where(m =>
            {
                if (!LibraryExclusion.IsExcluded(_libraryManager, m, account.ExcludedLibraryIds, _logger)) return true;
                skippedExcluded++;
                return false;
            }).ToList();

            if (skippedExcluded > 0)
                _logger.LogInformation(
                    "Skipping {Count} films for {Username} as {LbUser}: in a library this account excludes",
                    skippedExcluded, user.Username, account.LetterboxdUsername);

            if (movies.Count == 0)
            {
                SyncProgress.Complete(SyncProgress.TrackLetterboxd);
                return;
            }
        }

        // Skip films marked played on Jellyfin with no plausible LastPlayedDate: either
        // missing entirely, or an epoch-adjacent value (e.g. 1970-01-01) some clients send
        // when marking an item watched manually without a real timestamp (issue #106). In
        // either case there's no real watch date to log: viewingDate would otherwise fall
        // back to DateTime.Now (today) or literally 1970, which drifts forward on every run
        // and slips past every same-date duplicate check, posting a phantom rewatch to
        // Letterboxd roughly every other day. Wait until Jellyfin records an actual play
        // date. This also closes the import-then-export loop from issue #32, DiaryImportTask
        // marks films played without a LastPlayedDate.
        var skippedNoPlayDate = 0;
        movies = movies.Where(m =>
        {
            var ud = _userDataManager.GetUserData(user, m);
            if (Helpers.HasPlausibleWatchDate(ud?.LastPlayedDate)) return true;
            skippedNoPlayDate++;
            return false;
        }).ToList();

        if (skippedNoPlayDate > 0)
            _logger.LogInformation(
                "Skipping {Count} films for {Username}: marked played but no plausible LastPlayedDate (no real watch date to log)",
                skippedNoPlayDate, user.Username);

        if (movies.Count == 0)
        {
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        // A film without a TMDb id can never be matched on Letterboxd. Say so in one line, and
        // record it in history once rather than on every run.
        var lbAccount = account.LetterboxdUsername;
        var noTmdbId = movies.Where(m => !HasTmdbId(m)).ToList();
        if (noTmdbId.Count > 0)
        {
            _logger.LogInformation(
                "Skipping {Count} films for {Username} as {LbUser}: no TMDb ID on the Jellyfin item, so Letterboxd cannot match them ({Titles})",
                noTmdbId.Count, user.Username, lbAccount, string.Join(", ", noTmdbId.Take(5).Select(m => m.Name)) + (noTmdbId.Count > 5 ? ", ..." : string.Empty));
            foreach (var movie in noTmdbId)
            {
                if (SyncHistory.HasNoTmdbIdSkip(user.Username ?? string.Empty, movie.Name, lbAccount)) continue;
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = movie.Name,
                    Username = user.Username ?? string.Empty,
                    Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Skipped,
                    Error = SyncHistory.NoTmdbIdError,
                    Source = source,
                    Account = lbAccount
                });
            }

            var excluded = noTmdbId.ToHashSet();
            movies = movies.Where(m => !excluded.Contains(m)).ToList();
            if (movies.Count == 0)
            {
                SyncProgress.Complete(SyncProgress.TrackLetterboxd);
                return;
            }
        }

        // Abandon films Letterboxd keeps saying it does not have. BuildSyncQueue pushes
        // previously-failed films to the head of the queue, so such a film would retry forever.
        // After MaxConsecutiveSyncFailures permanent failures for this account, leave it alone.
        var abandonedFailures = 0;
        movies = movies.Where(m =>
        {
            if (!int.TryParse(m.GetProviderId(MetadataProvider.Tmdb), out var tmdbId)) return true;
            if (ShouldAbandon(SyncHistory.GetFailureStreak(user.Username ?? string.Empty, tmdbId, lbAccount)))
            {
                abandonedFailures++;
                return false;
            }
            return true;
        }).ToList();

        if (abandonedFailures > 0)
            _logger.LogInformation(
                "Skipping {Count} films for {Username} as {LbUser}: failing on every run (not found on Letterboxd {Threshold}+ times, or erroring for {Days}+ days), no longer retrying",
                abandonedFailures, user.Username, lbAccount, MaxConsecutiveSyncFailures, MinTransientFailureDays);

        if (movies.Count == 0)
        {
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        // Filter out anything we already successfully synced for this exact viewing date,
        // so we don't burn Cloudflare quota re-checking films that are definitely on Letterboxd.
        // Sort what's left so previously-failed films come first, if rate limits hit,
        // we make progress on the backlog instead of repeatedly retrying the same head of queue.
        int preFilterCount = movies.Count;
        int locallySkipped = 0;

        if (account.SkipPreviouslySynced)
        {
            var candidates = movies.Select(m =>
            {
                int? tid = int.TryParse(m.GetProviderId(MetadataProvider.Tmdb), out var v) ? v : null;
                var ud = _userDataManager.GetUserData(user, m);
                return (Item: m, TmdbId: tid, ViewingDate: ViewingDateFor(ud?.LastPlayedDate));
            });

            var (queue, skippedLocally) = BuildSyncQueue(
                candidates,
                user.Username ?? string.Empty,
                (u, t, d) => SyncHistory.WasSuccessfullySynced(u, t, d, lbAccount),
                (u, t) => SyncHistory.GetLastStatusForFilm(u, t, lbAccount));
            movies = queue;
            locallySkipped = skippedLocally;
        }

        if (locallySkipped > 0)
            _logger.LogInformation("Skipping {Count} of {Total} films for {Username}: already in local sync history",
                locallySkipped, preFilterCount, user.Username);

        if (movies.Count == 0)
        {
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        var breakerUserId = user.Id.ToString("N");
        if (AuthBreaker.BlocksLogin(breakerUserId, account.LetterboxdUsername))
        {
            var since = AuthBreaker.GetState(breakerUserId, account.LetterboxdUsername)?.FirstFailureUtc;
            _logger.LogInformation(
                "Skipping Letterboxd sync for {Username}: auth breaker open (login failing since {Since:u}); re-save credentials to resume, or wait for the daily retry",
                account.LetterboxdUsername, since);
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = $"Account {account.LetterboxdUsername} paused",
                Username = user.Username ?? string.Empty,
                Account = lbAccount,
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Skipped,
                Error = $"Login failing since {since:yyyy-MM-dd}; sync paused until credentials are re-saved (one login is retried each day for a week)",
                Source = source
            });
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        ILetterboxdService service;
        try
        {
            service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Auth failed for {Username}: {Message}", user.Username, ex.Message);
            // Auth failures never reach SyncHistory.Record (we bail before the film loop),
            // so telemetry needs its own hook here. Classify rather than hardcode auth:
            // a Cloudflare 403 on /sign-in/ should count as cloudflare, not auth.
            TelemetryService.RecordError(TelemetryService.Classify(ex.Message));
            if (AuthBreaker.RecordFailure(breakerUserId, account.LetterboxdUsername, ex.Message))
                await AuthBreaker.NotifyOpenedAsync(_activityManager, user.Id, account.LetterboxdUsername, _logger).ConfigureAwait(false);
            SyncProgress.Complete(SyncProgress.TrackLetterboxd);
            return;
        }

        AuthBreaker.RecordSuccess(breakerUserId, account.LetterboxdUsername);

        using var _ = service;

        var synced = 0;
        var skipped = 0;
        var failed = 0;
        var attempted = 0;
        var blocksInARow = 0;
        var position = 0;
        var failures = new List<SyncEvent>();

        SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, "Syncing films");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, movies.Count);

        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            position++;

            // Films without a TMDb id were set aside before the loop.
            if (!int.TryParse(movie.GetProviderId(MetadataProvider.Tmdb), out var tmdbId))
                continue;

            attempted++;
            try
            {
                using var filmLock = await FilmSyncLock.AcquireAsync(breakerUserId, lbAccount, tmdbId, cancellationToken)
                    .ConfigureAwait(false);

                var film = await LookupPacedAsync(service, tmdbId, cancellationToken).ConfigureAwait(false);

                var userData = _userDataManager.GetUserData(user, movie);
                var viewingDate = ViewingDateFor(userData?.LastPlayedDate);
                var viewingDateStr = viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // Throws when the check itself fails, which lands in the catch below as a
                // retryable failure: an unanswered check must never be read as "not logged".
                var diaryInfo = await service.GetDiaryInfoAsync(film.FilmId, lbAccount, cancellationToken).ConfigureAwait(false);
                blocksInARow = 0;
                if (Helpers.IsDuplicate(diaryInfo.LastDate, viewingDate))
                {
                    _logger.LogInformation("Skipping {Title} (TMDb:{TmdbId}): already on Letterboxd diary for {Date}",
                        movie.Name, tmdbId, viewingDateStr);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = movie.Name,
                        FilmSlug = film.Slug,
                        TmdbId = tmdbId,
                        Username = user.Username ?? string.Empty,
                        Account = lbAccount,
                        Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate,
                        Status = SyncStatus.Skipped,
                        Error = SyncHistory.AlreadyOnDiaryError,
                        Source = source
                    });
                    skipped++;
                    SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);
                    continue;
                }

                // Local-history backstop: if our own log has a recent successful sync for this
                // account that is not far enough back to count as a real rewatch, refuse rather
                // than risk a duplicate (Letterboxd's diary can lag behind a write just made).
                var localLastSync = SyncHistory.GetLastSuccessfulSyncDate(user.Username ?? string.Empty, tmdbId, lbAccount);
                if (localLastSync.HasValue && !Helpers.IsRewatch(localLastSync, viewingDate))
                {
                    var lastSyncStr = localLastSync.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    _logger.LogInformation("Skipping {Title} (TMDb:{TmdbId}): local history shows prior sync on {Date}, suppressing potential duplicate",
                        movie.Name, tmdbId, lastSyncStr);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = movie.Name,
                        FilmSlug = film.Slug,
                        TmdbId = tmdbId,
                        Username = user.Username ?? string.Empty,
                        Account = lbAccount,
                        Timestamp = DateTime.UtcNow,
                        ViewingDate = viewingDate,
                        Status = SyncStatus.Skipped,
                        Error = $"{SyncHistory.BackstopErrorPrefix}{lastSyncStr}, suppressing potential duplicate",
                        Source = source
                    });
                    skipped++;
                    SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);
                    continue;
                }

                // Same rule as real-time sync: an earlier diary entry more than a day before this
                // viewing makes it a rewatch.
                bool isRewatch = Helpers.IsRewatch(diaryInfo.LastDate, viewingDate);
                bool liked = account.SyncFavorites && (userData?.IsFavorite ?? false);
                double? lbRating = Helpers.MapRating(userData?.Rating);

                await service.MarkAsWatchedAsync(film.Slug, film.FilmId, viewingDate, liked,
                    film.ProductionId, isRewatch, lbRating, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("{Action} {Title} (TMDb:{TmdbId}) to Letterboxd for {Username} as {LbUser} on {Date}",
                    isRewatch ? "Logged rewatch of" : "Logged", movie.Name, tmdbId, user.Username, lbAccount, viewingDateStr);
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = movie.Name,
                    FilmSlug = film.Slug,
                    TmdbId = tmdbId,
                    Username = user.Username ?? string.Empty,
                    Account = lbAccount,
                    Timestamp = DateTime.UtcNow,
                    ViewingDate = viewingDate,
                    Status = isRewatch ? SyncStatus.Rewatch : SyncStatus.Success,
                    Source = source
                });
                synced++;
                SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown or a cancelled task, not a failure of this film: record nothing.
                SyncProgress.Complete(SyncProgress.TrackLetterboxd);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to sync {Title} (TMDb:{TmdbId}) for {Username} as {LbUser}: {Message}",
                    movie.Name, tmdbId, user.Username, lbAccount, ex.Message);
                var failure = new SyncEvent
                {
                    FilmTitle = movie.Name,
                    TmdbId = tmdbId,
                    Username = user.Username ?? string.Empty,
                    Account = lbAccount,
                    Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed,
                    Error = ex.Message,
                    Source = source,
                    PermanentFailure = ex is FilmNotFoundException
                };
                SyncHistory.Record(failure);
                failures.Add(failure);
                failed++;
                SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);

                if (account.StopOnFailure)
                {
                    _logger.LogWarning("Stop-on-failure enabled for {Username}: halting after {Synced} synced, {Skipped} skipped, {Failed} failed",
                        user.Username, synced, skipped, failed);
                    break;
                }

                blocksInARow = SyncErrors.IsBlock(ex) ? blocksInARow + 1 : 0;
                if (blocksInARow >= MaxConsecutiveBlocks)
                {
                    var remaining = movies.Count - position;
                    _logger.LogWarning(
                        "Letterboxd blocked {Count} films in a row for {Username} as {LbUser}; pausing this account's sync until the next run ({Remaining} films left)",
                        blocksInARow, user.Username, lbAccount, remaining);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = $"Account {lbAccount} paused",
                        Username = user.Username ?? string.Empty,
                        Account = lbAccount,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Skipped,
                        Error = $"Letterboxd blocked {blocksInARow} requests in a row (usually Cloudflare, or a long rate limit); the other {remaining} films wait for the next sync",
                        Source = source
                    });
                    break;
                }
            }
        }

        // Every film tried failed and Letterboxd was erroring: a bad run for the service or this
        // account says nothing about the films, so none of these failures may count toward
        // abandoning a film, including any "not found" answers given during it.
        if (attempted >= OutageMinAttempts && failed == attempted && failures.Any(f => !f.PermanentFailure))
        {
            _logger.LogWarning("Every one of the {Count} films tried for {Username} as {LbUser} failed; treating the run as an outage, so none of them counts toward giving up on a film",
                attempted, user.Username, lbAccount);
            SyncHistory.MarkOutage(failures);
        }

        _logger.LogInformation("Letterboxd sync complete for {Username}: {Synced} synced, {Skipped} skipped (+{LocalSkipped} skipped locally), {Failed} failed",
            user.Username, synced, skipped, locallySkipped, failed);
        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    /// <summary>The 3-5 s pause between films' Letterboxd requests. A test hook replaces it.</summary>
    internal static Func<CancellationToken, Task> FilmPause { get; set; }
        = ct => Task.Delay(3000 + Random.Shared.Next(2000), ct);

    /// <summary>
    /// Looks the film up and paces the film's requests. On the website the lookup itself requests
    /// pages (a cache miss throttles before them), so the pause follows it and the diary page is
    /// never fetched straight after the film page. On the API the pause runs alongside the lookup,
    /// so a cache miss does not wait twice.
    /// </summary>
    internal static async Task<FilmResult> LookupPacedAsync(ILetterboxdService service, int tmdbId, CancellationToken cancellationToken)
    {
        if (service.IsWebsiteSession)
        {
            var film = await service.LookupFilmByTmdbIdAsync(tmdbId, cancellationToken).ConfigureAwait(false);
            await FilmPause(cancellationToken).ConfigureAwait(false);
            return film;
        }

        var pacing = FilmPause(cancellationToken);
        var result = await service.LookupFilmByTmdbIdAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        await pacing.ConfigureAwait(false);
        return result;
    }

    private static bool HasTmdbId(BaseItem item) => int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), out _);

    internal static bool ShouldAbandon(FailureStreak streak)
        => streak.Permanent >= MaxConsecutiveSyncFailures
            || (streak.Transient >= MaxTransientSyncFailures && streak.TransientDays >= MinTransientFailureDays);

    /// <summary>
    /// The viewing date for a Jellyfin play timestamp: its day in the server's time zone, the same
    /// day real-time sync logs. The no-plausible-date filter runs first, so the fallback to today
    /// is only a guard.
    /// </summary>
    internal static DateTime ViewingDateFor(DateTime? lastPlayedUtc)
        => Helpers.ToLocalViewingDate(lastPlayedUtc ?? DateTime.UtcNow);

    /// <summary>
    /// Filter out already-synced candidates and order the rest with previously-failed first,
    /// never-attempted next. Items with no TMDb ID stay in the queue at the lowest priority rather
    /// than vanish (the runner sets them aside before calling this). Pure function for testability.
    /// </summary>
    internal static (List<T> Queue, int LocallySkipped) BuildSyncQueue<T>(
        IEnumerable<(T Item, int? TmdbId, DateTime ViewingDate)> candidates,
        string username,
        Func<string, int, DateTime, bool> wasSynced,
        Func<string, int, SyncStatus?> lastStatus)
    {
        var remaining = new List<(T Item, int Priority)>();
        int locallySkipped = 0;

        foreach (var c in candidates)
        {
            if (c.TmdbId is not int tid)
            {
                // No TMDb ID: kept, never dropped silently.
                remaining.Add((c.Item, 1));
                continue;
            }

            if (wasSynced(username, tid, c.ViewingDate))
            {
                locallySkipped++;
                continue;
            }

            // Only a failure jumps the queue. A skip is settled or deliberate (already on the
            // diary, no TMDb id, account paused), so putting it first would only spend the run's
            // Letterboxd budget on films that need nothing.
            var prev = lastStatus(username, tid);
            var priority = prev == SyncStatus.Failed ? 0 : 1;
            remaining.Add((c.Item, priority));
        }

        return (remaining.OrderBy(x => x.Priority).Select(x => x.Item).ToList(), locallySkipped);
    }
}
