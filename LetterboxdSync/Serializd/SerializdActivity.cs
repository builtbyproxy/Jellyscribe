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
/// Kept in a separate JSONL from the Letterboxd feed so neither dashboard shows the other's rows.
/// Also feeds telemetry (see <see cref="Record"/>), the TV counterpart of
/// <see cref="SyncHistory.Record"/>'s hook.
/// </summary>
public static class SerializdActivity
{
    private static readonly object _lock = new();
    private static List<SyncEvent>? _events;
    private static List<string>? _unreadableLines;
    private static bool _readFailed;
    private static ILogger? _logger;

    internal static string? DataPathOverride { get; set; }

    internal static void ResetForTesting()
    {
        lock (_lock) { _events = null; _unreadableLines = null; _readFailed = false; }
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
        _unreadableLines = new List<string>();
        _readFailed = false;
        try
        {
            if (File.Exists(DataPath))
            {
                foreach (var line in File.ReadLines(DataPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    SyncEvent? e = null;
                    try { e = JsonSerializer.Deserialize<SyncEvent>(line); }
                    catch (Exception) { /* kept verbatim below rather than lost */ }
                    if (e != null) _events.Add(e);
                    else _unreadableLines.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            _readFailed = true;
            _logger?.LogError(ex, "Failed to load Serializd activity from {Path}; it will not be rewritten until it loads cleanly", DataPath);
            return _events;
        }

        if (_unreadableLines.Count > 0)
        {
            // Never compact (and so rewrite) a file with lines this version cannot read.
            _logger?.LogWarning("Skipped {Count} unreadable lines in Serializd activity {Path}; they are kept in the file", _unreadableLines.Count, DataPath);
            return _events;
        }

        // Same cap as the Letterboxd history: a failing episode appends a row every run.
        var dropped = SyncHistory.Compact(_events);
        if (dropped > 0)
        {
            Save(_events);
            _logger?.LogInformation("Compacted Serializd activity: dropped {Count} old skipped/failed events", dropped);
        }

        return _events;
    }

    private static void Save(List<SyncEvent> events)
    {
        if (_readFailed)
        {
            _logger?.LogWarning("Not rewriting Serializd activity {Path}: it did not load cleanly", DataPath);
            return;
        }

        try
        {
            JsonlFile.WriteAllLinesAtomic(DataPath,
                events.Select(e => JsonSerializer.Serialize(e)).Concat(_unreadableLines ?? new List<string>()));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save Serializd activity to {Path}", DataPath);
        }
    }

    public static void Record(SyncEvent evt)
    {
        if (string.IsNullOrEmpty(evt.UserId))
            evt.UserId = SyncHistory.ResolveUserId(evt.Username);

        lock (_lock)
        {
            Load().Add(evt);
            try
            {
                JsonlFile.AppendLine(DataPath, JsonSerializer.Serialize(evt));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append Serializd activity to {Path}", DataPath);
            }
        }

        // Telemetry chokepoint, the TV counterpart of SyncHistory.Record's hook. No-op
        // (and exception-proof) while telemetry is disabled.
        TelemetryService.OnTvSyncEvent(evt);
    }

    public static int StampMissingUserIds()
    {
        lock (_lock)
        {
            var events = Load();
            var stamped = SyncHistory.StampMissingUserIds(events);
            if (stamped == 0) return 0;
            Save(events);
            return stamped;
        }
    }

    public static (int Total, int Success, int Failed, int Skipped, int Rewatches) GetStats(string? username = null)
    {
        lock (_lock)
        {
            // Reviews show in the feed but aren't episode logs, so they don't count toward the stats.
            IEnumerable<SyncEvent> events = Load().Where(e => e.Source != "review");
            if (!string.IsNullOrEmpty(username))
            {
                var userId = SyncHistory.ResolveUserId(username);
                events = events.Where(e => SyncHistory.BelongsTo(e, username, userId));
            }
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
