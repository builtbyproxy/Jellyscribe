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

    // NonBacktracking keeps the scan linear on any line, however long or odd.
    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>Replaces every email address in <paramref name="line"/> with <see cref="EmailPlaceholder"/>.</summary>
    public static string RedactEmails(string line)
        => string.IsNullOrEmpty(line) ? line : EmailPattern.Replace(line, EmailPlaceholder);

    /// <summary>
    /// A short, stable label for a Serializd account in log lines, such as <c>serializd-3fa2b1</c>:
    /// the first 6 hex digits of the SHA-256 of the lowercased email. It tells an admin's several
    /// accounts apart in one log without writing the address down. The tag alone does not reveal
    /// the address, though anyone who already knows an address can check it against the tag.
    /// </summary>
    public static string AccountTag(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "serializd-none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()));
        return "serializd-" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
    }
}
