using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Activity feed for the Serializd dashboard, the TV counterpart to the Letterboxd
/// <see cref="SyncHistory"/>. Reuses the same <see cref="SyncEvent"/> model and
/// <see cref="SyncHistory.GetPage(System.Collections.Generic.IEnumerable{SyncEvent}, int, int, string)"/>
/// paging helper so the dashboard view can be reused verbatim, just pointed at these events.
/// Kept in a separate JSONL from the Letterboxd feed so neither dashboard shows the other's rows,
/// and deliberately does NOT feed telemetry (that pipeline is Letterboxd-only for now).
/// </summary>
public static class SerializdActivity
{
    private static readonly object _lock = new();
    private static List<SyncEvent>? _events;
    private static ILogger? _logger;

    internal static string? DataPathOverride { get; set; }

    internal static void ResetForTesting()
    {
        lock (_lock) { _events = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(DataPathOverride)) return DataPathOverride!;
            var pluginDir = Path.GetDirectoryName(typeof(SerializdActivity).Assembly.Location);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir)) return Path.Combine(configDir, "serializd-activity.jsonl");
                return Path.Combine(pluginDir, "serializd-activity.jsonl");
            }

            return "serializd-activity.jsonl";
        }
    }

    private static List<SyncEvent> Load()
    {
        if (_events != null) return _events;
        _events = new List<SyncEvent>();
        try
        {
            if (File.Exists(DataPath))
            {
                foreach (var line in File.ReadLines(DataPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var e = JsonSerializer.Deserialize<SyncEvent>(line); if (e != null) _events.Add(e); }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load Serializd activity from {Path}", DataPath);
        }

        return _events;
    }

    public static void Record(SyncEvent evt)
    {
        lock (_lock)
        {
            Load().Add(evt);
            try
            {
                var dir = Path.GetDirectoryName(DataPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(DataPath, JsonSerializer.Serialize(evt) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append Serializd activity to {Path}", DataPath);
            }
        }
    }

    public static (int Total, int Success, int Failed, int Skipped, int Rewatches) GetStats(string? username = null)
    {
        lock (_lock)
        {
            // Reviews show in the feed but aren't episode logs, so they don't count toward the stats.
            IEnumerable<SyncEvent> events = Load().Where(e => e.Source != "review");
            if (!string.IsNullOrEmpty(username)) events = events.Where(e => e.Username == username);
            var list = events.ToList();
            return (
                list.Count,
                list.Count(e => e.Status == SyncStatus.Success),
                list.Count(e => e.Status == SyncStatus.Failed),
                list.Count(e => e.Status == SyncStatus.Skipped),
                list.Count(e => e.Status == SyncStatus.Rewatch));
        }
    }

    public static (List<SyncEvent> Events, int Total) GetPage(int offset, int count, string? username = null)
    {
        lock (_lock)
        {
            // Reuse the Letterboxd feed's paging/sort logic.
            return SyncHistory.GetPage(Load(), offset, count, username);
        }
    }
}
