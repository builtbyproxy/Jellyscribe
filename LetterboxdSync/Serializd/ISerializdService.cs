using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Business operations against a single authenticated Serializd account. The
/// factory (<see cref="SerializdServiceFactory"/>) hands back an already-logged-in
/// implementation, so callers never deal with tokens directly.
/// </summary>
public interface ISerializdService : IDisposable
{
    /// <summary>
    /// Maps a TMDb show id + season number to Serializd's own internal season id
    /// (required by the log endpoints). Returns null when the show has no season
    /// with that number (e.g. absolute-numbered anime, or a season not yet on
    /// Serializd), so callers can log-and-skip rather than fail.
    /// </summary>
    Task<int?> ResolveSeasonIdAsync(int showTmdbId, int seasonNumber);

    /// <summary>Marks the given episode numbers watched on Serializd.</summary>
    Task LogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers);

    /// <summary>
    /// Creates a dated diary log for a single episode (Serializd's `is_log` entry), stamped
    /// with the real watch date (<paramref name="watchedAtUtc"/> → <c>backdate</c>) and an
    /// optional rating. This is what populates the Diary/Reviews tabs, distinct from the
    /// watched-status marking done by <see cref="LogEpisodesAsync"/>.
    /// </summary>
    /// <param name="rating">Serializd rating 1..10 (10 = 5★), or null for unrated.</param>
    Task CreateEpisodeLogAsync(int showTmdbId, int seasonId, int episodeNumber,
        DateTime watchedAtUtc, int? rating, bool isRewatch);

    /// <summary>Removes the given episode numbers from watched (used by tests to clean up).</summary>
    Task UnlogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers);

    /// <summary>
    /// Sets show-level metadata: the user's rating and/or a like (heart), as a non-diary
    /// entry (<c>is_log:false</c>, so it shows as "your rating"/like on the show without
    /// adding a Diary row). Used to mirror a Jellyfin series rating + favorite.
    /// </summary>
    /// <param name="rating">Serializd rating 1..10, or null to leave unrated.</param>
    /// <param name="like">True to heart the show (Jellyfin favorite).</param>
    Task SetShowMetaAsync(int showTmdbId, int? rating, bool like);

    /// <summary>Returns the TMDb show ids on the authenticated user's Serializd watchlist.</summary>
    Task<List<int>> GetWatchlistShowTmdbIdsAsync();
}
