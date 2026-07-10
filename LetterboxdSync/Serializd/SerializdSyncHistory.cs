using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Records which (Jellyfin user, TMDb show, season, episode) tuples have already been
/// logged to Serializd, so the scheduled catch-up doesn't re-log the same episode every
/// run. Deliberately separate from the Letterboxd <see cref="SyncHistory"/> (which is
/// film-centric) and far simpler: an append-only JSONL of keys plus an in-memory set.
///
/// Both the real-time playback path and the scheduled runner record here after a
/// successful log, keyed by the Jellyfin user id (stable across Serializd account changes).
/// </summary>
public static class SerializdSyncHistory
{
    private static readonly object _lock = new();
    private static HashSet<string>? _keys;
    private static ILogger? _logger;

    /// <summary>Test-only hook for the JSONL location. Production uses the plugin configurations dir.</summary>
    internal static string? DataPathOverride { get; set; }

    internal static void ResetForTesting()
    {
        lock (_lock) { _keys = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(DataPathOverride))
                return DataPathOverride!;

            var pluginDir = Path.GetDirectoryName(typeof(SerializdSyncHistory).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "serializd-sync-history.jsonl");
                return Path.Combine(pluginDir, "serializd-sync-history.jsonl");
            }

            return "serializd-sync-history.jsonl";
        }
    }

    private static string Key(string userJellyfinId, int showTmdbId, int seasonNumber, int episodeNumber)
        => $"{userJellyfinId}|{showTmdbId}|{seasonNumber}|{episodeNumber}";

    private static HashSet<string> Load()
    {
        if (_keys != null) return _keys;
        _keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(DataPath))
            {
                foreach (var line in File.ReadLines(DataPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0) _keys.Add(trimmed);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Serializd sync history from {Path}", DataPath);
        }

        return _keys;
    }

    public static bool Has(string userJellyfinId, int showTmdbId, int seasonNumber, int episodeNumber)
    {
        lock (_lock)
        {
            return Load().Contains(Key(userJellyfinId, showTmdbId, seasonNumber, episodeNumber));
        }
    }

    public static void Record(string userJellyfinId, int showTmdbId, int seasonNumber, int episodeNumber)
    {
        lock (_lock)
        {
            var keys = Load();
            var key = Key(userJellyfinId, showTmdbId, seasonNumber, episodeNumber);
            if (!keys.Add(key)) return; // already recorded

            try
            {
                var dir = Path.GetDirectoryName(DataPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(DataPath, key + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append Serializd sync history to {Path}", DataPath);
            }
        }
    }
}
