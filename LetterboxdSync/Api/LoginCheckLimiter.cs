using System;
using System.Collections.Generic;

namespace LetterboxdSync.Api;

/// <summary>
/// In-memory sliding-window limit on the "Verify login" endpoints. Each check makes the server
/// sign in to Letterboxd or Serializd with whatever the caller typed, so without a limit any
/// signed-in Jellyfin user could use the server to test stolen passwords, and the remote
/// service would block the server's IP for every account in the household.
/// <para>
/// A check is counted when it starts (so a burst of parallel requests cannot slip past) and
/// refunded when the login succeeds, so the budget is effectively failed checks: a user
/// confirming several working accounts never runs out. One instance per remote service, since
/// a block on one does not affect the other. Nothing is persisted; a restart clears it.
/// </para>
/// </summary>
public sealed class LoginCheckLimiter
{
    /// <summary>How far back a check counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Failed checks one Jellyfin user may make per window.</summary>
    public const int PerUserLimit = 5;

    /// <summary>Failed checks the whole server may make per window, across all users.</summary>
    public const int GlobalLimit = 20;

    public static LoginCheckLimiter Letterboxd { get; } = new();

    public static LoginCheckLimiter Serializd { get; } = new();

    /// <summary>Test-only clock. Production never assigns it.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<DateTime>> _perUser = new(StringComparer.Ordinal);
    private readonly List<DateTime> _global = new();

    /// <summary>
    /// Counts one check for <paramref name="userKey"/> (the Jellyfin user id; empty when it
    /// cannot be resolved, which then shares one bucket). Returns false, with how long until a
    /// slot frees up, when either the user's or the server's budget is spent.
    /// </summary>
    public bool TryAcquire(string userKey, out DateTime stamp, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = UtcNow();
            var cutoff = now - Window;
            Prune(_global, cutoff);
            PruneUsers(cutoff);

            _perUser.TryGetValue(userKey, out var mine);
            var userFull = mine != null && mine.Count >= PerUserLimit;
            var globalFull = _global.Count >= GlobalLimit;
            if (userFull || globalFull)
            {
                // A slot frees when the oldest check in a full bucket leaves the window. When
                // both are full, the later of the two is when a retry can actually succeed.
                var freesAt = DateTime.MinValue;
                if (userFull) freesAt = mine![0] + Window;
                if (globalFull && _global[0] + Window > freesAt) freesAt = _global[0] + Window;
                stamp = default;
                retryAfter = freesAt - now;
                return false;
            }

            if (mine == null)
            {
                mine = new List<DateTime>();
                _perUser[userKey] = mine;
            }

            mine.Add(now);
            _global.Add(now);
            stamp = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Gives back a check that succeeded, so working logins never use up the budget.</summary>
    public void Refund(string userKey, DateTime stamp)
    {
        lock (_lock)
        {
            if (_perUser.TryGetValue(userKey, out var mine))
            {
                mine.Remove(stamp);
                if (mine.Count == 0) _perUser.Remove(userKey);
            }

            _global.Remove(stamp);
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
            _global.Clear();
            UtcNow = () => DateTime.UtcNow;
        }
    }

    private void PruneUsers(DateTime cutoff)
    {
        List<string>? empty = null;
        foreach (var (key, list) in _perUser)
        {
            Prune(list, cutoff);
            if (list.Count == 0) (empty ??= new List<string>()).Add(key);
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
