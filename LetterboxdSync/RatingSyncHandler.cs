using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Streams Jellyfin movie rating changes to the member's Letterboxd film rating. The diary sync
/// only carries a rating inside a new diary entry, so a rating set after the watch was logged
/// (by a client, the API, or Jellyfin Enhanced's review mirror; the web UI itself has no rating
/// control) never reached Letterboxd.
///
/// Numeric ratings save with <see cref="UserDataSaveReason.UpdateUserData"/>; favorite and like
/// toggles (and Jellyfin Enhanced's mirror) save with <see cref="UserDataSaveReason.UpdateUserRating"/>.
/// Neither reason means "the rating changed", and the save event carries no previous value, so the
/// handler keeps every user's current movie ratings in memory, seeded from the library at startup,
/// and acts only on a save whose rating differs from that. Pushes are further gated per account by
/// <see cref="RatingPushStore"/>. The plugin's own rating writes save with
/// <see cref="UserDataSaveReason.Import"/> and are ignored, so nothing echoes back.
///
/// The event handler does no I/O. One sweep loop pushes entries that have been quiet for
/// <see cref="DebounceWindow"/>, so a user tapping through star values produces one push with the
/// final value. A failed push is retried a few times with backoff.
/// </summary>
public sealed class RatingSyncHandler : IHostedService, IDisposable
{
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);
    internal const int MaxPending = 1000;
    internal const int MaxAttempts = 3;

    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RatingSyncHandler> _logger;
    private readonly MediaBrowser.Model.Activity.IActivityManager? _activityManager;

    private readonly ConcurrentDictionary<(Guid User, Guid Item), PendingRating> _pending = new();

    // Jellyfin's current rating per (user, movie), rated movies only: the "before" value that
    // UserDataSaveEventArgs does not carry. Seeded at startup, then kept current by Observe.
    private readonly ConcurrentDictionary<(Guid User, Guid Item), double> _known = new();
    private volatile bool _baselineReady;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>A rating waiting to be pushed. <paramref name="Attempt"/> is 1 for the first try.</summary>
    internal readonly record struct PendingRating(double Rating, DateTime DueUtc, int Attempt);

    /// <summary>Test seam for the clock.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Gap between consecutive pushes in one sweep, so a burst of ratings is paced.</summary>
    internal TimeSpan PushSpacing { get; set; } = TimeSpan.FromSeconds(2);

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

    internal bool TryGetPending(Guid userId, Guid itemId, out PendingRating pending)
        => _pending.TryGetValue((userId, itemId), out pending);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
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
            // ILetterboxdService takes no token, so an in-flight push (or its Cloudflare backoff)
            // finishes on its own; the host's stop token bounds how long shutdown waits for it.
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown. Pending values are dropped; the next change re-triggers.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            SeedBaseline(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read existing ratings for rating sync; the first save of each film will be treated as a change: {Message}",
                AuthBreaker.Sanitize(ex.Message));
        }
        finally
        {
            _baselineReady = true;
        }

        await SweepLoopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Record every user's current movie ratings without pushing anything, so a later save that
    /// leaves a rating unchanged (a favorite toggle on a film rated before this version, say) is
    /// recognised as such. Never overwrites a value <see cref="Observe"/> recorded meanwhile.
    /// </summary>
    internal void SeedBaseline(CancellationToken ct)
    {
        var seeded = 0;
        foreach (var user in _userManager.GetUsers())
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                Recursive = true
            });

            foreach (var movie in movies)
            {
                ct.ThrowIfCancellationRequested();
                var rating = _userDataManager.GetUserData(user, movie)?.Rating;
                if (rating is > 0 && _known.TryAdd((user.Id, movie.Id), rating.Value))
                    seeded++;
            }
        }

        _logger.LogInformation("Rating sync ready: {Count} existing film ratings recorded as the starting point", seeded);
    }

    internal void MarkBaselineReady() => _baselineReady = true;

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
    /// Synchronous, no I/O: filters the save and, when the rating actually changed, records it
    /// for the sweep loop. internal so tests can drive it without raising the event.
    /// </summary>
    internal void Observe(UserDataSaveEventArgs e)
    {
        if (e.SaveReason != UserDataSaveReason.UpdateUserData && e.SaveReason != UserDataSaveReason.UpdateUserRating)
            return;

        if (e.Item == null || !e.Item.IsMovie())
            return;

        var key = (e.UserId, e.Item.Id);
        double? current = e.UserData?.Rating is > 0 ? e.UserData.Rating : null;
        var known = _known.TryGetValue(key, out var stored);
        double? previous = known ? stored : null;

        // Before the baseline is in, an unknown film may still have an older rating, so any rated
        // save counts as a change. After it, unknown means unrated.
        var changed = known || _baselineReady ? previous != current : current.HasValue;
        if (!changed)
            return;

        if (current is double rating)
        {
            _known[key] = rating;
        }
        else
        {
            _known.TryRemove(key, out _);
            // Clearing wins over a tap still waiting out the debounce: final value wins.
            if (_pending.TryRemove(key, out _))
                _logger.LogDebug("Rating cleared on {Title} for user {UserId} before it synced; nothing will be sent", e.Item.Name, e.UserId);
            else
                _logger.LogDebug("Rating cleared on {Title} for user {UserId}; clearing is not propagated to Letterboxd", e.Item.Name, e.UserId);
            return;
        }

        _logger.LogDebug("Rating change on {Title} for user {UserId} ({Reason}): {Previous} -> {Rating}",
            e.Item.Name, e.UserId, e.SaveReason, previous, rating);

        if (!Config.GetEnabledAccountsForUser(e.UserId.ToString("N")).Any(a => a.SyncRatings))
            return;

        if (!_pending.ContainsKey(key) && _pending.Count >= MaxPending)
        {
            _logger.LogWarning("Rating sync queue is full ({Max} films); dropping the rating change on {Title}",
                MaxPending, e.Item.Name);
            return;
        }

        _pending[key] = new PendingRating(rating, UtcNow() + DebounceWindow, 1);
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
    /// Push every pending rating that is due. An entry is removed only if it is still the one
    /// read, so a tap that lands mid-pass stays queued for its own debounce. internal so tests
    /// can drain deterministically.
    /// </summary>
    internal async Task DrainDueAsync(CancellationToken ct)
    {
        var now = UtcNow();
        var due = _pending.Where(p => p.Value.DueUtc <= now).ToList();
        if (due.Count == 0)
            return;

        using var pass = new PushPass();
        foreach (var entry in due)
        {
            ct.ThrowIfCancellationRequested();
            if (!_pending.TryRemove(entry))
                continue;

            bool retry;
            try
            {
                retry = await PushAsync(entry.Key.User, entry.Key.Item, entry.Value.Rating, pass, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error syncing the rating on item {ItemId} for user {UserId}: {Message}",
                    entry.Key.Item, entry.Key.User, AuthBreaker.Sanitize(ex.Message));
                retry = true;
            }

            if (retry)
                Requeue(entry.Key, entry.Value);
        }
    }

    private void Requeue((Guid User, Guid Item) key, PendingRating failed)
    {
        if (failed.Attempt >= MaxAttempts)
        {
            _logger.LogWarning("Giving up syncing the rating on item {ItemId} for user {UserId} after {Attempts} attempts; it syncs on the next change",
                key.Item, key.User, failed.Attempt);
            return;
        }

        // TryAdd: a newer tap queued meanwhile carries the user's latest value and wins.
        _pending.TryAdd(key, failed with { DueUtc = UtcNow() + RetryDelay * failed.Attempt, Attempt = failed.Attempt + 1 });
    }

    /// <summary>Returns true when at least one account should be retried.</summary>
    private async Task<bool> PushAsync(Guid userId, Guid itemId, double jellyfinRating, PushPass pass, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user == null || item == null || !item.IsMovie())
            return false;

        if (!int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), out var tmdbId))
        {
            _logger.LogInformation("Not syncing the rating on {Title} to Letterboxd: it has no TMDb ID", item.Name);
            return false;
        }

        var stars = Helpers.MapRating(jellyfinRating);
        if (!stars.HasValue)
            return false;

        var userIdN = userId.ToString("N");
        var retry = false;
        foreach (var account in Config.GetEnabledAccountsForUser(userIdN).Where(a => a.SyncRatings).ToList())
        {
            ct.ThrowIfCancellationRequested();

            // Cheapest check first: a dictionary hit before the per-item library lookup.
            if (RatingPushStore.GetLastPushed(userIdN, account.LetterboxdUsername, tmdbId) == stars.Value)
                continue;

            if (LibraryExclusion.IsExcluded(_libraryManager, item, account.ExcludedLibraryIds, _logger))
                continue;

            if (AuthBreaker.IsOpen(userIdN, account.LetterboxdUsername))
            {
                _logger.LogInformation(
                    "Not syncing the rating on {Title} for {LbUser}: auth breaker open; it syncs on the next rating change after credentials are re-saved",
                    item.Name, account.LetterboxdUsername);
                continue;
            }

            var accountKey = (userIdN, account.LetterboxdUsername.ToLowerInvariant());
            if (pass.FailedAccounts.Contains(accountKey))
            {
                // Already failing this pass (Cloudflare, an outage): don't hammer it, try later.
                retry = true;
                continue;
            }

            if (!pass.Services.TryGetValue(accountKey, out var service))
            {
                try
                {
                    service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                        account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Not retried: repeated logins are what the breaker exists to stop.
                    _logger.LogError("Auth failed syncing the rating on {Title} for {Username} as {LbUser}: {Message}",
                        item.Name, user.Username, account.LetterboxdUsername, AuthBreaker.Sanitize(ex.Message));
                    pass.FailedAccounts.Add(accountKey);
                    if (AuthBreaker.RecordFailure(userIdN, account.LetterboxdUsername, ex.Message))
                        await AuthBreaker.NotifyOpenedAsync(_activityManager, userId, account.LetterboxdUsername, _logger).ConfigureAwait(false);
                    continue;
                }

                AuthBreaker.RecordSuccess(userIdN, account.LetterboxdUsername);
                pass.Services[accountKey] = service;
            }

            if (pass.Pushes++ > 0 && PushSpacing > TimeSpan.Zero)
                await Task.Delay(PushSpacing, ct).ConfigureAwait(false);

            try
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
                    Timestamp = UtcNow(),
                    Status = SyncStatus.Rated,
                    Source = SyncEventSources.Rating
                });
                _logger.LogInformation("Rated {Title} {Stars} stars on Letterboxd for {Username} as {LbUser}",
                    item.Name, stars.Value, user.Username, account.LetterboxdUsername);
            }
            catch (Exception ex)
            {
                // Logged, not recorded as a Failed sync event: Failed events feed the diary runner's
                // per-film abandon counter, and a rating push failing must never stop a film's
                // diary sync. The store is untouched and the entry is requeued with backoff.
                _logger.LogError("Failed to sync the rating on {Title} for {Username} as {LbUser}: {Message}",
                    item.Name, user.Username, account.LetterboxdUsername, AuthBreaker.Sanitize(ex.Message));
                pass.FailedAccounts.Add(accountKey);
                retry = true;
            }
        }

        return retry;
    }

    /// <summary>
    /// State shared by one drain pass: one authenticated service per account (so a burst of
    /// ratings is one login, not one per film), the accounts that already failed, and a push count
    /// for pacing.
    /// </summary>
    private sealed class PushPass : IDisposable
    {
        public Dictionary<(string User, string Account), ILetterboxdService> Services { get; } = new();

        public HashSet<(string User, string Account)> FailedAccounts { get; } = new();

        public int Pushes { get; set; }

        public void Dispose()
        {
            foreach (var service in Services.Values)
                service.Dispose();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
