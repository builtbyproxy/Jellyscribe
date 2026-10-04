namespace LetterboxdSync.Api;

/// <summary>Credentials to check with <see cref="LetterboxdController.VerifyLogin"/>; nothing is saved.</summary>
public class LetterboxdVerifyRequest
{
    public string? LetterboxdUsername { get; set; }

    public string? LetterboxdPassword { get; set; }

    public string? RawCookies { get; set; }

    public string? UserAgent { get; set; }
}
