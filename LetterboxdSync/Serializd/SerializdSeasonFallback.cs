using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace LetterboxdSync.Serializd;

internal sealed record SerializdSeasonTarget(int SeasonId, int EpisodeOffset, int? EpisodeCount = null)
{
    /// <summary>
    /// The episode number to log on Serializd, or null when it would land past the end of the
    /// season Serializd has, which means the offset no longer matches Serializd's numbering.
    /// </summary>
    public int? EpisodeFor(int episode)
    {
        var number = episode + EpisodeOffset;
        return number > EpisodeCount ? null : number;
    }
}

internal static class SerializdSeasonFallback
{
    internal static Func<Series?, IReadOnlyDictionary<int, int>> SeasonLengthsReader { get; set; } = ReadSeasonLengths;

    public static async Task<SerializdSeasonTarget?> ResolveAsync(
        ISerializdService service, int showTmdbId, int seasonNumber, Func<IReadOnlyDictionary<int, int>> seasonLengths,
        CancellationToken cancellationToken = default)
    {
        var seasonId = await service.ResolveSeasonIdAsync(showTmdbId, seasonNumber, cancellationToken).ConfigureAwait(false);
        if (seasonId != null)
            return new SerializdSeasonTarget(seasonId.Value, 0);

        if (seasonNumber < 2)
            return null;
        if (seasonNumber != 2 && await service.ResolveSeasonIdAsync(showTmdbId, 2, cancellationToken).ConfigureAwait(false) != null)
            return null;

        var firstSeasonId = await service.ResolveSeasonIdAsync(showTmdbId, 1, cancellationToken).ConfigureAwait(false);
        if (firstSeasonId == null)
            return null;

        var firstSeasonCount = await service.GetSeasonEpisodeCountAsync(showTmdbId, 1, cancellationToken).ConfigureAwait(false);
        var offset = EpisodeOffset(seasonNumber, seasonLengths());
        if (firstSeasonCount == null || offset == null || offset >= firstSeasonCount)
            return null;

        return new SerializdSeasonTarget(firstSeasonId.Value, offset.Value, firstSeasonCount);
    }

    internal static int? EpisodeOffset(int seasonNumber, IReadOnlyDictionary<int, int> seasonLengths)
    {
        var offset = 0;
        for (var season = 1; season < seasonNumber; season++)
        {
            if (!seasonLengths.TryGetValue(season, out var length) || length <= 0)
                return null;
            offset += length;
        }

        return offset;
    }

    internal static IReadOnlyDictionary<int, int> ReadSeasonLengths(Series? series)
    {
        if (series == null)
            return new Dictionary<int, int>();

        return series.GetRecursiveChildren(i => i is Episode)
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber > 0 && e.IndexNumber > 0)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .ToDictionary(g => g.Key, g => g.Max(e => Math.Max(e.IndexNumber!.Value, e.IndexNumberEnd ?? 0)));
    }
}
