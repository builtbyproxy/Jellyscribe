using System;
using System.Collections.Generic;

namespace LetterboxdSync.Api;

/// <summary>
/// In-memory sliding-window limit on the "Verify login" endpoints. Each check makes the server
/// sign in to Letterboxd or Serializd with whatever the caller typed, so without a limit any
/// signed-in Jellyfin user could use the server to test stolen passwords, and the remote
/// service would block the server's IP for every account in the household.
/// <para>
/// A check is counted when it starts (so a burst of parallel requests cannot slip past). A
/// check whose login succeeds is refunded from the failed-check budget, so a user confirming
/// several working accounts is not locked out, but every check still counts toward a looser
/// per-user cap, so a loop of valid logins cannot hammer the remote service either. One
/// instance per remote service, since a block on one does not affect the other. Nothing is
/// persisted; a restart clears it.
/// </para>
/// <para>
/// It is a mitigation, not a lock: the server-wide cap means a few users failing at once can
/// make everyone wait out the window, which is the price of keeping the server's IP off the
/// remote service's block list.
/// </para>
/// </summary>
internal sealed class LoginCheckLimiter
{
    /// <summary>How far back a check counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Failed checks one Jellyfin user may make per window.</summary>
    public const int PerUserLimit = 5;

    /// <summary>Checks of any outcome one Jellyfin user may make per window.</summary>
    public const int PerUserTotalLimit = 20;

    /// <summary>Failed checks the whole server may make per window, across all users.</summary>
    public const int GlobalLimit = 20;

    public static LoginCheckLimiter Letterboxd { get; } = new();

    public static LoginCheckLimiter Serializd { get; } = new();

    /// <summary>Test-only clock. Production never assigns it.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private sealed class UserBucket
    {
        public readonly List<DateTime> Failed = new();
        public readonly List<DateTime> All = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, UserBucket> _perUser = new(StringComparer.Ordinal);
    private readonly List<DateTime> _globalFailed = new();

    /// <summary>
    /// Counts one check for <paramref name="userKey"/> (the Jellyfin user id; empty when it
    /// cannot be resolved, which then shares one bucket). Returns false, with how long until a
    /// slot frees up, when the user's or the server's budget is spent.
    /// </summary>
    public bool TryAcquire(string userKey, out DateTime stamp, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = UtcNow();
            var cutoff = now - Window;
            Prune(_globalFailed, cutoff);
            PruneUsers(cutoff);

            _perUser.TryGetValue(userKey, out var mine);

            // A slot frees when the oldest entry in a full list leaves the window. With more
            // than one list full, the latest of those is when a retry can actually succeed.
            var freesAt = DateTime.MinValue;
            if (mine != null && mine.Failed.Count >= PerUserLimit) freesAt = Later(freesAt, mine.Failed[0] + Window);
            if (mine != null && mine.All.Count >= PerUserTotalLimit) freesAt = Later(freesAt, mine.All[0] + Window);
            if (_globalFailed.Count >= GlobalLimit) freesAt = Later(freesAt, _globalFailed[0] + Window);
            if (freesAt != DateTime.MinValue)
            {
                stamp = default;
                retryAfter = freesAt - now;
                return false;
            }

            if (mine == null)
            {
                mine = new UserBucket();
                _perUser[userKey] = mine;
            }

            mine.Failed.Add(now);
            mine.All.Add(now);
            _globalFailed.Add(now);
            stamp = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>
    /// Gives back a check whose login succeeded, so working logins never use up the
    /// failed-check budgets. It still counts toward <see cref="PerUserTotalLimit"/>.
    /// </summary>
    public void Refund(string userKey, DateTime stamp)
    {
        lock (_lock)
        {
            if (_perUser.TryGetValue(userKey, out var mine))
                mine.Failed.Remove(stamp);

            _globalFailed.Remove(stamp);
        }
    }

    /// <summary>The message the dashboards show when a check is refused.</summary>
    public static string RefusalMessage(TimeSpan retryAfter)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
        return $"Too many login checks. Try again in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
    }

    internal void ResetForTesting()
    {
        lock (_lock)
        {
            _perUser.Clear();
            _globalFailed.Clear();
            UtcNow = () => DateTime.UtcNow;
        }
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    private void PruneUsers(DateTime cutoff)
    {
        List<string>? empty = null;
        foreach (var (key, bucket) in _perUser)
        {
            Prune(bucket.Failed, cutoff);
            Prune(bucket.All, cutoff);
            if (bucket.All.Count == 0) (empty ??= new List<string>()).Add(key);
        }

        if (empty != null)
        {
            foreach (var key in empty) _perUser.Remove(key);
        }
    }

    // Entries are appended in time order, so everything stale sits at the front.
    private static void Prune(List<DateTime> list, DateTime cutoff)
    {
        var stale = 0;
        while (stale < list.Count && list[stale] <= cutoff) stale++;
        if (stale > 0) list.RemoveRange(0, stale);
    }
}
