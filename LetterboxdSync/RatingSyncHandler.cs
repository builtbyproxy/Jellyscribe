using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Streams Jellyfin movie rating changes to the member's Letterboxd film rating. The diary sync
/// only carries a rating inside a new diary entry, so a rating set after the watch was logged
/// (in the web UI, a client, or Jellyfin Enhanced's review mirror) never reached Letterboxd.
///
/// Numeric ratings save with <see cref="UserDataSaveReason.UpdateUserData"/>; favorite and like
/// toggles (and Jellyfin Enhanced's mirror) save with <see cref="UserDataSaveReason.UpdateUserRating"/>.
/// Neither reason alone means "the rating changed", so both are accepted and every push is gated
/// by <see cref="RatingPushStore"/>: only a half-star value different from the last one pushed
/// goes out. The plugin's own rating writes save with <see cref="UserDataSaveReason.Import"/> and
/// are ignored, so nothing echoes back.
///
/// The event handler does no I/O: it records the newest rating per (user, item). One sweep loop
/// pushes entries that have been quiet for <see cref="DebounceWindow"/>, so a user tapping through
/// star values produces one push with the final value.
/// </summary>
public sealed class RatingSyncHandler : IHostedService, IDisposable
{
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    internal const int MaxPending = 1000;

    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RatingSyncHandler> _logger;
    private readonly MediaBrowser.Model.Activity.IActivityManager? _activityManager;

    private readonly ConcurrentDictionary<(Guid User, Guid Item), PendingRating> _pending = new();

    // Last rating seen per (user, item), only so diagnostics log real changes rather than every
    // playstate save. Bounded by rated library items per user.
    private readonly ConcurrentDictionary<(Guid User, Guid Item), double?> _observed = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    internal readonly record struct PendingRating(double Rating, DateTime LastSeenUtc);

    /// <summary>Test seam for the debounce clock.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    // activityManager is optional, as in PlaybackHandler: a null only skips the auth-breaker
    // admin notification.
    public RatingSyncHandler(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<RatingSyncHandler> logger,
        MediaBrowser.Model.Activity.IActivityManager? activityManager = null)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _activityManager = activityManager;
        RatingPushStore.SetLogger(logger);
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    internal int PendingCount => _pending.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SweepLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        if (_cts == null || _loop == null)
            return;

