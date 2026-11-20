using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LetterboxdSync;

/// <summary>
/// Stops one episode finish reaching Serializd twice (issue #200). Jellyfin can raise
/// PlaybackStopped more than once for a single finish, a second or so apart; without this the
/// second event finds the first event's history row and logs a rewatch. Events for the same
/// (user, account, show, season, episode range) run one at a time, and a finish within
/// <see cref="Window"/> of one already logged is treated as the same finish. In memory only:
/// the duplicate events arrive seconds apart, so nothing is lost on a restart.
/// </summary>
internal static class EpisodeFinishGuard
{
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private static readonly Dictionary<string, DateTime> _lastLogged = new(StringComparer.Ordinal);

    internal static string Key(string userId, string account, int showTmdbId, int season, int firstEpisode, int lastEpisode)
        => $"episode\n{userId}\n{account.ToLowerInvariant()}\n{showTmdbId}\n{season}\n{firstEpisode}-{lastEpisode}";

    public static Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken = default)
        => FilmSyncLock.AcquireAsync(key, cancellationToken);

    /// <summary>True when this key had a finish logged within the window. Call while holding the lock.</summary>
    public static bool IsRecentFinish(string key)
    {
        var now = UtcNow();
        lock (_lastLogged)
        {
            Prune(now);
            return _lastLogged.TryGetValue(key, out var at) && now - at < Window;
        }
    }

    /// <summary>Records a finish Serializd accepted. Call while holding the lock.</summary>
    public static void MarkLogged(string key)
    {
        var now = UtcNow();
        lock (_lastLogged)
        {
            Prune(now);
            _lastLogged[key] = now;
        }
    }

    internal static void ResetForTesting()
    {
        lock (_lastLogged) _lastLogged.Clear();
    }

    private static void Prune(DateTime now)
    {
        List<string>? expired = null;
        foreach (var (k, at) in _lastLogged)
        {
            if (now - at >= Window)
                (expired ??= new List<string>()).Add(k);
        }
        if (expired != null)
            foreach (var k in expired) _lastLogged.Remove(k);
    }
}
