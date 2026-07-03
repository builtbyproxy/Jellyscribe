using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using LetterboxdSync.Security;

namespace LetterboxdSync.Configuration;

public class Account
{
    public string UserJellyfinId { get; set; } = string.Empty;

    public string LetterboxdUsername { get; set; } = string.Empty;

    [XmlIgnore]
    public string LetterboxdPassword { get; set; } = string.Empty;

    /// <summary>
    /// XML-serialized, encrypted form of <see cref="LetterboxdPassword"/> (see
    /// <see cref="SecretProtector"/>). Exists only so the on-disk config file never holds
    /// the password in plaintext; JsonIgnore keeps it out of the admin config page's
    /// GET/PUT JSON round-trip (configPage.js echoes that payload back verbatim on save,
    /// which would otherwise clobber a freshly-typed password with stale ciphertext).
    /// Nothing outside this class and tests should read or write it directly, use
    /// <see cref="LetterboxdPassword"/>.
    /// </summary>
    [XmlElement("LetterboxdPassword")]
    [JsonIgnore]
    public string LetterboxdPasswordProtected
    {
        get => SecretProtector.Protect(LetterboxdPassword) ?? string.Empty;
        set => LetterboxdPassword = SecretProtector.Unprotect(value) ?? string.Empty;
    }

    [XmlIgnore]
    public string? RawCookies { get; set; }

    /// <summary>Encrypted on-disk form of <see cref="RawCookies"/>. See <see cref="LetterboxdPasswordProtected"/>.</summary>
    [XmlElement("RawCookies")]
    [JsonIgnore]
    public string? RawCookiesProtected
    {
        get => SecretProtector.Protect(RawCookies);
        set => RawCookies = SecretProtector.Unprotect(value);
    }

    public string? UserAgent { get; set; }

    public bool Enabled { get; set; }

    public bool SyncFavorites { get; set; }

    public bool EnableDateFilter { get; set; }

    public int DateFilterDays { get; set; } = 7;

    public bool EnableWatchlistSync { get; set; }

    public bool EnableDiaryImport { get; set; }

    public bool AutoRequestWatchlist { get; set; }

    /// <summary>
    /// When true, <see cref="AutoRequestWatchlist"/> also creates an attributed Seerr
    /// request for watchlisted films that are already in the library / available, as long as
    /// this user has no existing request for them. This backfills a requester trail for films
    /// that entered the library outside Seerr (manual Radarr add, deleted request, etc.)
    /// so "who wanted this?" is answerable. Off by default: it creates request rows for
    /// already-available media (harmless for downloads, Radarr already has the file).
    /// </summary>
    public bool BackfillAvailableRequests { get; set; }

    public bool MirrorJellyseerrWatchlist { get; set; }

    public bool SkipPreviouslySynced { get; set; } = true;

    public bool StopOnFailure { get; set; }

    /// <summary>
    /// When a Jellyfin user has multiple Letterboxd accounts, the primary one is used to:
    /// (1) resolve rating conflicts on diary import (primary's rating wins), and
    /// (2) preselect the default option in manual UI dropdowns (review modal, sync buttons).
    /// Auto-sync paths still fan out to all enabled accounts; this flag does not narrow them.
    /// At most one account per UserJellyfinId should be primary; the loader auto-promotes
    /// the first enabled account if none is marked.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Optional override for the watchlist playlist name. When null, defaults to
    /// "Letterboxd Watchlist ({LetterboxdUsername})" so each account gets its own playlist.
    /// </summary>
    public string? PlaylistName { get; set; }
}
