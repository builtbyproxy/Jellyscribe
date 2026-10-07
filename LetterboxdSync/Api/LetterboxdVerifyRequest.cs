namespace LetterboxdSync.Api;

/// <summary>
/// Credentials to check with <see cref="LetterboxdController.VerifyLogin"/>; nothing is saved. An
/// empty password or cookie field falls back to the stored account with this username.
/// </summary>
public class LetterboxdVerifyRequest
{
    public string? LetterboxdUsername { get; set; }

    public string? LetterboxdPassword { get; set; }

    public string? RawCookies { get; set; }

    public bool ClearRawCookies { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Owner of the stored account to fall back to. Honoured for administrators only; others always use their own.</summary>
    public string? UserJellyfinId { get; set; }

    /// <summary>The stored account's username when the form renamed it; empty means <see cref="LetterboxdUsername"/>.</summary>
    public string? OriginalLetterboxdUsername { get; set; }
}
