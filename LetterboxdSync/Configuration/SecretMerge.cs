using System;
using System.Linq;

namespace LetterboxdSync.Configuration;

/// <summary>
/// Secrets (passwords, raw cookies, the Seerr API key) are write-only: no JSON
/// response carries them, so every client saves without them. These rules turn such a save back
/// into the intended change: an empty value keeps the stored secret, a non-empty one replaces it,
/// and an explicit clear flag drops it. A stored account secret carries over to the same login
/// (Jellyfin user + Letterboxd username or Serializd email). An edit that renames the login or
/// moves it to another Jellyfin user names the login it started from in the write-only Original*
/// fields, and the secrets follow it; without them a different login starts empty.
/// </summary>
internal static class SecretMerge
{
    internal static string? KeepIfEmpty(string? incoming, string? stored)
        => string.IsNullOrEmpty(incoming) ? stored : incoming;

    /// <summary>The login an edit started from: <paramref name="original"/> when the client named one, else <paramref name="current"/>.</summary>
    internal static string OriginalOr(string? original, string current)
        => string.IsNullOrWhiteSpace(original) ? current : original;

    /// <summary>Jellyfin user ids are stored without dashes; a client may send either form.</summary>
    private static bool SameUser(string? a, string? b)
        => string.Equals(a?.Replace("-", string.Empty), b?.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>For a secret with a clear flag: a typed value wins; otherwise the flag drops the stored one, or it is kept.</summary>
    internal static string? TypedClearedOrKept(string? incoming, bool clear, string? stored)
        => !string.IsNullOrEmpty(incoming) ? incoming : clear ? null : stored;

    internal static Account? FindStored(this PluginConfiguration stored, string userJellyfinId, string letterboxdUsername)
        => stored.Accounts?.FirstOrDefault(a =>
            SameUser(a.UserJellyfinId, userJellyfinId) &&
            string.Equals(a.LetterboxdUsername?.Trim(), letterboxdUsername?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static SerializdAccount? FindStoredSerializd(this PluginConfiguration stored, string userJellyfinId, string email)
        => stored.SerializdAccounts?.FirstOrDefault(a =>
            SameUser(a.UserJellyfinId, userJellyfinId) &&
            string.Equals(a.Email?.Trim(), email?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Fills the secrets an incoming account left empty from its stored counterpart.</summary>
    internal static void KeepSecretsFrom(this Account incoming, Account? stored)
    {
        incoming.LetterboxdPassword = KeepIfEmpty(incoming.LetterboxdPassword, stored?.LetterboxdPassword) ?? string.Empty;
        incoming.RawCookies = TypedClearedOrKept(incoming.RawCookies, incoming.ClearRawCookies, stored?.RawCookies);
        incoming.ClearRawCookies = false;
    }

    internal static void KeepSecretsFrom(this SerializdAccount incoming, SerializdAccount? stored)
        => incoming.Password = KeepIfEmpty(incoming.Password, stored?.Password) ?? string.Empty;

    /// <summary>Applies the rules to a whole config about to replace <paramref name="stored"/>.</summary>
    internal static void KeepSecretsFrom(this PluginConfiguration incoming, PluginConfiguration stored)
    {
        // Only Jellyfin's admin-only plugin configuration endpoint reaches this, so the Original*
        // fields may point at any user's stored login.
        foreach (var account in incoming.Accounts ?? new())
        {
            account.KeepSecretsFrom(stored.FindStored(
                OriginalOr(account.OriginalUserJellyfinId, account.UserJellyfinId),
                OriginalOr(account.OriginalLetterboxdUsername, account.LetterboxdUsername)));
            account.OriginalUserJellyfinId = null;
            account.OriginalLetterboxdUsername = null;
        }

        foreach (var account in incoming.SerializdAccounts ?? new())
        {
            account.KeepSecretsFrom(stored.FindStoredSerializd(
                OriginalOr(account.OriginalUserJellyfinId, account.UserJellyfinId),
                OriginalOr(account.OriginalEmail, account.Email)));
            account.OriginalUserJellyfinId = null;
            account.OriginalEmail = null;
        }

        incoming.JellyseerrApiKey = TypedClearedOrKept(incoming.JellyseerrApiKey, incoming.ClearJellyseerrApiKey, stored.JellyseerrApiKey);
        incoming.ClearJellyseerrApiKey = false;
    }

    /// <summary>
    /// True when a stored secret may be sent to <paramref name="url"/>: it is the destination the
    /// secret was saved for, so a test request can't send it anywhere else.
    /// </summary>
    internal static bool IsStoredUrl(string? url, string? storedUrl)
    {
        if (!Uri.TryCreate(url?.Trim().TrimEnd('/'), UriKind.Absolute, out var a) ||
            !Uri.TryCreate(storedUrl?.Trim().TrimEnd('/'), UriKind.Absolute, out var b))
            return false;
        // Scheme and host are case-insensitive (Uri lower-cases them); the path is compared exactly.
        return a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port &&
               string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.Ordinal);
    }
}
