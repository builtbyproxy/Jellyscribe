using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// The last film rating <see cref="RatingSyncHandler"/> successfully pushed to Letterboxd, per
/// (Jellyfin user, Letterboxd account, TMDb id). The handler pushes only when a new rating maps to
/// a different half-star value than the one stored here, because Jellyfin raises the same
/// user-data event for favorite toggles and playstate edits that leave the rating untouched.
///
/// Persisted as append-only JSONL beside the other history files so a restart does not turn the
/// next unrelated save on every rated film into a push. One line per successful push; on load the
/// file is compacted to the latest value per key, so it is bounded by rated films, not by events.
/// Nothing is ever evicted: an evicted key would make its next no-op save push again.
/// </summary>
public static class RatingPushStore
{
    private static readonly object _lock = new();
    private static Dictionary<string, Line>? _ratings;
    private static int _linesOnDisk;
    private static ILogger? _logger;

    /// <summary>Test-only hook for the JSONL location. Production uses the plugin configurations dir.</summary>
    internal static string? DataPathOverride { get; set; }

    internal static void ResetForTesting()
    {
        lock (_lock) { _ratings = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(DataPathOverride))
                return DataPathOverride!;

            var pluginDir = Path.GetDirectoryName(typeof(RatingPushStore).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-rating-pushes.jsonl");
                return Path.Combine(pluginDir, "letterboxd-rating-pushes.jsonl");
            }

            return "letterboxd-rating-pushes.jsonl";
        }
    }

    private sealed class Line
    {
        [JsonPropertyName("u")] public string User { get; set; } = string.Empty;
        [JsonPropertyName("a")] public string Account { get; set; } = string.Empty;
        [JsonPropertyName("t")] public int TmdbId { get; set; }
        [JsonPropertyName("r")] public double Rating { get; set; }
    }

    // Letterboxd usernames are case-insensitive (PutAccounts matches them OrdinalIgnoreCase).
    private static string Key(string userJellyfinId, string letterboxdUsername, int tmdbId)
        => $"{userJellyfinId}|{letterboxdUsername.ToLowerInvariant()}|{tmdbId}";

    private static Dictionary<string, Line> Load()
    {
        if (_ratings != null) return _ratings;
        _ratings = new Dictionary<string, Line>(StringComparer.Ordinal);
        var lineCount = 0;
        try
        {
            if (File.Exists(DataPath))
            {
                foreach (var raw in File.ReadLines(DataPath))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    lineCount++;
                    try
                    {
                        var line = JsonSerializer.Deserialize<Line>(raw);
                        if (line != null)
                            _ratings[Key(line.User, line.Account, line.TmdbId)] = line;
                    }
                    catch (JsonException)
                    {
                        // A torn final line from a crash mid-append; the rest of the file is still good.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Not cached: an empty store would make every no-op save push, so retry the read next time.
            _logger?.LogError(ex, "Failed to load rating push history from {Path}", DataPath);
            var partial = _ratings;
            _ratings = null;
            return partial;
        }

        _linesOnDisk = lineCount;
        if (lineCount > _ratings.Count)
            Compact();

        return _ratings;
    }

    private static void Compact()
    {
        try
        {
            var tmp = DataPath + ".tmp";
            using (var writer = new StreamWriter(tmp, append: false))
            {
                foreach (var line in _ratings!.Values)
                    writer.WriteLine(JsonSerializer.Serialize(line));
            }

            File.Move(tmp, DataPath, overwrite: true);
            _linesOnDisk = _ratings!.Count;
        }
        catch (Exception ex)
        {
            // Compaction is an optimisation; the uncompacted file still loads correctly.
            _logger?.LogWarning("Failed to compact rating push history at {Path}: {Message}", DataPath, ex.Message);
        }
    }

    /// <summary>The half-star rating last pushed for this film and account, or null if none.</summary>
    public static double? GetLastPushed(string userJellyfinId, string letterboxdUsername, int tmdbId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(Key(userJellyfinId, letterboxdUsername, tmdbId), out var line) ? line.Rating : null;
        }
    }

    /// <summary>Record a successful push. Call only after Letterboxd accepted the rating.</summary>
    public static void RecordPushed(string userJellyfinId, string letterboxdUsername, int tmdbId, double rating)
    {
        lock (_lock)
        {
            var ratings = Load();
            var key = Key(userJellyfinId, letterboxdUsername, tmdbId);
            if (ratings.TryGetValue(key, out var existing) && existing.Rating.Equals(rating))
                return;
            var line = new Line
            {
                User = userJellyfinId,
                Account = letterboxdUsername.ToLowerInvariant(),
                TmdbId = tmdbId,
                Rating = rating
            };
            ratings[key] = line;

            try
            {
                var dir = Path.GetDirectoryName(DataPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(DataPath, JsonSerializer.Serialize(line) + Environment.NewLine);
                _linesOnDisk++;

                // Re-rates append; compact well before the file is dominated by superseded lines.
                if (_linesOnDisk > (2 * ratings.Count) + 100)
                    Compact();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append rating push history to {Path}", DataPath);
            }
        }
    }
}
