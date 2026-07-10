using System.Collections.Generic;

namespace LetterboxdSync.Serializd;

/// <summary>
/// A resolved Serializd episode target: the TMDb show id, the season number, and
/// the episode number(s) to log. Season number is resolved to Serializd's internal
/// season id later, by the service (it needs an API call).
/// </summary>
public sealed record SerializdEpisodeRef(int ShowTmdbId, int SeasonNumber, IReadOnlyList<int> EpisodeNumbers);

/// <summary>
/// Pure mapping from a Jellyfin episode's raw fields to a Serializd episode target.
/// Kept separate from <see cref="PlaybackHandler"/> so every edge case (missing ids,
/// specials, multi-episode files) is unit-testable without the Jellyfin entity graph.
/// </summary>
internal static class SerializdEpisodeMapper
{
    // A single media file rarely spans more than a handful of episodes; a range
    // wider than this is almost certainly bad metadata (e.g. a whole-season file
    // with IndexNumberEnd set to the finale). Log only the first episode rather
    // than firing dozens of spurious logs.
    private const int MaxRangeSpan = 24;

    public static SerializdEpisodeRef? Build(int? seriesTmdbId, int? seasonNumber, int? episodeNumber, int? episodeNumberEnd)
    {
        if (seriesTmdbId is not > 0)
            return null;

        // Season number may legitimately be 0 (Specials); reject only null/negative.
        if (seasonNumber is not >= 0)
            return null;

        if (episodeNumber is not > 0)
            return null;

        var start = episodeNumber.Value;
        var numbers = new List<int> { start };

        if (episodeNumberEnd is int end && end > start && end - start <= MaxRangeSpan)
        {
            for (var n = start + 1; n <= end; n++)
                numbers.Add(n);
        }

        return new SerializdEpisodeRef(seriesTmdbId.Value, seasonNumber.Value, numbers);
    }
}
