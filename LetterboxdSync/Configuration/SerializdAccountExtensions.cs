using System;
using System.Collections.Generic;
using System.Linq;

namespace LetterboxdSync.Configuration;

/// <summary>
/// Account-selection helpers for Serializd links, mirroring
/// <see cref="AccountExtensions"/> for Letterboxd so selection rules live in one place.
/// </summary>
public static class SerializdAccountExtensions
{
    /// <summary>All enabled Serializd accounts for a Jellyfin user, in config order.</summary>
    public static IEnumerable<SerializdAccount> GetEnabledSerializdAccountsForUser(
        this PluginConfiguration config, string userJellyfinId)
    {
        if (config?.SerializdAccounts == null || string.IsNullOrEmpty(userJellyfinId))
            return Array.Empty<SerializdAccount>();

        return config.SerializdAccounts
            .Where(a => a.Enabled
                && a.UserJellyfinId == userJellyfinId
                && !string.IsNullOrWhiteSpace(a.Email))
            .OrderByDescending(a => a.IsPrimary)   // primary first (parity with Letterboxd)
            .ToList();
    }

    /// <summary>
    /// The primary enabled Serializd account for a user (the one that owns the Seerr-watchlist
    /// mirror destination), or the first enabled account if none is flagged. Mirrors
    /// <see cref="AccountExtensions.GetPrimaryAccountForUser"/>.
    /// </summary>
    public static SerializdAccount? GetPrimarySerializdAccountForUser(
        this PluginConfiguration config, string userJellyfinId)
    {
        var enabled = config.GetEnabledSerializdAccountsForUser(userJellyfinId).ToList();
        if (enabled.Count == 0) return null;
        return enabled.FirstOrDefault(a => a.IsPrimary) ?? enabled[0];
    }

    /// <summary>Resolved watchlist collection/playlist name, defaulting to "Serializd Watchlist".</summary>
    public static string GetWatchlistName(this SerializdAccount account)
        => string.IsNullOrWhiteSpace(account.WatchlistName) ? "Serializd Watchlist" : account.WatchlistName!.Trim();
}
