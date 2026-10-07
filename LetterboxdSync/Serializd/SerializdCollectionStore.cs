using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Which Jellyfin collection (BoxSet) holds each Serializd account's watchlist, keyed by
/// (Jellyfin user, Serializd email). Collections are server-wide and Jellyfin lets any number
/// share a name, so the watchlist sync reconciles only the collection recorded here and never
/// adopts one by name (beyond the one-off upgrade rule in <see cref="SerializdWatchlistSyncRunner"/>).
///
/// Kept in its own JSON file beside the plugin config rather than on <c>SerializdAccount</c>:
/// both dashboards rebuild accounts from their form fields on save, which would drop a field
/// they do not know about, and losing the id would make the next run create a duplicate.
/// </summary>
internal static class SerializdCollectionStore
{
    private const string FileName = "serializd-watchlist-collections.json";
    private static readonly object _lock = new();

    internal sealed class Entry
    {
        /// <summary>The collection's item id, "N" format.</summary>
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

        /// <summary>The configured name the plugin last applied, so a changed setting can be told apart from a rename made in Jellyfin.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

        [JsonIgnore] public Guid CollectionId => Guid.TryParse(Id, out var g) ? g : Guid.Empty;
    }

    private static string DataPath
    {
        get
        {
            var configFile = Plugin.Instance?.ConfigurationFilePath;
            var dir = string.IsNullOrEmpty(configFile) ? null : Path.GetDirectoryName(configFile);
            if (string.IsNullOrEmpty(dir))
                throw new InvalidOperationException("The plugin configurations directory is not known yet");
            return Path.Combine(dir, FileName);
        }
    }

    // The key only has to be stable and is never read back, so it holds a hash of the email
    // rather than the address itself. This is tidiness, not secrecy: the email is in the plugin
    // config in the clear, and an unsalted hash of a known address is easy to match.
    private static string Key(string userJellyfinId, string email)
        => userJellyfinId + "|" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));

    // Read on every call: the file is tiny and touched once per account per sync, and a cache
    // would need invalidating whenever Plugin.Instance (and so the path) changes. An unreadable
    // file throws rather than reading as empty: "nothing tracked" would make every account
    // create a duplicate collection, and the next write would drop everyone else's entries.
    private static Dictionary<string, Entry> Load()
    {
        if (!File.Exists(DataPath))
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(DataPath))
            ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    public static Entry? Get(string userJellyfinId, string email)
    {
        lock (_lock)
        {
            return Load().TryGetValue(Key(userJellyfinId, email), out var entry) ? entry : null;
        }
    }

    /// <summary>True when any account other than this one already tracks <paramref name="collectionId"/>.</summary>
    public static bool IsTrackedByAnotherAccount(Guid collectionId, string userJellyfinId, string email)
    {
        lock (_lock)
        {
            var self = Key(userJellyfinId, email);
            return Load().Any(kv => kv.Key != self && kv.Value.CollectionId == collectionId);
        }
    }

    public static void Set(string userJellyfinId, string email, Guid collectionId, string appliedName)
    {
        lock (_lock)
        {
            var all = Load();
            all[Key(userJellyfinId, email)] = new Entry { Id = collectionId.ToString("N"), Name = appliedName };
            var dir = Path.GetDirectoryName(DataPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var tmp = DataPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, DataPath, overwrite: true);
        }
    }
}
