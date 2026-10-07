using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Persistent cache of Letterboxd film slug → TMDb ID mappings.
/// Eliminates repeated HTTP requests for films already resolved.
/// </summary>
public static class TmdbCache
{
    /// <summary>
    /// Stored for a slug whose Letterboxd page is a TV entry, so it is not fetched again.
    /// TMDb ids start at 1, so it never collides with a real one. <see cref="Get"/> never
    /// returns it; <see cref="TryGet"/> reports it as a known slug with no movie id.
    /// </summary>
    internal const int NotAFilm = 0;

    private static readonly object _lock = new();
    private static Dictionary<string, int>? _cache;
    private static ILogger? _logger;

    /// <summary>
    /// Test-only override for the cache file location, plus a way to clear the
    /// in-memory cache between tests. Production never assigns this; the default
    /// CachePath logic uses the plugin's configurations directory.
    /// </summary>
    internal static string? CachePathOverride { get; set; }

    /// <summary>Test hook: drop the in-memory cache so the next access re-reads from disk.</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _cache = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    private static string CachePath
    {
        get
        {
            if (!string.IsNullOrEmpty(CachePathOverride))
                return CachePathOverride!;

            var assembly = typeof(TmdbCache).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-tmdb-cache.json");
            }

            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "tmdb-cache.json");

            return "tmdb-cache.json";
        }
    }

    private static Dictionary<string, int> Load()
    {
        if (_cache != null) return _cache;

        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                _cache = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
                _logger?.LogInformation("Loaded {Count} slug→TMDb mappings from cache", _cache.Count);
                return _cache;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load TMDb cache from {Path}", CachePath);
        }

        _cache = new Dictionary<string, int>();
        return _cache;
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            // Written beside the file and swapped in, so a crash mid-write can't leave invalid
            // JSON (which Load would treat as an empty cache).
            JsonlFile.WriteAllTextAtomic(CachePath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save TMDb cache to {Path}", CachePath);
        }
    }

    /// <summary>The cached TMDb movie id for a slug, or null when it is unknown or not a film.</summary>
    public static int? Get(string slug)
        => TryGet(slug, out var tmdbId) ? tmdbId : null;

    /// <summary>
    /// True when the slug has been resolved before. <paramref name="tmdbId"/> is its TMDb movie
    /// id, or null when the slug was found to be a TV entry.
    /// </summary>
    public static bool TryGet(string slug, out int? tmdbId)
    {
        lock (_lock)
        {
            var known = Load().TryGetValue(slug, out var stored);
            tmdbId = known && stored != NotAFilm ? stored : null;
            return known;
        }
    }

    public static void Set(string slug, int tmdbId)
    {
        lock (_lock)
        {
            var cache = Load();
            cache[slug] = tmdbId;
            Save();
        }
    }

    public static int Count
    {
        get
        {
            lock (_lock)
            {
                return Load().Count;
            }
        }
    }
}
