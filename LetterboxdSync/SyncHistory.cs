using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public enum SyncStatus
{
    Success,
    Skipped,
    Failed,
    Rewatch,

    /// <summary>A watchlist sync successfully auto-requested a title via Seerr. Not a
    /// watch/diary outcome; see <see cref="SyncEventSources.SeerrAutoRequestFilm"/>/
    /// <see cref="SyncEventSources.SeerrAutoRequestTv"/> for which watchlist triggered it.</summary>
    Requested,

    /// <summary>A Jellyfin rating change was pushed to the member's Letterboxd film rating
    /// (<see cref="SyncEventSources.Rating"/>). Deliberately not Success: it is not a diary
    /// entry, so it must not count as a sync or satisfy the duplicate-entry backstop.</summary>
    Rated
}

/// <summary>
/// Well-known values for SyncEvent.Source. Free-form strings are still allowed,
/// but these are the ones the runner switches on.
/// </summary>
public static class SyncEventSources
{
    /// <summary>DiaryImportTask marked a Jellyfin item played because it appeared on the user's Letterboxd diary.</summary>
    public const string DiaryImport = "diary-import";

    /// <summary>WatchlistSyncRunner's Seerr auto-request step, for a Letterboxd (film) watchlist.</summary>
    public const string SeerrAutoRequestFilm = "seerr-auto-request-film";

    /// <summary>SerializdWatchlistSyncRunner's Seerr auto-request step, for a Serializd (TV) watchlist.</summary>
    public const string SeerrAutoRequestTv = "seerr-auto-request-tv";

    /// <summary>RatingSyncHandler pushed a Jellyfin rating change to the Letterboxd film rating.</summary>
    public const string Rating = "rating";
}

public class SyncEvent
{
    public string FilmTitle { get; set; } = string.Empty;
    public string FilmSlug { get; set; } = string.Empty;
    public int TmdbId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? ViewingDate { get; set; }
    public SyncStatus Status { get; set; }
    public string? Error { get; set; }
    public string? Source { get; set; }

    /// <summary>
    /// The Letterboxd account (username) the event was for. One Jellyfin user can link several
    /// accounts, each with its own diary, so every diary lookup scopes by it. Null on rows written
    /// before it existed; lookups treat those as belonging to every account of the user (see
    /// <see cref="SyncHistory.MatchesAccount"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; set; }

    /// <summary>
    /// True on a Failed event whose cause will not change on retry (Letterboxd has no film for the
    /// TMDb id). Only these count toward abandoning a film; a network error, a Cloudflare block or
    /// an outage never does. False on every row written before it existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PermanentFailure { get; set; }
}

public static class SyncHistory
{
    private static readonly object _lock = new();
    private static List<SyncEvent>? _events;
    private static ILogger? _logger;

    /// <summary>
    /// Test-only hook for the JSONL file location, plus a way to clear the in-memory
    /// list between tests. Production never assigns these; the default DataPath logic
    /// uses the plugin's configurations directory.
    /// </summary>
    internal static string? DataPathOverride { get; set; }

    /// <summary>Test hook: drop the in-memory cache so the next access re-reads from disk.</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _events = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    internal static Func<string, string?>? UserIdResolver { get; set; }

    internal static string? ResolveUserId(string? username)
        => string.IsNullOrEmpty(username) ? null : UserIdResolver?.Invoke(username);

