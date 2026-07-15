using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

public class RepositoryMigrationTests
{
    private static ServerConfiguration ConfigWith(params RepositoryInfo[] repositories)
        => new() { PluginRepositories = repositories };

    [Fact]
    public void Adds_Proxied_Entry_Alongside_RawGitHub_Entry()
    {
        var raw = new RepositoryInfo
        {
            Name = "Letterboxd Sync",
            Url = RepositoryMigrator.RawGitHubManifestUrl,
            Enabled = true,
        };
        var config = ConfigWith(raw);

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));

        Assert.Equal(2, config.PluginRepositories.Length);
        Assert.Same(raw, config.PluginRepositories[0]);
        Assert.Equal(RepositoryMigrator.RawGitHubManifestUrl, raw.Url);

        var mirror = config.PluginRepositories[1];
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, mirror.Url);
        Assert.Equal("Letterboxd Sync (mirror)", mirror.Name);
        Assert.True(mirror.Enabled);
    }

    [Fact]
    public void Mirror_Is_Disabled_When_All_Raw_Entries_Are_Disabled()
    {
        var config = ConfigWith(new RepositoryInfo
        {
            Name = "Letterboxd Sync",
            Url = RepositoryMigrator.RawGitHubManifestUrl,
            Enabled = false,
        });

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.False(config.PluginRepositories[1].Enabled);
    }

    [Fact]
    public void Mirror_Is_Enabled_When_Any_Of_Multiple_Raw_Entries_Is_Enabled()
    {
        var config = ConfigWith(
            new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl, Enabled = false },
            new RepositoryInfo { Name = "Letterboxd Sync (dup)", Url = RepositoryMigrator.RawGitHubManifestUrl + "/", Enabled = true });

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));

        // Both raw entries stay; exactly one mirror is appended.
        Assert.Equal(3, config.PluginRepositories.Length);
        var mirror = config.PluginRepositories[2];
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, mirror.Url);
        Assert.True(mirror.Enabled);
    }

    [Theory]
    [InlineData("HTTPS://RAW.GITHUBUSERCONTENT.COM/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json")]
    [InlineData("https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json/")]
    [InlineData("  https://raw.githubusercontent.com/builtbyproxy/jellyfin-plugin-letterboxd/main/manifest.json ")]
    public void Matches_Raw_Url_Ignoring_Case_TrailingSlash_And_Whitespace(string url)
    {
        var config = ConfigWith(new RepositoryInfo { Name = "Letterboxd Sync", Url = url });

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, config.PluginRepositories[1].Url);
    }

    [Fact]
    public void NoOp_When_Proxied_Entry_Already_Exists()
    {
        var config = ConfigWith(
            new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl },
            new RepositoryInfo { Name = "Letterboxd Sync (mirror)", Url = RepositoryMigrator.ProxiedManifestUrl });

        Assert.False(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.Equal(2, config.PluginRepositories.Length);
    }

    [Fact]
    public void NoOp_When_Only_Proxied_Entry_Exists()
    {
        var config = ConfigWith(new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.ProxiedManifestUrl });

        Assert.False(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.Single(config.PluginRepositories);
    }

    [Fact]
    public void Leaves_Unrelated_Repositories_Alone()
    {
        var thirdParty = new RepositoryInfo
        {
            Name = "File Transformation",
            Url = "https://www.iamparadox.dev/jellyfin/plugins/manifest.json",
        };
        var config = ConfigWith(
            thirdParty,
            new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl });

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.Equal(3, config.PluginRepositories.Length);
        Assert.Same(thirdParty, config.PluginRepositories[0]);
    }

    [Fact]
    public void Does_Not_Mutate_Original_Entries_Or_Array()
    {
        // The save-failure revert in RepositoryMigrationService depends on this:
        // restoring the original array must fully undo the migration.
        var raw = new RepositoryInfo
        {
            Name = "Letterboxd Sync",
            Url = RepositoryMigrator.RawGitHubManifestUrl,
            Enabled = true,
        };
        var originalArray = new[] { raw };
        var config = new ServerConfiguration { PluginRepositories = originalArray };

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));

        Assert.NotSame(originalArray, config.PluginRepositories);
        var single = Assert.Single(originalArray);
        Assert.Same(raw, single);
        Assert.Equal(RepositoryMigrator.RawGitHubManifestUrl, raw.Url);
    }

    [Fact]
    public void NoOp_When_No_Repositories_Configured()
    {
        Assert.False(RepositoryMigrator.TryAddProxiedEntry(ConfigWith()));
    }

    [Fact]
    public void NoOp_When_PluginRepositories_Is_Null()
    {
        var config = new ServerConfiguration { PluginRepositories = null! };

        Assert.False(RepositoryMigrator.TryAddProxiedEntry(config));
    }

    [Fact]
    public void NoOp_When_Plugin_Not_In_Catalog()
    {
        // Sideloaded installs / users who removed the repo get nothing added.
        var config = ConfigWith(new RepositoryInfo { Name = "Other", Url = "https://example.com/manifest.json" });

        Assert.False(RepositoryMigrator.TryAddProxiedEntry(config));
    }

    [Fact]
    public void Tolerates_Null_Element_And_Null_Url_Entries()
    {
        var config = ConfigWith(
            null!,
            new RepositoryInfo { Name = "Broken", Url = null },
            new RepositoryInfo { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl });

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.Equal(4, config.PluginRepositories.Length);
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, config.PluginRepositories[3].Url);
    }

    [Fact]
    public void Mirror_Name_Falls_Back_When_Raw_Entry_Has_No_Name()
    {
        var config = ConfigWith(new RepositoryInfo { Name = null, Url = RepositoryMigrator.RawGitHubManifestUrl });

        Assert.True(RepositoryMigrator.TryAddProxiedEntry(config));
        Assert.Equal("Jellyscribe (mirror)", config.PluginRepositories[1].Name);
    }
}

