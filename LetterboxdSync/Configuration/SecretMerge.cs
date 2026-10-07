using System;
using System.Linq;

namespace LetterboxdSync.Configuration;

/// <summary>
/// Secrets (passwords, raw cookies, the Seerr API key) are write-only: no JSON
/// response carries them, so every client saves without them. These rules turn such a save back
/// into the intended change: an empty value keeps the stored secret, a non-empty one replaces it,
/// and an explicit clear flag drops it. A stored account secret only carries over to the same
/// login (Jellyfin user + Letterboxd username or Serializd email); a renamed login starts empty.
/// </summary>
internal static class SecretMerge
{
    internal static string? KeepIfEmpty(string? incoming, string? stored)
        => string.IsNullOrEmpty(incoming) ? stored : incoming;

    internal static Account? FindStored(this PluginConfiguration stored, string userJellyfinId, string letterboxdUsername)
        => stored.Accounts?.FirstOrDefault(a =>
            string.Equals(a.UserJellyfinId, userJellyfinId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.LetterboxdUsername, letterboxdUsername?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static SerializdAccount? FindStoredSerializd(this PluginConfiguration stored, string userJellyfinId, string email)
        => stored.SerializdAccounts?.FirstOrDefault(a =>
            string.Equals(a.UserJellyfinId, userJellyfinId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Email?.Trim(), email?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Fills the secrets an incoming account left empty from its stored counterpart.</summary>
    internal static void KeepSecretsFrom(this Account incoming, Account? stored)
    {
        incoming.LetterboxdPassword = KeepIfEmpty(incoming.LetterboxdPassword, stored?.LetterboxdPassword) ?? string.Empty;
        incoming.RawCookies = incoming.ClearRawCookies ? null : KeepIfEmpty(incoming.RawCookies, stored?.RawCookies);
        incoming.ClearRawCookies = false;
    }

    internal static void KeepSecretsFrom(this SerializdAccount incoming, SerializdAccount? stored)
        => incoming.Password = KeepIfEmpty(incoming.Password, stored?.Password) ?? string.Empty;

    /// <summary>Applies the rules to a whole config about to replace <paramref name="stored"/>.</summary>
    internal static void KeepSecretsFrom(this PluginConfiguration incoming, PluginConfiguration stored)
    {
        foreach (var account in incoming.Accounts ?? new())
            account.KeepSecretsFrom(stored.FindStored(account.UserJellyfinId, account.LetterboxdUsername));

        foreach (var account in incoming.SerializdAccounts ?? new())
            account.KeepSecretsFrom(stored.FindStoredSerializd(account.UserJellyfinId, account.Email));

        incoming.JellyseerrApiKey = KeepIfEmpty(incoming.JellyseerrApiKey, stored.JellyseerrApiKey);
    }

    /// <summary>
    /// True when a stored secret may be sent to <paramref name="url"/>: it is the destination the
    /// secret was saved for, so a test request can't send it anywhere else.
    /// </summary>
    internal static bool IsStoredUrl(string? url, string? storedUrl)
        => !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(storedUrl) &&
           string.Equals(url.Trim().TrimEnd('/'), storedUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
