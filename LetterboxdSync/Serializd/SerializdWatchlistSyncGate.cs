using System.Threading;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Serializes Serializd watchlist runs (scheduled + "Sync watchlist now") against each other.
/// Without it, repeated clicks started parallel runs that each fetched the watchlist, sent
/// Seerr requests and raced to create the same Jellyfin collection. Separate from
/// <see cref="SerializdSyncGate"/> so a watched-episode catch-up and a watchlist run, which
/// the dashboard starts from different buttons and the scheduler on different triggers, never
/// refuse each other.
/// </summary>
internal static class SerializdWatchlistSyncGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);

    public static bool IsRunning => Instance.CurrentCount == 0;
}
