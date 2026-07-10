using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using LetterboxdSync.Security;
using MediaBrowser.Model.Plugins;

namespace LetterboxdSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Account> Accounts { get; set; } = new List<Account>();

    /// <summary>
    /// Serializd (TV) account links, one or more per Jellyfin user. Independent of
    /// <see cref="Accounts"/> (Letterboxd/film); a user can link either, both, or neither.
    /// </summary>
    public List<SerializdAccount> SerializdAccounts { get; set; } = new List<SerializdAccount>();

    /// <summary>
    /// Base URL of the Seerr instance, e.g. "http://192.168.1.122:5055" or "https://requests.example.com".
    /// Trailing slash is stripped at use time.
    /// </summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>
    /// Seerr API key (Settings → General → API Key in Seerr).
    /// </summary>
    [XmlIgnore]
    public string? JellyseerrApiKey { get; set; }

    /// <summary>Encrypted on-disk form of <see cref="JellyseerrApiKey"/>. See <see cref="Configuration.Account.LetterboxdPasswordProtected"/> for why this is JsonIgnore'd.</summary>
    [XmlElement("JellyseerrApiKey")]
    [JsonIgnore]
    public string? JellyseerrApiKeyProtected
    {
        get => SecretProtector.Protect(JellyseerrApiKey);
        set => JellyseerrApiKey = SecretProtector.Unprotect(value);
    }

    /// <summary>
    /// Anonymous opt-in usage telemetry state. Off by default; nothing is ever sent
    /// while disabled. See <see cref="TelemetryData"/> for what persists and why.
    /// </summary>
    public TelemetryData Telemetry { get; set; } = new();

    /// <summary>
    /// One-shot guard for the catalog migration that adds the proxied (edge-cached)
    /// plugin repository entry alongside the GitHub one (v1.19.0). Set after the
    /// first attempt so a user who deletes the added entry is never overridden.
    /// </summary>
    public bool CatalogMigrationDone { get; set; }
}
