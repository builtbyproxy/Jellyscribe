using System.Collections.Generic;

namespace LetterboxdSync.Api;

public class AccountUpdateRequest
{
    public string LetterboxdUsername { get; set; } = string.Empty;

    public string LetterboxdPassword { get; set; } = string.Empty;

    /// <summary>Empty keeps the stored cookies; <see cref="ClearRawCookies"/> drops them.</summary>
    public string? RawCookies { get; set; }

    public bool ClearRawCookies { get; set; }

    /// <summary>
    /// The username this account had before the edit, when the user renamed it. The stored
    /// password, cookies and other kept settings follow the rename. Empty means unchanged.
    /// </summary>
    public string? OriginalLetterboxdUsername { get; set; }

    public string? UserAgent { get; set; }

    public bool Enabled { get; set; }

    public bool SyncFavorites { get; set; }

    /// <summary>
    /// Null means the client did not send the field: PutAccount and PutAccounts then keep the
    /// stored value, and a new account gets the default (on).
    /// </summary>
    public bool? SyncRatings { get; set; }

    public bool EnableDateFilter { get; set; }

    public int DateFilterDays { get; set; } = 7;

    public bool EnableWatchlistSync { get; set; }

    public bool EnableDiaryImport { get; set; }

    public bool AutoRequestWatchlist { get; set; }

    public bool BackfillAvailableRequests { get; set; }

    public bool MirrorJellyseerrWatchlist { get; set; }

    public bool SkipPreviouslySynced { get; set; } = true;

    public bool StopOnFailure { get; set; }

    public bool IsPrimary { get; set; }

    public string? PlaylistName { get; set; }

    /// <summary>
    /// Library ids this account never exports. Null means the client did not send the field, and
    /// both PutAccount and PutAccounts then keep the account's stored list.
    /// </summary>
    public List<string>? ExcludedLibraryIds { get; set; }
}

/// <summary>
/// Bulk-replace payload for the per-user multi-account endpoint. The caller submits
/// the full set of accounts they want for themselves; the server ignores any
/// UserJellyfinId in the request and stamps every entry with the calling user.
/// </summary>
public class AccountsUpdateRequest
{
    public List<AccountUpdateRequest> Accounts { get; set; } = new List<AccountUpdateRequest>();
}

public class JellyseerrTestRequest
{
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
}
