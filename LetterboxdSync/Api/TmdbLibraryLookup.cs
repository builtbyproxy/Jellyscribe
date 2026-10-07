using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace LetterboxdSync.Api;

/// <summary>Finds a user's library items by TMDb id, for the dashboard endpoints.</summary>
internal static class TmdbLibraryLookup
{
    /// <summary>
    /// Library items of one kind carrying the given TMDb id, filtered in the database
    /// instead of loading the whole library. The id is re-checked in memory, so a query
    /// that ever came back looser could not resolve (and write a rating to) the wrong item.
    /// </summary>
    internal static IEnumerable<BaseItem> FindByTmdbId(ILibraryManager libraryManager, User user, BaseItemKind kind, int tmdbId)
    {
        var id = tmdbId.ToString(CultureInfo.InvariantCulture);
        return libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = id }
        }).Where(item => item.GetProviderId(MetadataProvider.Tmdb) == id);
    }
}
