using System;
using System.Collections.Concurrent;

namespace LetterboxdSync;

/// <summary>
/// Last-known watchlist sizes per Jellyfin user, recorded by the watchlist sync runners and
/// read back by the <c>/Stats</c> endpoints so the Overview can show an "On watchlists" tile.
/// Kept per (user, account) so a user with several diary accounts sums correctly. In-memory:
/// values populate on the next watchlist sync after a restart (null/unknown until then, which
/// the UI renders as "—").
/// </summary>
public static class WatchlistStats
{
    private static readonly ConcurrentDictionary<string, int> Film = new();
    private static readonly ConcurrentDictionary<string, int> Tv = new();

    private static string Key(string userId, string account) => userId + "|" + account;

    /// <summary>Record the film (Letterboxd) watchlist size for one account.</summary>
    public static void SetFilm(string userId, string account, int count) => Film[Key(userId, account)] = count;

    /// <summary>Record the TV (Serializd) watchlist size for one account.</summary>
    public static void SetTv(string userId, string account, int count) => Tv[Key(userId, account)] = count;

    /// <summary>Total film watchlist size across the user's accounts, or null if never recorded.</summary>
    public static int? GetFilm(string userId) => Sum(Film, userId);

    /// <summary>Total TV watchlist size across the user's accounts, or null if never recorded.</summary>
    public static int? GetTv(string userId) => Sum(Tv, userId);

    private static int? Sum(ConcurrentDictionary<string, int> store, string userId)
    {
        var prefix = userId + "|";
        var any = false;
        var total = 0;
        foreach (var kv in store)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                any = true;
                total += kv.Value;
            }
        }

        return any ? total : (int?)null;
    }
}
