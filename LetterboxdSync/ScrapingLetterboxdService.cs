using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class ScrapingLetterboxdService : ILetterboxdService
{
    private readonly LetterboxdHttpClient _http;
    private readonly LetterboxdAuth _auth;
    private readonly LetterboxdScraper _scraper;
    private readonly LetterboxdDiary _diary;

    // The diary page is addressed by slug, but callers pass back FilmResult.FilmId (the numeric
    // id on this path, per the ILetterboxdService contract), so remember which slug each id
    // this instance returned belongs to.
    private readonly ConcurrentDictionary<string, string> _slugByFilmId = new(StringComparer.Ordinal);

    public ScrapingLetterboxdService(ILogger logger, string? userAgent = null)
        : this(logger, null, userAgent)
    {
    }

    /// <summary>
    /// A service over an existing cookie jar, so a signed-in session outlives the service. The
    /// factory keeps one jar per account.
    /// </summary>
    internal ScrapingLetterboxdService(ILogger logger, string? userAgent, System.Net.CookieContainer cookies)
        : this(logger, null, userAgent, cookies)
    {
    }

    // Tests inject a mock handler; production passes null and gets the real cookie handler.
    internal ScrapingLetterboxdService(ILogger logger, System.Net.Http.HttpMessageHandler? handler, string? userAgent = null,
        System.Net.CookieContainer? cookies = null)
    {
        _http = new LetterboxdHttpClient(logger, handler, userAgent, cookies);
        _auth = new LetterboxdAuth(_http, logger);
        _scraper = new LetterboxdScraper(_http, logger);
        _diary = new LetterboxdDiary(_http, _auth, _scraper, logger);
    }

    public bool IsWebsiteSession => true;

    public async Task AuthenticateAsync(string username, string password, string? rawCookies = null)
    {
        _http.SetRawCookies(rawCookies);
        await _auth.AuthenticateAsync(username, password).ConfigureAwait(false);
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var film = await _scraper.LookupFilmByTmdbIdAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(film.FilmId) && !string.IsNullOrEmpty(film.Slug))
            _slugByFilmId[film.FilmId] = film.Slug;
        return film;
    }

    public Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username, CancellationToken cancellationToken = default)
    {
        if (_slugByFilmId.TryGetValue(filmIdOrSlug, out var slug))
            return _scraper.GetDiaryInfoAsync(slug, username, cancellationToken);

        // A numeric id this instance never returned has no slug to look up, and its diary URL
        // would 404, which reads as "never logged". Refuse rather than risk a duplicate.
        if (filmIdOrSlug.Length > 0 && filmIdOrSlug.All(char.IsAsciiDigit))
            throw new DiaryCheckFailedException(
                $"Could not check the Letterboxd diary: film id {filmIdOrSlug} was not looked up by this session");

        return _scraper.GetDiaryInfoAsync(filmIdOrSlug, username, cancellationToken);
    }

    public Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null,
        CancellationToken cancellationToken = default)
        => _diary.MarkAsWatchedAsync(filmSlug, filmId, date, liked, productionId, rewatch, rating, cancellationToken);

    public Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null)
        => _diary.PostReviewAsync(filmSlug, reviewText, containsSpoilers, isRewatch, date, rating);

    public Task<List<int>> GetWatchlistTmdbIdsAsync(string username, CancellationToken cancellationToken = default)
        => _scraper.GetWatchlistTmdbIdsAsync(username, cancellationToken);

    public Task<List<int>> GetDiaryTmdbIdsAsync(string username)
        => _scraper.GetDiaryTmdbIdsAsync(username);

    public Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username, CancellationToken cancellationToken = default)
        => _scraper.GetDiaryFilmEntriesAsync(username, cancellationToken);

    public Task SetFilmRatingAsync(string filmSlug, string filmId, double rating, CancellationToken cancellationToken = default)
        => _diary.SetFilmRatingAsync(filmSlug, filmId, rating, cancellationToken);

    public void Dispose()
    {
        _http.Dispose();
    }
}
