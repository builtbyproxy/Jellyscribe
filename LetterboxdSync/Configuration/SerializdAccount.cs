using System.Text.Json.Serialization;
using System.Xml.Serialization;
using LetterboxdSync.Security;

namespace LetterboxdSync.Configuration;

/// <summary>
/// A Jellyfin user's link to one Serializd account. Mirrors <see cref="Account"/>
/// (Letterboxd) but for TV scrobbling. Serializd logs in by email + password; the
/// password is encrypted at rest with the same shadow-property pattern as the
/// Letterboxd password (see <see cref="Account.LetterboxdPasswordProtected"/>).
/// </summary>
public class SerializdAccount
{
    public string UserJellyfinId { get; set; } = string.Empty;

    /// <summary>Serializd login email. Not a secret (it's the account identifier), stored in the clear like the Letterboxd username.</summary>
    public string Email { get; set; } = string.Empty;

    [XmlIgnore]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted on-disk form of <see cref="Password"/>. XmlElement keeps the on-disk
    /// element name stable; JsonIgnore keeps ciphertext out of the admin config page's
    /// GET/PUT round-trip. See <see cref="Account.LetterboxdPasswordProtected"/>.
    /// </summary>
    [XmlElement("SerializdPassword")]
    [JsonIgnore]
    public string SerializdPasswordProtected
    {
        get => SecretProtector.Protect(Password) ?? string.Empty;
        set => Password = SecretProtector.Unprotect(value) ?? string.Empty;
    }

    /// <summary>Serializd username returned at login, for display in the UI. Not used for auth.</summary>
    public string? SerializdUsername { get; set; }

    public bool Enabled { get; set; }

    /// <summary>Mark shows that are Jellyfin favourites as liked (hearted) on Serializd. Mirrors <see cref="Account.SyncFavorites"/>.</summary>
    public bool SyncFavorites { get; set; }

    /// <summary>Limit the catch-up to episodes watched within the last <see cref="DateFilterDays"/> days. Mirrors <see cref="Account.EnableDateFilter"/>.</summary>
    public bool EnableDateFilter { get; set; }

    /// <summary>Look-back window (days) for <see cref="EnableDateFilter"/>. Mirrors <see cref="Account.DateFilterDays"/>.</summary>
    public int DateFilterDays { get; set; } = 7;

    /// <summary>Opt-in: mirror this account's Serializd watchlist into a Jellyfin collection + playlist. Mirrors <see cref="Account.EnableWatchlistSync"/>.</summary>
    public bool SyncWatchlist { get; set; }

    /// <summary>Skip episodes already logged locally (the dedup history), so the catch-up doesn't re-log them. Mirrors <see cref="Account.SkipPreviouslySynced"/>.</summary>
    public bool SkipPreviouslySynced { get; set; } = true;

    /// <summary>Halt this account's catch-up on the first failure. Mirrors <see cref="Account.StopOnFailure"/>.</summary>
    public bool StopOnFailure { get; set; }

    /// <summary>Primary account for this Jellyfin user (multi-account tie-breaks). Mirrors <see cref="Account.IsPrimary"/>.</summary>
    public bool IsPrimary { get; set; }
}
