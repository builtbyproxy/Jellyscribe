using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LetterboxdSync.Serializd;

/// <summary>A show on the Serializd watchlist, with the specific season numbers watchlisted.</summary>
public sealed record SerializdWatchlistEntry(int ShowTmdbId, IReadOnlyList<int> SeasonNumbers);

/// <summary>An episode-level entry on the Serializd diary (used to mark Jellyfin episodes played).</summary>
public sealed record SerializdDiaryEpisode(int ShowTmdbId, int SeasonNumber, int EpisodeNumber);

/// <summary>
/// Business operations against a single authenticated Serializd account. The
/// factory (<see cref="SerializdServiceFactory"/>) hands back an already-logged-in
/// implementation, so callers never deal with tokens directly. The optional
/// <see cref="CancellationToken"/> on the methods the sync runners call cancels the waits
/// (backoff and rate-limit pauses) and the reads, never a write already sent.
/// </summary>
public interface ISerializdService : IDisposable
{
    /// <summary>
    /// Maps a TMDb show id + season number to Serializd's own internal season id
    /// (required by the log endpoints). Returns null when the show has no season
    /// with that number (e.g. absolute-numbered anime, or a season not yet on
    /// Serializd), so callers can log-and-skip rather than fail.
    /// </summary>
    Task<int?> ResolveSeasonIdAsync(int showTmdbId, int seasonNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// The number of episodes Serializd lists for a season, or null when the show has no such
    /// season or Serializd gives no count.
    /// </summary>
    Task<int?> GetSeasonEpisodeCountAsync(int showTmdbId, int seasonNumber, CancellationToken cancellationToken = default);

    /// <summary>Marks the given episode numbers watched on Serializd.</summary>
    Task LogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a dated diary log for a single episode (Serializd's `is_log` entry), stamped
    /// with the real watch date (<paramref name="watchedAtUtc"/> → <c>backdate</c>) and an
    /// optional rating. This is what populates the Diary/Reviews tabs, distinct from the
    /// watched-status marking done by <see cref="LogEpisodesAsync"/>.
    /// </summary>
    /// <param name="rating">Serializd rating 1..10 (10 = 5★), or null for unrated.</param>
    Task CreateEpisodeLogAsync(int showTmdbId, int seasonId, int episodeNumber,
        DateTime watchedAtUtc, int? rating, bool isRewatch, CancellationToken cancellationToken = default);

    /// <summary>Removes the given episode numbers from watched (used by tests to clean up).</summary>
    Task UnlogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers);

    /// <summary>
    /// Sets show-level metadata: the user's rating and/or a like (heart), as a non-diary
    /// entry (<c>is_log:false</c>, so it shows as "your rating"/like on the show without
    /// adding a Diary row). Used to mirror a Jellyfin series rating + favorite.
    /// </summary>
    /// <param name="rating">Serializd rating 1..10, or null to leave unrated.</param>
    /// <param name="like">True to heart the show (Jellyfin favorite).</param>
    Task SetShowMetaAsync(int showTmdbId, int? rating, bool like, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authenticated user's Serializd watchlist: each show (TMDb id) plus the
    /// specific season numbers watchlisted (empty = the whole show / no season detail).
    /// </summary>
    Task<List<SerializdWatchlistEntry>> GetWatchlistAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a show-level review (text + optional rating), the Serializd equivalent of a Letterboxd review.</summary>
    Task CreateShowReviewAsync(int showTmdbId, int? rating, string? reviewText, bool containsSpoiler);

    /// <summary>
    /// Writes a review against a specific episode, so it attaches to that episode rather than the
    /// whole show. <paramref name="seasonId"/> and <paramref name="episodeNumber"/> are Serializd's,
    /// resolved through <see cref="SerializdSeasonFallback"/> like an episode log, so a show
    /// Serializd lists as one season gets the absolute episode number.
    /// </summary>
    Task CreateEpisodeReviewAsync(int showTmdbId, int seasonId, int episodeNumber, int? rating, string? reviewText, bool containsSpoiler);

    /// <summary>Reads the authenticated user's Serializd diary as episode-level entries (for import to Jellyfin).</summary>
    Task<List<SerializdDiaryEpisode>> GetDiaryEpisodesAsync(CancellationToken cancellationToken = default);
}