    internal static bool BelongsTo(SyncEvent e, string username, string? userId)
        => !string.IsNullOrEmpty(e.UserId) && !string.IsNullOrEmpty(userId)
            ? string.Equals(e.UserId, userId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(e.Username, username, StringComparison.Ordinal);

    /// <summary>
    /// Error text of a Skipped event recorded because Letterboxd's diary already had the film on
    /// that viewing date. Such a viewing is settled: <see cref="WasSuccessfullySynced"/> counts it,
    /// so the next run does not ask Letterboxd again.
    /// </summary>
    public const string AlreadyOnDiaryError = "Already on Letterboxd diary for this date";

    /// <summary>Start of the Error text of a Skipped event the local duplicate backstop recorded.
    /// Settled the same way as <see cref="AlreadyOnDiaryError"/>.</summary>
    public const string BackstopErrorPrefix = "Local history shows prior sync on ";

    /// <summary>
    /// True when the event is for <paramref name="account"/>. A row with no account predates
    /// account scoping and matches every account of its user, the same legacy fallback
    /// <see cref="Serializd.SerializdSyncHistory"/> uses, so an upgrade never makes a film already
    /// logged look unlogged. A null <paramref name="account"/> matches every row.
    /// </summary>
    internal static bool MatchesAccount(SyncEvent e, string? account)
        => string.IsNullOrEmpty(account) || string.IsNullOrEmpty(e.Account)
            || string.Equals(e.Account, account, StringComparison.OrdinalIgnoreCase);

    private static bool IsSettledSkip(SyncEvent e)
        => e.Status == SyncStatus.Skipped && e.Error != null
            && (string.Equals(e.Error, AlreadyOnDiaryError, StringComparison.Ordinal)
                || e.Error.StartsWith(BackstopErrorPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Clears <see cref="SyncEvent.PermanentFailure"/> on events already recorded and rewrites the
    /// store. The runner calls it when every film it tried in a run failed: that is an account or
    /// service outage, not a fact about the films, so none of it may count toward abandonment.
    /// </summary>
    public static void ClearPermanentFailure(IReadOnlyCollection<SyncEvent> recorded)
    {
        if (recorded.Count == 0) return;
        lock (_lock)
        {
            var changed = false;
            foreach (var e in recorded)
            {
                if (!e.PermanentFailure) continue;
                e.PermanentFailure = false;
                changed = true;
            }
            if (changed && _events != null) SaveAllEvents();
        }
    }

    public static int StampMissingUserIds()
    {
        lock (_lock)
        {
            var stamped = StampMissingUserIds(LoadEvents());
            if (stamped > 0) SaveAllEvents();
            return stamped;
        }
    }

    internal static int StampMissingUserIds(IEnumerable<SyncEvent> events)
    {
        var stamped = 0;
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(e.UserId)) continue;
            var id = ResolveUserId(e.Username);
            if (string.IsNullOrEmpty(id)) continue;
            e.UserId = id;
            stamped++;
        }
        return stamped;
    }

    private static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(DataPathOverride))
                return DataPathOverride!;

