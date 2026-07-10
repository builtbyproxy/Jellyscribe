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
            .ToList();
    }
}
