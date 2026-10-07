using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LetterboxdSync;

/// <remarks>
/// The optional <see cref="CancellationToken"/> on the methods the sync runners call cancels the
/// waits (pacing, rate-limit and backoff) and the reads. It never cuts off a write already sent,
/// since that write may have landed.
/// </remarks>
public interface ILetterboxdService : IDisposable
{
    Task AuthenticateAsync(string username, string password, string? rawCookies = null);

    Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True for the website (scraping) session. Its film lookup makes page requests of its own,
    /// so callers pace the next request after the lookup rather than alongside it.
    /// </summary>
    bool IsWebsiteSession => false;

    Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username, CancellationToken cancellationToken = default);

    Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null,
        CancellationToken cancellationToken = default);

    Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null);

    Task<List<int>> GetWatchlistTmdbIdsAsync(string username, CancellationToken cancellationToken = default);

    Task<List<int>> GetDiaryTmdbIdsAsync(string username);

    Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the member's film rating (the one on the film page and profile, not a diary entry).
    /// <paramref name="rating"/> is Letterboxd half-stars, 0.5 to 5.0, already mapped by the caller
    /// with <see cref="Helpers.MapRating"/>; each implementation converts to its own wire scale.
    /// <paramref name="filmId"/> must come from this instance's <see cref="LookupFilmByTmdbIdAsync"/>,
    /// since the two implementations use different id forms. Letterboxd also marks a rated film
    /// watched, which removes it from the member's watchlist.
    /// </summary>
    Task SetFilmRatingAsync(string filmSlug, string filmId, double rating, CancellationToken cancellationToken = default);
}
