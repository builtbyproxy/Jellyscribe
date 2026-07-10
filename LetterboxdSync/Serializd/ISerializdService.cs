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

    /// <summary>Removes the given episode numbers from watched (used by tests to clean up).</summary>
    Task UnlogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers);
}
