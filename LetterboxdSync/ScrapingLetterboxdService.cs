using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    // Tests inject a mock handler; production passes null and gets the real cookie handler.
    internal ScrapingLetterboxdService(ILogger logger, System.Net.Http.HttpMessageHandler? handler, string? userAgent = null)
    {
        _http = new LetterboxdHttpClient(logger, handler, userAgent);
        _auth = new LetterboxdAuth(_http, logger);
        _scraper = new LetterboxdScraper(_http, logger);
        _diary = new LetterboxdDiary(_http, _auth, _scraper, logger);
    }

    public async Task AuthenticateAsync(string username, string password, string? rawCookies = null)
    {
        _http.SetRawCookies(rawCookies);
        await _auth.AuthenticateAsync(username, password).ConfigureAwait(false);
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId)
    {
        var film = await _scraper.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(film.FilmId) && !string.IsNullOrEmpty(film.Slug))
            _slugByFilmId[film.FilmId] = film.Slug;
        return film;
    }

    public Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username)
    {
        if (_slugByFilmId.TryGetValue(filmIdOrSlug, out var slug))
            return _scraper.GetDiaryInfoAsync(slug, username);

        // A numeric id this instance never returned has no slug to look up, and its diary URL
        // would 404, which reads as "never logged". Refuse rather than risk a duplicate.
        if (filmIdOrSlug.Length > 0 && filmIdOrSlug.All(char.IsAsciiDigit))
            throw new DiaryCheckFailedException(
                $"Could not check the Letterboxd diary: film id {filmIdOrSlug} was not looked up by this session");

        return _scraper.GetDiaryInfoAsync(filmIdOrSlug, username);
    }

    public Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null)
        => _diary.MarkAsWatchedAsync(filmSlug, filmId, date, liked, productionId, rewatch, rating);

    public Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null)
        => _diary.PostReviewAsync(filmSlug, reviewText, containsSpoilers, isRewatch, date, rating);

    public Task<List<int>> GetWatchlistTmdbIdsAsync(string username)
        => _scraper.GetWatchlistTmdbIdsAsync(username);

    public Task<List<int>> GetDiaryTmdbIdsAsync(string username)
        => _scraper.GetDiaryTmdbIdsAsync(username);

    public Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username)
        => _scraper.GetDiaryFilmEntriesAsync(username);

    public Task SetFilmRatingAsync(string filmSlug, string filmId, double rating)
        => _diary.SetFilmRatingAsync(filmSlug, filmId, rating);

    public void Dispose()
    {
        _http.Dispose();
    }
}