public class RepositoryMigrationServiceTests
{
    private static (RepositoryMigrationService Service, IServerConfigurationManager Manager, ServerConfiguration Config, PluginConfiguration PluginConfig)
        Setup(bool migrationDone = false, params RepositoryInfo[] repositories)
    {
        var config = new ServerConfiguration { PluginRepositories = repositories };
        var manager = Substitute.For<IServerConfigurationManager>();
        manager.Configuration.Returns(config);
        var pluginConfig = new PluginConfiguration { CatalogMigrationDone = migrationDone };
        var service = new RepositoryMigrationService(manager, NullLogger<RepositoryMigrationService>.Instance)
        {
            GetPluginConfiguration = () => pluginConfig,
            SavePluginConfiguration = () => { },
        };
        return (service, manager, config, pluginConfig);
    }

    private static RepositoryInfo RawEntry() =>
        new() { Name = "Letterboxd Sync", Url = RepositoryMigrator.RawGitHubManifestUrl, Enabled = true };

    [Fact]
    public async Task StartAsync_AddsMirror_SavesServerConfig_And_SetsFlag()
    {
        var (service, manager, config, pluginConfig) = Setup(migrationDone: false, RawEntry());

        await service.StartAsync(CancellationToken.None);

        manager.Received(1).SaveConfiguration();
        Assert.Equal(2, config.PluginRepositories.Length);
        Assert.Equal(RepositoryMigrator.ProxiedManifestUrl, config.PluginRepositories[1].Url);
        Assert.True(pluginConfig.CatalogMigrationDone);
    }

    [Fact]
    public async Task StartAsync_DoesNothing_WhenFlagAlreadySet()
    {
        // The opt-out contract: once the migration has run, a user who deletes the
        // mirror entry must never see it re-added.
        var (service, manager, config, _) = Setup(migrationDone: true, RawEntry());

        await service.StartAsync(CancellationToken.None);

        manager.DidNotReceive().SaveConfiguration();
        Assert.Single(config.PluginRepositories);
    }

    [Fact]
    public async Task StartAsync_SetsFlag_EvenWhenNothingToAdd()
    {
        var (service, manager, _, pluginConfig) = Setup(
            migrationDone: false,
            new RepositoryInfo { Name = "Other", Url = "https://example.com/manifest.json" });

        await service.StartAsync(CancellationToken.None);

        manager.DidNotReceive().SaveConfiguration();
        Assert.True(pluginConfig.CatalogMigrationDone);
    }

    [Fact]
    public async Task StartAsync_SwallowsException_Reverts_And_LeavesFlagUnset_WhenSaveFails()
    {
        var raw = RawEntry();
        var (service, manager, config, pluginConfig) = Setup(migrationDone: false, raw);
        manager.When(m => m.SaveConfiguration()).Do(_ => throw new InvalidOperationException("disk full"));

        var ex = await Record.ExceptionAsync(() => service.StartAsync(CancellationToken.None));

        Assert.Null(ex);
        // In-memory config must match what's on disk: the un-migrated original.
        Assert.Same(raw, Assert.Single(config.PluginRepositories));
        // Flag stays unset so the migration retries on the next boot.
        Assert.False(pluginConfig.CatalogMigrationDone);
    }

    [Fact]
    public async Task StartAsync_DoesNothing_WhenPluginNotInitialized()
    {
        var config = new ServerConfiguration { PluginRepositories = new[] { RawEntry() } };
        var manager = Substitute.For<IServerConfigurationManager>();
        manager.Configuration.Returns(config);
        var service = new RepositoryMigrationService(manager, NullLogger<RepositoryMigrationService>.Instance)
        {
            GetPluginConfiguration = () => null,
        };

        await service.StartAsync(CancellationToken.None);

        manager.DidNotReceive().SaveConfiguration();
        Assert.Single(config.PluginRepositories);
    }
}