        _cts.Cancel();
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown. Pending values inside the debounce window are dropped; the
            // next change to that film re-triggers.
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            Observe(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error observing a user data save for rating sync");
        }
    }

    /// <summary>
    /// Synchronous, no I/O: filters the save and records the newest rating for the sweep loop.
    /// internal so tests can drive it without raising the event.
    /// </summary>
    internal void Observe(UserDataSaveEventArgs e)
    {
        if (e.SaveReason != UserDataSaveReason.UpdateUserData && e.SaveReason != UserDataSaveReason.UpdateUserRating)
            return;

        if (e.Item == null || !e.Item.IsMovie())
            return;

        var key = (e.UserId, e.Item.Id);
        var rating = e.UserData?.Rating;

        var changed = !_observed.TryGetValue(key, out var previous) || previous != rating;
        if (changed)
        {
            _observed[key] = rating;
            if (rating is > 0)
                _logger.LogDebug("Rating change observed on {Title} for user {UserId} ({Reason}): {Previous} -> {Rating}",
                    e.Item.Name, e.UserId, e.SaveReason, previous, rating);
            else if (previous is > 0)
                _logger.LogDebug("Rating cleared on {Title} for user {UserId} ({Reason}); clearing is not propagated to Letterboxd",
                    e.Item.Name, e.UserId, e.SaveReason);
        }

        if (rating is not > 0)
            return;

        if (!Config.GetEnabledAccountsForUser(e.UserId.ToString("N")).Any(a => a.SyncRatings))
            return;

        if (!_pending.ContainsKey(key) && _pending.Count >= MaxPending)
        {
            _logger.LogWarning("Rating sync queue is full ({Max} films); dropping the rating change on {Title}",
                MaxPending, e.Item.Name);
            return;
        }

        _pending[key] = new PendingRating(rating.Value, UtcNow());
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await DrainDueAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error draining pending rating pushes");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Push every pending rating that has been quiet for <see cref="DebounceWindow"/>. An entry is
    /// removed only if it is still the one read, so a tap that lands mid-drain survives to the
    /// next sweep. internal so tests can drain deterministically.
    /// </summary>
    internal async Task DrainDueAsync(CancellationToken ct)
    {
        var now = UtcNow();
        foreach (var entry in _pending)
        {
            ct.ThrowIfCancellationRequested();
            if (now - entry.Value.LastSeenUtc < DebounceWindow)
                continue;
            if (!_pending.TryRemove(entry))
                continue;

            await PushAsync(entry.Key.User, entry.Key.Item, entry.Value.Rating, ct).ConfigureAwait(false);
        }
    }

    private async Task PushAsync(Guid userId, Guid itemId, double jellyfinRating, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user == null || item == null || !item.IsMovie())
            return;

        if (!int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), out var tmdbId))
        {
            _logger.LogInformation("Not syncing the rating on {Title} to Letterboxd: it has no TMDb ID", item.Name);
            return;
        }

        var stars = Helpers.MapRating(jellyfinRating);
        if (!stars.HasValue)
            return;

        var userIdN = userId.ToString("N");
        foreach (var account in Config.GetEnabledAccountsForUser(userIdN).Where(a => a.SyncRatings).ToList())
        {
            ct.ThrowIfCancellationRequested();

            if (LibraryExclusion.IsExcluded(_libraryManager, item, account.ExcludedLibraryIds, _logger))
                continue;

            if (RatingPushStore.GetLastPushed(userIdN, account.LetterboxdUsername, tmdbId) == stars.Value)
                continue;

            if (AuthBreaker.IsOpen(userIdN, account.LetterboxdUsername))
            {
                _logger.LogInformation(
                    "Not syncing the rating on {Title} for {LbUser}: auth breaker open; it syncs on the next rating change after credentials are re-saved",
                    item.Name, account.LetterboxdUsername);
                continue;
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
                _logger.LogError("Auth failed syncing the rating on {Title} for {Username} as {LbUser}: {Message}",
                    item.Name, user.Username, account.LetterboxdUsername, AuthBreaker.Sanitize(ex.Message));
                if (AuthBreaker.RecordFailure(userIdN, account.LetterboxdUsername, ex.Message))
                    await AuthBreaker.NotifyOpenedAsync(_activityManager, userId, account.LetterboxdUsername, _logger).ConfigureAwait(false);
                continue;
            }

            AuthBreaker.RecordSuccess(userIdN, account.LetterboxdUsername);

            try
            {
                using (service)
                {
                    var film = await service.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
                    await service.SetFilmRatingAsync(film.Slug, film.FilmId, stars.Value).ConfigureAwait(false);

                    RatingPushStore.RecordPushed(userIdN, account.LetterboxdUsername, tmdbId, stars.Value);
                    SyncHistory.Record(new SyncEvent
                    {
                        FilmTitle = $"{item.Name} · Rated {stars.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} stars",
                        FilmSlug = film.Slug,
                        TmdbId = tmdbId,
                        Username = user.Username,
                        Timestamp = DateTime.UtcNow,
                        Status = SyncStatus.Rated,
                        Source = SyncEventSources.Rating
                    });
                    _logger.LogInformation("Rated {Title} {Stars} stars on Letterboxd for {Username} as {LbUser}",
                        item.Name, stars.Value, user.Username, account.LetterboxdUsername);
                }
            }
            catch (Exception ex)
            {
                // Logged, not recorded as a Failed sync event: Failed events feed the diary runner's
                // per-film abandon counter, and a rating push failing must never stop a film's
                // diary sync. The store is untouched, so the next save of this rating retries.
                _logger.LogError("Failed to sync the rating on {Title} for {Username} as {LbUser}: {Message}",
                    item.Name, user.Username, account.LetterboxdUsername, ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
