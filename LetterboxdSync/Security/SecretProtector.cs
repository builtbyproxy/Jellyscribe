using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace LetterboxdSync.Security;

/// <summary>
/// Encrypts secret configuration fields (Letterboxd password, raw cookies, Seerr API key)
/// at rest so LetterboxdSync.xml never holds them in plaintext. Uses ASP.NET Core's Data
/// Protection API with a key ring persisted next to the plugin's other data files (same
/// convention as <see cref="SyncHistory"/>'s DataPath).
///
/// Threat model: this raises the bar against the config file being copied out in isolation
/// (backups, git, pasted into a support thread/bug report) since the ciphertext is useless
/// without the separate key ring. It does NOT protect against an attacker with full read
/// access to the Jellyfin data volume, because the key ring has to live somewhere the
/// plugin can reach it too, on Linux Data Protection has no OS keystore (DPAPI is
/// Windows-only) to encrypt the key ring itself. That ceiling is inherent to any
/// self-hosted secret storage without an external KMS.
/// </summary>
internal static class SecretProtector
{
    private const string Purpose = "LetterboxdSync.Secrets.v1";
    private const string Prefix = "enc:v1:";

    /// <summary>
    /// Test-only hook for the key ring location, mirrors <see cref="SyncHistory.DataPathOverride"/>.
    /// Production never assigns this; the default KeyDirectory logic uses the plugin's
    /// configurations directory.
    /// </summary>
    internal static string? KeyDirectoryOverride { get; set; }

    private static IDataProtector? _protector;
    private static readonly object _lock = new();

    private static IDataProtector Protector
    {
        get
        {
            if (_protector != null) return _protector;
            lock (_lock)
            {
                _protector ??= CreateProtector();
                return _protector;
            }
        }
    }

    /// <summary>Test hook: drop the cached protector so the next access re-derives it (e.g. after changing KeyDirectoryOverride).</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _protector = null; }
    }

    private static IDataProtector CreateProtector()
    {
        var dir = KeyDirectoryOverride ?? DefaultKeyDirectory();
        Directory.CreateDirectory(dir);
        TryRestrictPermissions(dir);

        var provider = DataProtectionProvider.Create(new DirectoryInfo(dir));
        return provider.CreateProtector(Purpose);
    }

    private static string DefaultKeyDirectory()
    {
        var assembly = typeof(SecretProtector).Assembly.Location;
        var pluginDir = Path.GetDirectoryName(assembly);
        if (!string.IsNullOrEmpty(pluginDir))
        {
            var configDir = Path.Combine(pluginDir, "..", "configurations");
            if (Directory.Exists(configDir))
                return Path.Combine(configDir, "letterboxd-sync-keys");

            return Path.Combine(pluginDir, "letterboxd-sync-keys");
        }

        return "letterboxd-sync-keys";
    }

    // Best-effort on Unix; the container runs as a single non-root user (PUID/PGID) so this
    // mainly keeps the key ring out of any accidental world-readable default. Never blocks
    // startup over a chmod failure.
    private static void TryRestrictPermissions(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
        }
    }

    /// <summary>Encrypts a plaintext secret for storage. Null/empty pass through unchanged.</summary>
    internal static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        return Prefix + Protector.Protect(plaintext);
    }

    /// <summary>
    /// Decrypts a stored value. A value without the <c>enc:v1:</c> prefix is treated as
    /// legacy plaintext, written before encryption was added, and returned as-is; the next
    /// save re-encrypts it. Returns null if the value is prefixed but fails to decrypt
    /// (key ring missing/rotated/corrupted), so callers can tell "no value" apart from
    /// "value present but unreadable" if they need to.
    /// </summary>
    internal static string? Unprotect(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return storedValue;
        if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal)) return storedValue;

        try
        {
            return Protector.Unprotect(storedValue[Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
