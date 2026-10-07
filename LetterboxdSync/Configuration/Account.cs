using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using LetterboxdSync.Security;

namespace LetterboxdSync.Configuration;

public class Account
{
    public string UserJellyfinId { get; set; } = string.Empty;

    public string LetterboxdUsername { get; set; } = string.Empty;

    /// <summary>
    /// Plaintext password, in memory only. JsonIgnore keeps it out of every JSON response,
    /// including Jellyfin's GET /Plugins/{id}/Configuration; JSON writes go through
    /// <see cref="LetterboxdPasswordInput"/> instead (see <see cref="SecretMerge"/>).
    /// </summary>
    [XmlIgnore]
    [JsonIgnore]
    public string LetterboxdPassword { get; set; } = string.Empty;

    /// <summary>
    /// Write-only JSON form of <see cref="LetterboxdPassword"/>, under the old JSON name so a
    /// config POST can still set it. The getter is internal, so System.Text.Json never writes it.
    /// An empty value means "keep the stored password" (<see cref="SecretMerge"/>).
    /// </summary>
    [XmlIgnore]
    [JsonPropertyName("LetterboxdPassword")]
    public string? LetterboxdPasswordInput
    {
        internal get => LetterboxdPassword;
        set => LetterboxdPassword = value ?? string.Empty;
    }

    [XmlIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(LetterboxdPassword);

    /// <summary>
    /// XML-serialized, encrypted form of <see cref="LetterboxdPassword"/> (see
    /// <see cref="SecretProtector"/>). Exists only so the on-disk config file never holds
    /// the password in plaintext; JsonIgnore keeps it out of the admin config page's
    /// GET/PUT JSON round-trip (configPage.html echoes that payload back verbatim on save,
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

    /// <summary>Plaintext raw cookies, in memory only. Same JSON rules as <see cref="LetterboxdPassword"/>.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public string? RawCookies { get; set; }

    /// <summary>Write-only JSON form of <see cref="RawCookies"/>. See <see cref="LetterboxdPasswordInput"/>.</summary>
    [XmlIgnore]
    [JsonPropertyName("RawCookies")]
    public string? RawCookiesInput
    {
        internal get => RawCookies;
        set => RawCookies = value;
    }

    [XmlIgnore]
    public bool HasRawCookies => !string.IsNullOrEmpty(RawCookies);

    /// <summary>Write-only: true on a config POST drops the stored cookies, which an empty value would keep.</summary>
    [XmlIgnore]
    public bool ClearRawCookies { internal get; set; }

    /// <summary>
    /// Write-only: the owner this account had before the edit being saved, when an admin moved
    /// it to another Jellyfin user. Lets <see cref="SecretMerge"/> carry the stored password and
    /// cookies across the move. Empty means unchanged.
    /// </summary>
    [XmlIgnore]
    public string? OriginalUserJellyfinId { internal get; set; }

    /// <summary>Write-only: the username before a rename, for the same reason as <see cref="OriginalUserJellyfinId"/>.</summary>
    [XmlIgnore]
    public string? OriginalLetterboxdUsername { internal get; set; }

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

    /// <summary>
    /// When true, a rating changed in Jellyfin after (or without) a watch is pushed to this
    /// account's Letterboxd film rating by <see cref="RatingSyncHandler"/>. Defaults on, which is
    /// also what configs saved before this setting existed deserialize to.
    /// </summary>
    public bool SyncRatings { get; set; } = true;

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

    /// <summary>
    /// Jellyfin library ids (CollectionFolder ids, "N" format) whose items this account never
    /// exports, on the scheduled and real-time paths alike. Empty means every library syncs, which
    /// is also what configs saved before this setting existed deserialize to. Import paths ignore it.
    /// </summary>
    public List<string> ExcludedLibraryIds { get; set; } = new List<string>();
}