            var assembly = typeof(SyncHistory).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-sync-history.jsonl");
            }

            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "sync-history.jsonl");

            return "sync-history.jsonl";
        }
    }

    private static List<SyncEvent> LoadEvents()
    {
        if (_events != null) return _events;

        _events = new List<SyncEvent>();

        try
        {
            var jsonlPath = DataPath;
            if (File.Exists(jsonlPath))
            {
                foreach (var line in File.ReadLines(jsonlPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var evt = JsonSerializer.Deserialize<SyncEvent>(line);
                        if (evt != null) _events.Add(evt);
                    }
                    catch { }
                }
                return _events;
            }

            // Migrate from old JSON format if it exists
            var legacyPath = jsonlPath.Replace(".jsonl", ".json");
            if (File.Exists(legacyPath))
            {
                var json = File.ReadAllText(legacyPath);
                _events = JsonSerializer.Deserialize<List<SyncEvent>>(json) ?? new List<SyncEvent>();
                // Write in new JSONL format
                SaveAllEvents();
                _logger?.LogInformation("Migrated {Count} sync history events from JSON to JSONL", _events.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load sync history from {Path}", DataPath);
        }

        return _events;
    }

    private static void SaveAllEvents()
    {
        try
        {
            var path = DataPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var writer = new StreamWriter(path, append: false);
            foreach (var evt in _events!)
            {
                writer.WriteLine(JsonSerializer.Serialize(evt));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save sync history to {Path}", DataPath);
        }
    }

    public static void Record(SyncEvent evt)
    {
        if (string.IsNullOrEmpty(evt.UserId))
            evt.UserId = ResolveUserId(evt.Username);

        lock (_lock)
        {
            var events = LoadEvents();
            events.Add(evt);

            try
            {
                var path = DataPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, JsonSerializer.Serialize(evt) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append sync event to {Path}", DataPath);
            }
        }

        // Telemetry chokepoint: every sync outcome in the plugin flows through Record,
        // so this single hook counts successes/skips/failures from all sources. No-op
        // (and exception-proof) while telemetry is disabled.
        TelemetryService.OnSyncEvent(evt);
    }

    public static List<SyncEvent> GetRecent(int count = 100, string? username = null)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            IEnumerable<SyncEvent> filtered = events;

            if (!string.IsNullOrEmpty(username))
            {
                var userId = ResolveUserId(username);
                filtered = filtered.Where(e => BelongsTo(e, username, userId));
            }

            return filtered.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }
    }

    /// <summary>
    /// Page through history newest-first. Returns the slice plus the total count for the
    /// caller's paginator. Username, when supplied, restricts both the slice and the total
    /// to that user's events.
    /// </summary>
    public static (List<SyncEvent> Events, int Total) GetPage(int offset, int count, string? username = null)
    {
        lock (_lock)
        {
            return GetPage(LoadEvents(), offset, count, username);
        }
    }

    internal static (List<SyncEvent> Events, int Total) GetPage(IEnumerable<SyncEvent> events, int offset, int count, string? username = null)
    {
        IEnumerable<SyncEvent> filtered = events;
        if (!string.IsNullOrEmpty(username))
        {
            var userId = ResolveUserId(username);
            filtered = filtered.Where(e => BelongsTo(e, username, userId));
        }

        var ordered = filtered.OrderByDescending(e => e.Timestamp).ToList();
        var safeOffset = Math.Max(0, offset);
        var safeCount = Math.Max(0, count);
        var slice = ordered.Skip(safeOffset).Take(safeCount).ToList();
        return (slice, ordered.Count);
    }

    /// <summary>
    /// Most recent status recorded for this user/film, or null if there's no history.
    /// Used to prioritise previously-failed films at the head of the sync queue.
    /// </summary>
    public static SyncStatus? GetLastStatusForFilm(string username, int tmdbId, string? account = null)
    {
        lock (_lock)
        {
            return GetLastStatusForFilm(LoadEvents(), username, tmdbId, account);
        }
    }

    /// <summary>
    /// True if this user/account/film viewing date is already settled: a Success or Rewatch entry,
    /// or a skip because Letterboxd's diary already had it or the local backstop refused it, whose
    /// ViewingDate matches. Used to short-circuit the duplicate check without making an HTTP call
    /// to Letterboxd.
    /// </summary>
    public static bool WasSuccessfullySynced(string username, int tmdbId, DateTime viewingDate, string? account = null)
    {
        lock (_lock)
        {
            return WasSuccessfullySynced(LoadEvents(), username, tmdbId, viewingDate, account);
        }
    }

    // Pure overloads, exposed for unit testing without touching the on-disk store.

    internal static SyncStatus? GetLastStatusForFilm(IEnumerable<SyncEvent> events, string username, int tmdbId, string? account = null)
    {
        var userId = ResolveUserId(username);
        SyncEvent? latest = null;
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (!MatchesAccount(e, account)) continue;
            // A rating push says nothing about the film's diary sync, which is what callers rank by.
            if (e.Status == SyncStatus.Rated) continue;
            if (latest == null || e.Timestamp > latest.Timestamp) latest = e;
        }
        return latest?.Status;
    }

    /// <summary>
    /// Number of consecutive permanent failures (<see cref="SyncEvent.PermanentFailure"/>) at the
    /// tail of this user/account/film's history, most recent first, stopping at the first
    /// non-Failed event. The runner uses this to abandon a film Letterboxd keeps saying it does
    /// not have, instead of retrying it on every run. Transient failures (network, Cloudflare,
    /// rate limits, an outage) neither continue nor break the streak, so a bad few days can never
    /// abandon a film, and any later success or skip resets it.
    /// </summary>
    public static int GetConsecutiveFailureCount(string username, int tmdbId, string? account = null)
    {
        lock (_lock)
        {
            return GetConsecutiveFailureCount(LoadEvents(), username, tmdbId, account);
        }
    }

    internal static int GetConsecutiveFailureCount(IEnumerable<SyncEvent> events, string username, int tmdbId, string? account = null)
    {
        // Rated events are skipped: a rating push neither continues nor breaks the film's diary
        // failure streak.
        var userId = ResolveUserId(username);
        var ordered = events
            .Where(e => e.TmdbId == tmdbId && BelongsTo(e, username, userId)
                && MatchesAccount(e, account)
                && e.Status != SyncStatus.Rated)
            .OrderByDescending(e => e.Timestamp);

        var count = 0;
        foreach (var e in ordered)
        {
            if (e.Status != SyncStatus.Failed) break;
            if (e.PermanentFailure) count++;
        }
        return count;
    }

    internal static bool WasSuccessfullySynced(IEnumerable<SyncEvent> events, string username, int tmdbId, DateTime viewingDate, string? account = null)
    {
        var userId = ResolveUserId(username);
        var target = viewingDate.Date;
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (!MatchesAccount(e, account)) continue;
            if (e.Status != SyncStatus.Success && e.Status != SyncStatus.Rewatch && !IsSettledSkip(e)) continue;
            if (e.ViewingDate?.Date == target) return true;
        }
        return false;
    }

    /// <summary>
    /// ViewingDate of the most recent Success or Rewatch entry for this user/account/film, or
    /// null if there isn't one. Used as a local-history backstop against duplicates when
    /// Letterboxd's diary does not yet show an entry that was just written.
    /// </summary>
    public static DateTime? GetLastSuccessfulSyncDate(string username, int tmdbId, string? account = null)
    {
        lock (_lock)
        {
            return GetLastSuccessfulSyncDate(LoadEvents(), username, tmdbId, account);
        }
    }

    internal static DateTime? GetLastSuccessfulSyncDate(IEnumerable<SyncEvent> events, string username, int tmdbId, string? account = null)
    {
        var userId = ResolveUserId(username);
        SyncEvent? latest = null;
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (!MatchesAccount(e, account)) continue;
            if (e.Status != SyncStatus.Success && e.Status != SyncStatus.Rewatch) continue;
            if (latest == null || e.Timestamp > latest.Timestamp) latest = e;
        }
        return latest?.ViewingDate;
    }

    /// <summary>
    /// True if DiaryImportTask has previously marked this user's Jellyfin copy of this
    /// film as played because the film was found on their Letterboxd diary. The runner
    /// uses this as a guard against the import-then-export loop described in
    /// https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/issues/32.
    /// </summary>
    public static bool WasImportedFromDiary(string username, int tmdbId)
    {
        lock (_lock)
        {
            return WasImportedFromDiary(LoadEvents(), username, tmdbId);
        }
    }

    internal static bool WasImportedFromDiary(IEnumerable<SyncEvent> events, string username, int tmdbId)
    {
        var userId = ResolveUserId(username);
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (string.Equals(e.Source, SyncEventSources.DiaryImport, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static (int Total, int Success, int Failed, int Skipped, int Rewatches, int Requested) GetStats(string? username = null)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            IEnumerable<SyncEvent> filtered = events;

            if (!string.IsNullOrEmpty(username))
            {
                var userId = ResolveUserId(username);
                filtered = filtered.Where(e => BelongsTo(e, username, userId));
            }

            var list = filtered.ToList();
            return (
                // A rating push is not a logged film (the dashboards show Total as "Films logged"),
                // the same way Serializd's stats leave reviews out of theirs.
                list.Count(e => e.Status != SyncStatus.Rated),
                list.Count(e => e.Status == SyncStatus.Success),
                list.Count(e => e.Status == SyncStatus.Failed),
                list.Count(e => e.Status == SyncStatus.Skipped),
                list.Count(e => e.Status == SyncStatus.Rewatch),
                list.Count(e => e.Status == SyncStatus.Requested)
            );
        }
    }
}
