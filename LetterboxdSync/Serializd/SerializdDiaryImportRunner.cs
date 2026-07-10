using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Reverse import: marks Jellyfin episodes played when they appear on the user's Serializd
/// diary. The TV counterpart to the Letterboxd <c>DiaryImportTask</c>. Opt-in per account via
/// <see cref="SerializdAccount.EnableDiaryImport"/>. Records imported episodes in
/// <see cref="SerializdSyncHistory"/> under a dedicated kind so the scrobble catch-up won't
/// bounce them straight back (import-then-export loop guard).
/// </summary>
public class SerializdDiaryImportRunner
{
    internal const string KindImported = "imported";

    private readonly ILogger<SerializdDiaryImportRunner> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public SerializdDiaryImportRunner(
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _logger = loggerFactory.CreateLogger<SerializdDiaryImportRunner>();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public async Task RunForAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var pairs = _userManager.GetUsers()
            .SelectMany(u => Config.GetEnabledSerializdAccountsForUser(u.Id.ToString("N"))
                .Where(a => a.EnableDiaryImport)
                .Select(a => (User: u, Account: a)))
            .ToList();

        var processed = 0;
        foreach (var (user, account) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ImportOneAsync(user, account, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd diary import failed for {Username} as {Email}: {Message}",
                    user.Username, account.Email, ex.Message);
            }

            processed++;
            if (pairs.Count > 0)
                progress.Report((double)processed / pairs.Count * 100);
        }

        progress.Report(100);
    }

    private async Task ImportOneAsync(User user, SerializdAccount account, CancellationToken cancellationToken)
    {
        List<SerializdDiaryEpisode> diary;
        using (var service = await SerializdServiceFactory
                   .CreateAuthenticatedAsync(account.Email, account.Password, _logger).ConfigureAwait(false))
        {
            diary = await service.GetDiaryEpisodesAsync().ConfigureAwait(false);
        }

        if (diary.Count == 0) return;

        // Index the user's episodes by (series TMDb id, season number, episode number).
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Episode>();

        var byKey = new Dictionary<(int, int, int), Episode>();
        foreach (var ep in episodes)
        {
            if (!int.TryParse(ep.Series?.GetProviderId(MetadataProvider.Tmdb), out var tmdb)) continue;
            if (ep.ParentIndexNumber is not int season || ep.IndexNumber is not int number) continue;
            byKey[(tmdb, season, number)] = ep;
        }

        var userId = user.Id.ToString("N");
        var marked = 0;
        foreach (var d in diary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byKey.TryGetValue((d.ShowTmdbId, d.SeasonNumber, d.EpisodeNumber), out var ep)) continue;

            var ud = _userDataManager.GetUserData(user, ep);
            if (ud.Played) continue;

            // Do NOT set LastPlayedDate: the scrobble catch-up skips played episodes without a
            // play date, so an imported episode won't be re-logged straight back to Serializd.
            ud.Played = true;
            _userDataManager.SaveUserData(user, ep, ud, MediaBrowser.Model.Entities.UserDataSaveReason.Import, cancellationToken);
            SerializdSyncHistory.Record(userId, d.ShowTmdbId, d.SeasonNumber, d.EpisodeNumber, KindImported);
            marked++;
        }

        if (marked > 0)
            _logger.LogInformation("Serializd diary import: marked {Count} episodes played for {Username} as {Email}",
                marked, user.Username, account.Email);
    }
}
