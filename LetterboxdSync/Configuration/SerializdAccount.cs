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

    /// <summary>Opt-in: mirror this account's Serializd watchlist into a Jellyfin playlist ("Serializd Watchlist").</summary>
    public bool SyncWatchlist { get; set; }
}
