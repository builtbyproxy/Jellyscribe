using System;
using System.Collections.Generic;
using System.Linq;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace LetterboxdSync;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Jellyscribe";

    // BasePlugin derives the config file name from Name by default (Name + ".xml"),
    // so renaming the display name would otherwise silently point every install at a
    // brand-new, empty LetterboxdSync.xml -> Jellyscribe.xml config file instead of the
    // one holding every user's actual linked accounts. Pinned here so the rebrand truly
    // carries no functional or data changes, matching the GUID staying fixed below.
    public override string ConfigurationFileName => "LetterboxdSync.xml";

    public override Guid Id => Guid.Parse("c7a3e1b9-5d42-4f8a-9c06-2b7d8e4f1a35");

    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Normalise the at-most-one-primary-per-Jellyfin-user invariant on every config
    /// save. The per-user PutAccount endpoint already does this, but the admin config
    /// page saves the entire PluginConfiguration via the stock plugin API which bypasses
    /// the controller; without this hook an admin can leave two accounts marked primary
    /// for the same Jellyfin user and rating-conflict resolution becomes ambiguous.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration cfg)
        {
            // Close auth breakers for accounts whose credentials just changed. The admin
            // dashboard saves through this method rather than the user-facing /Accounts
            // endpoint, and only that endpoint reset breakers, so an admin who fixed a stale
            // password stayed stuck on "Login failing - sync paused" forever. Re-creating the
            // account didn't help either: the breaker is keyed on (user, Letterboxd username),
            // so a fresh entry inherits the old breaker state. See issue #112.
            //
            // Scoped to accounts whose credentials actually changed (or that are new). A blanket
            // reset on every config write would re-open the breaker whenever an unrelated
            // setting is saved, which is exactly the retry storm the breaker exists to prevent.
            ResetBreakersForChangedCredentials(cfg);

            cfg.NormalisePrimaryFlags();

            // Telemetry identity is generated server-side at the moment of opt-in, so
            // every UI path (banner, checkbox, raw API) gets the same guarantee: random
            // UUID, never derived from anything, plus a per-instance jitter slot.
            cfg.Telemetry ??= new TelemetryData();
            if (cfg.Telemetry.Enabled && string.IsNullOrEmpty(cfg.Telemetry.InstanceId))
            {
                cfg.Telemetry.InstanceId = Guid.NewGuid().ToString();
                cfg.Telemetry.JitterMinutes = Random.Shared.Next(0, 720);
            }
        }
        base.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Compares the incoming config against the one currently in memory and closes the auth
    /// breaker for any Letterboxd account whose password or raw cookies changed, plus any account
    /// that isn't in the old config at all (a newly added or re-created one, which would otherwise
    /// inherit a stale breaker keyed on the same user + username).
    /// </summary>
    private void ResetBreakersForChangedCredentials(PluginConfiguration incoming)
    {
        try
        {
            var old = Configuration;
            if (old?.Accounts == null) return;

            // Jellyfin's UpdatePluginConfiguration always deserializes a fresh object, so the
            // incoming config is a different instance and the comparison below is meaningful.
            // An in-process caller that mutates Configuration directly and passes it back gives
            // us nothing to diff against; leave breakers alone rather than blanket-resetting,
            // which would reopen every paused account on an unrelated save.
            if (ReferenceEquals(old, incoming)) return;

            foreach (var account in incoming.Accounts ?? new List<Account>())
            {
                if (string.IsNullOrWhiteSpace(account.UserJellyfinId) ||
                    string.IsNullOrWhiteSpace(account.LetterboxdUsername))
                    continue;

                var previous = old.Accounts.FirstOrDefault(a =>
                    string.Equals(a.UserJellyfinId, account.UserJellyfinId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.LetterboxdUsername, account.LetterboxdUsername, StringComparison.OrdinalIgnoreCase));

                var credentialsChanged = previous == null
                    || !string.Equals(previous.LetterboxdPassword, account.LetterboxdPassword, StringComparison.Ordinal)
                    || !string.Equals(previous.RawCookies ?? string.Empty, account.RawCookies ?? string.Empty, StringComparison.Ordinal);

                if (credentialsChanged)
                    AuthBreaker.Reset(account.UserJellyfinId, account.LetterboxdUsername);
            }
        }
        catch (Exception)
        {
            // Never let breaker bookkeeping stop a config save. AuthBreaker does its own
            // logging; there is no logger on the plugin entry point to report through here.
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "letterboxdsync",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.html",
                EnableInMainMenu = true,
                DisplayName = "Jellyscribe",
            },
            new PluginPageInfo
            {
                Name = "letterboxdsyncjs",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.configPage.js"
            },
            new PluginPageInfo
            {
                Name = "letterboxdstats",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.statsPage.html",
            },
            new PluginPageInfo
            {
                Name = "letterboxduser",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.userPage.html",
            }
        };
    }
}
