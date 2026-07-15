using System;
using System.Collections.Generic;
using System.Linq;

namespace LetterboxdSync;

/// <summary>
/// Tracks the progress of long-running sync operations for dashboard display.
///
/// Keyed by an independent-concurrency "track" (<see cref="TrackLetterboxd"/> /
/// <see cref="TrackSerializd"/>, matching the two sync gates that can legitimately run at the
/// same time: <see cref="SyncGate"/> for the Letterboxd family, <see cref="Serializd.SerializdSyncGate"/>
/// for Serializd). Without this, a Letterboxd run and a Serializd run writing to one shared
/// singleton would stomp each other's task name/counts, or one's <see cref="Complete"/> could
/// flip <c>IsRunning</c> off while the other track is still active.
/// </summary>
public static class SyncProgress
{
    public const string TrackLetterboxd = "letterboxd";
    public const string TrackSerializd = "serializd";

    private static readonly object _lock = new();
    private static readonly Dictionary<string, TrackState> _tracks = new(StringComparer.Ordinal);

    private sealed class TrackState
    {
        public string? TaskName;
        public string? Phase;
        public int TotalItems;
        public int ProcessedItems;
        public int CacheHits;
        public int NewLookups;
        public bool IsRunning;
        public DateTime? StartedAt;
    }

    private static TrackState TrackFor(string track)
    {
        if (!_tracks.TryGetValue(track, out var state))
        {
            state = new TrackState();
            _tracks[track] = state;
        }

        return state;
    }

    public static void Start(string track, string taskName, string phase)
    {
        lock (_lock)
        {
            var t = TrackFor(track);
            t.TaskName = taskName;
            t.Phase = phase;
            t.TotalItems = 0;
            t.ProcessedItems = 0;
            t.CacheHits = 0;
            t.NewLookups = 0;
            t.IsRunning = true;
            t.StartedAt = DateTime.UtcNow;
        }
    }

    public static void SetPhase(string track, string phase)
    {
        lock (_lock) { TrackFor(track).Phase = phase; }
    }

    public static void SetTotal(string track, int total)
    {
        lock (_lock) { TrackFor(track).TotalItems = total; }
    }

    public static void IncrementProcessed(string track)
    {
        lock (_lock) { TrackFor(track).ProcessedItems++; }
    }

    public static void IncrementCacheHit(string track)
    {
        lock (_lock) { TrackFor(track).CacheHits++; }
    }

    public static void IncrementNewLookup(string track)
    {
        lock (_lock) { TrackFor(track).NewLookups++; }
    }

    public static void Complete(string track)
    {
        lock (_lock)
        {
            var t = TrackFor(track);
            t.Phase = "complete";
            t.IsRunning = false;
        }
    }

    /// <summary>
    /// A single dashboard-friendly snapshot. When exactly one track is running, this is
    /// that track's state unchanged (the common case). When two are running at once (e.g.
    /// "Sync all now" fires the Letterboxd and Serializd families together), the two are
    /// merged, rather than one clobbering the other. When none are running, the
    /// most-recently-finished track's terminal state is returned.
    /// </summary>
    public static object GetSnapshot()
    {
        lock (_lock)
        {
            var running = _tracks.Values.Where(t => t.IsRunning).ToList();

            if (running.Count == 0)
            {
                var last = _tracks.Values.OrderByDescending(t => t.StartedAt ?? DateTime.MinValue).FirstOrDefault();
                return Snapshot(last);
            }

            if (running.Count == 1)
                return Snapshot(running[0]);

            var startedAt = running.Min(t => t.StartedAt);
            return new
            {
                taskName = string.Join(" + ", running.Select(t => t.TaskName).Where(n => !string.IsNullOrEmpty(n))),
                phase = string.Join(" · ", running.Select(t => t.Phase).Where(p => !string.IsNullOrEmpty(p))),
                totalItems = running.Sum(t => t.TotalItems),
                processedItems = running.Sum(t => t.ProcessedItems),
                cacheHits = running.Sum(t => t.CacheHits),
                newLookups = running.Sum(t => t.NewLookups),
                isRunning = true,
                startedAt,
                elapsedSeconds = startedAt.HasValue ? (int)(DateTime.UtcNow - startedAt.Value).TotalSeconds : 0
            };
        }
    }

    private static object Snapshot(TrackState? t)
    {
        return new
        {
            taskName = t?.TaskName,
            phase = t?.Phase,
            totalItems = t?.TotalItems ?? 0,
            processedItems = t?.ProcessedItems ?? 0,
            cacheHits = t?.CacheHits ?? 0,
            newLookups = t?.NewLookups ?? 0,
            isRunning = t?.IsRunning ?? false,
            startedAt = t?.StartedAt,
            elapsedSeconds = t?.StartedAt.HasValue == true ? (int)(DateTime.UtcNow - t.StartedAt!.Value).TotalSeconds : 0
        };
    }

    /// <summary>Test-only: clears all track state between tests.</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _tracks.Clear(); }
    }
}
