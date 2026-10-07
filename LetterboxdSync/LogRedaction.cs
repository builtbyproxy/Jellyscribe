using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LetterboxdSync;

/// <summary>
/// Keeps email addresses out of what the plugin logs and out of the log lines an admin can send
/// to the developer. Serializd accounts are keyed by email, so log calls name them by
/// <see cref="AccountTag"/> instead, and <see cref="RedactEmails"/> masks any address that still
/// reaches a log line (an older log, or an email typed as a Letterboxd login or quoted in an
/// error message).
/// </summary>
internal static class LogRedaction
{
    public const string EmailPlaceholder = "[email]";

    // The @ also as it appears URL-encoded, as an HTML entity or escaped in JSON, since logged
    // URLs and response bodies carry addresses that way. NonBacktracking keeps the scan linear
    // on any line, however long or odd.
    // The local part takes the usual address characters but not = & ? /, which would swallow a
    // URL's "username=" or path before the address; the domain may hold non-ASCII letters.
    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+'!#$*^`{|}~\-]+(?:@|%40|%2540|&#64;|\\u0040)(?:[\p{L}\p{N}\-]+\.)+\p{L}{2,}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Older releases logged a posted review's reply as "Review response for <slug>: status=<n>, body=<text>".
    private static readonly Regex LegacyReviewBody = new(
        @"(Review response for \S+: status=\d+), body=.*$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>Replaces every email address in <paramref name="line"/> with <see cref="EmailPlaceholder"/>.</summary>
    public static string RedactEmails(string line)
        => string.IsNullOrEmpty(line) ? line : EmailPattern.Replace(line, EmailPlaceholder);

    /// <summary>
    /// Cuts the body from a review-reply line an older release logged (the body can echo the
    /// review). Returns true when it did, so the caller can drop that entry's continuation lines.
    /// </summary>
    public static bool TryCutLegacyReviewBody(ref string line)
    {
        var cut = LegacyReviewBody.Replace(line, "$1, body=[removed]");
        if (cut == line) return false;
        line = cut;
        return true;
    }

    /// <summary>
    /// A short, stable label for a Serializd account in log lines, such as <c>serializd-3fa2b1</c>:
    /// the first 6 hex digits of the SHA-256 of the lowercased email. It tells an admin's several
    /// accounts apart in one log without writing the address down. The tag alone does not reveal
    /// the address, though anyone who already knows an address can check it against the tag, and
    /// the same address gets the same tag on every server. With 24 bits, many addresses share a tag.
    /// </summary>
    public static string AccountTag(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "serializd-none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        return "serializd-" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
    }
}
