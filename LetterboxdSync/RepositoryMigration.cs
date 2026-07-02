using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// One-shot startup migration that ADDS the Cloudflare Worker's proxied manifest URL
/// to the Jellyfin plugin catalog alongside this plugin's existing raw-GitHub entry.
/// The Worker serves the identical manifest (edge-cached) and counting its polls is
/// the only way to measure the active install base continuously, downloads already
/// route through the Worker, but those are only observable at each release's update
/// wave.
///
/// Deliberate properties, in order of importance:
/// - The GitHub entry is NEVER removed or rewritten: it stays as a working fallback
///   update path if the Worker is unreachable (workers.dev is blocked in some
///   networks) or ever goes away.
/// - The migration runs ONCE per install (guarded by
///   <see cref="PluginConfiguration.CatalogMigrationDone"/>): a user who deletes the
///   added entry has opted out and is never overridden on a later boot.
/// - Only our own repository entries are considered; every other catalog entry the
///   user has configured is left alone.
/// </summary>
public class RepositoryMigrationService : IHostedService
{
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<RepositoryMigrationService> _logger;

    public RepositoryMigrationService(
        IServerConfigurationManager configurationManager,
        ILogger<RepositoryMigrationService> logger)
    {
        _configurationManager = configurationManager;
        _logger = logger;
    }

    // Test seams; production defaults reach the Plugin singleton (same pattern as
    // LetterboxdServiceFactory.OverrideForTesting).
    internal Func<PluginConfiguration?> GetPluginConfiguration { get; set; } =
        () => Plugin.Instance?.Configuration;

    internal Action SavePluginConfiguration { get; set; } =
        () => Plugin.Instance?.SaveConfiguration();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Best-effort: a failure here must never affect plugin (or server) startup.
        try
        {
            var pluginConfiguration = GetPluginConfiguration();
            if (pluginConfiguration is null || pluginConfiguration.CatalogMigrationDone)
                return Task.CompletedTask;

            var configuration = _configurationManager.Configuration;
            var original = configuration.PluginRepositories;
            if (RepositoryMigrator.TryAddProxiedEntry(configuration))
            {
                try
                {
                    _configurationManager.SaveConfiguration();
                }
                catch
                {
                    // TryAddProxiedEntry never mutates the original entries, so
                    // restoring the original array fully undoes the migration in
                    // memory. Without this, a later unrelated SaveConfiguration by
                    // the server would persist a half-failed migration. The flag
                    // below stays unset, so the migration retries next boot.
                    configuration.PluginRepositories = original;
                    throw;
                }

                _logger.LogInformation(
                    "Added proxied LetterboxdSync catalog entry {Url} alongside the existing GitHub entry",
                    RepositoryMigrator.ProxiedManifestUrl);
            }

            // One-shot, even when there was nothing to add: never re-run, so a user
            // who later deletes the proxied entry (or their raw entry) is not
            // second-guessed on every boot.
            pluginConfiguration.CatalogMigrationDone = true;
            SavePluginConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LetterboxdSync repository migration failed; catalog left unchanged");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Pure migration logic, separated from the hosted service so tests can exercise it
/// against a plain <see cref="ServerConfiguration"/> without a running server.
/// Assigns a NEW array and never mutates the pre-existing <see cref="RepositoryInfo"/>
/// objects, so a caller that snapshots the original array can fully revert by
/// assigning it back (see the save-failure path above).
/// </summary>
internal static class RepositoryMigrator
{
    internal const string RawGitHubManifestUrl =
        "https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json";

    internal const string ProxiedManifestUrl =
        "https://lbsync-telemetry.lachlanbyoung.workers.dev/manifest.json";

    /// <summary>
    /// Appends the proxied-manifest repository entry if the catalog has this plugin's
    /// raw-GitHub entry and no proxied entry yet. Returns whether the configuration
    /// was modified and needs saving. A catalog without our raw entry is left alone , 
    /// sideloaded installs and users who removed the repo get nothing added.
    /// </summary>
    internal static bool TryAddProxiedEntry(ServerConfiguration configuration)
    {
        var repositories = configuration.PluginRepositories;
        if (repositories is null || repositories.Length == 0)
            return false;

        if (repositories.Any(r => r is not null && UrlEquals(r.Url, ProxiedManifestUrl)))
            return false;

        var rawEntries = repositories
            .Where(r => r is not null && UrlEquals(r.Url, RawGitHubManifestUrl))
            .ToArray();
        if (rawEntries.Length == 0)
            return false;

        var baseName = rawEntries
            .Select(r => r.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Letterboxd Sync";

        configuration.PluginRepositories = repositories
            .Append(new RepositoryInfo
            {
                Name = baseName + " (mirror)",
                Url = ProxiedManifestUrl,
                // An enabled raw entry never gains a disabled mirror; a fully
                // disabled raw entry gets an equally disabled mirror.
                Enabled = rawEntries.Any(r => r.Enabled),
            })
            .ToArray();
        return true;
    }

    private static bool UrlEquals(string? url, string expected)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return string.Equals(url.Trim().TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase);
    }
}
