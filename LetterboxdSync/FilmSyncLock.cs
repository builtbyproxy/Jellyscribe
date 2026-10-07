using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LetterboxdSync;

/// <summary>
/// Serialises diary writes per (Jellyfin user, Letterboxd account, film), so the real-time sync
/// and the scheduled sync (or two quick finishes of the same film) cannot both pass the
/// duplicate checks before either has recorded its entry. Different films and different accounts
/// never wait on each other. An entry lives only while someone holds or waits for it.
/// </summary>
internal static class FilmSyncLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Refs;
    }

    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal static int ActiveCount
    {
        get { lock (_entries) return _entries.Count; }
    }

    public static async Task<IDisposable> AcquireAsync(string userId, string account, int tmdbId, CancellationToken cancellationToken = default)
    {
        var key = $"{userId}\n{account.ToLowerInvariant()}\n{tmdbId}";
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.Refs++;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Releaser(key, entry);
    }

    private static void Release(string key, Entry entry, bool held)
    {
        if (held) entry.Gate.Release();
        lock (_entries)
        {
            if (--entry.Refs == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly string _key;
        private readonly Entry _entry;
        private int _disposed;

        public Releaser(string key, Entry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Release(_key, _entry, held: true);
        }
    }
}
