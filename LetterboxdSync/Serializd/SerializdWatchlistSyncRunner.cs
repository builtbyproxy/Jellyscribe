using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Mirrors a user's Serializd watchlist into a Jellyfin **collection** ("Serializd Watchlist").
/// A collection (BoxSet) is the right container for TV shows, a playlist expands a Series into
/// its episodes. Only shows already in the library are added, matched by the watchlist item's
/// TMDb show id. Opt-in per account via <see cref="SerializdAccount.SyncWatchlist"/>.
///
/// v1 is add-only: it adds newly-watchlisted shows, but does not remove shows you later drop
/// from your Serializd watchlist (deferred).
/// </summary>
public class SerializdWatchlistSyncRunner
{
    private const string CollectionName = "Serializd Watchlist";

    private readonly ILogger<SerializdWatchlistSyncRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ICollectionManager _collectionManager;

    public SerializdWatchlistSyncRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ICollectionManager collectionManager)
    {
        _logger = loggerFactory.CreateLogger<SerializdWatchlistSyncRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _collectionManager = collectionManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public async Task RunForAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pairs = _userManager.GetUsers()
            .SelectMany(u => Config.GetEnabledSerializdAccountsForUser(u.Id.ToString("N"))
                .Where(a => a.SyncWatchlist)
                .Select(a => (User: u, Account: a)))
            .ToList();

        var processed = 0;
        foreach (var (user, account) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncOneAsync(user, account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd watchlist sync failed for {Username} as {Email}: {Message}",
                    user.Username, account.Email, ex.Message);
            }

            processed++;
            if (pairs.Count > 0)
                progress.Report((double)processed / pairs.Count * 100);
        }

        progress.Report(100);
    }

    public async Task<bool> TryRunForUserAsync(string userJellyfinId, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userJellyfinId);
        if (user == null) return false;

        var accounts = Config.GetEnabledSerializdAccountsForUser(userJellyfinId).Where(a => a.SyncWatchlist).ToList();
        if (accounts.Count == 0) return false;

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncOneAsync(user, account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd watchlist sync failed for {Username} as {Email}: {Message}",
                    user.Username, account.Email, ex.Message);
            }
        }

        return true;
    }

    private async Task SyncOneAsync(User user, SerializdAccount account, CancellationToken cancellationToken)
    {
        List<int> tmdbIds;
        using (var service = await SerializdServiceFactory
                   .CreateAuthenticatedAsync(account.Email, account.Password, _logger).ConfigureAwait(false))
        {
            tmdbIds = await service.GetWatchlistShowTmdbIdsAsync().ConfigureAwait(false);
        }

        // Match watchlist shows to Series already in the Jellyfin library, by TMDb id.
        var seriesList = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true,
        });

        var wanted = new HashSet<Guid>();
        foreach (var tmdbId in tmdbIds)
        {
            var match = seriesList.FirstOrDefault(s => s.GetProviderId(MetadataProvider.Tmdb) == tmdbId.ToString());
            if (match != null) wanted.Add(match.Id);
        }

        _logger.LogInformation("Serializd watchlist: {Total} shows, {Matched} in library, for {Username}",
            tmdbIds.Count, wanted.Count, user.Username);

        if (wanted.Count == 0)
            return;

        var existing = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
        }).FirstOrDefault(b => string.Equals(b.Name, CollectionName, StringComparison.Ordinal));

        if (existing == null)
        {
            await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = CollectionName,
                ItemIdList = wanted.Select(g => g.ToString("N")).ToList(),
                UserIds = new[] { user.Id },
            }).ConfigureAwait(false);
            _logger.LogInformation("Created '{Name}' collection with {Count} shows for {Username}",
                CollectionName, wanted.Count, user.Username);
            return;
        }

        // Add-only: AddToCollectionAsync ignores shows already in the collection.
        await _collectionManager.AddToCollectionAsync(existing.Id, wanted).ConfigureAwait(false);
        _logger.LogInformation("Updated '{Name}' collection ({Count} watchlist shows in library) for {Username}",
            CollectionName, wanted.Count, user.Username);
    }
}
