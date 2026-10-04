using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Diary operations: mark as watched, post reviews.
/// </summary>
public class LetterboxdDiary
{
    private readonly LetterboxdHttpClient _http;
    private readonly LetterboxdAuth _auth;
    private readonly LetterboxdScraper _scraper;
    private readonly ILogger _logger;

    public LetterboxdDiary(LetterboxdHttpClient http, LetterboxdAuth auth, LetterboxdScraper scraper, ILogger logger)
    {
        _http = http;
        _auth = auth;
        _scraper = scraper;
        _logger = logger;
    }

    public async Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null)
    {
        var viewingDate = date ?? DateTime.Now;
        _logger.LogDebug("MarkAsWatched: slug={Slug}, date={Date}, rewatch={Rewatch}, rating={Rating}",
            filmSlug, viewingDate.ToString("yyyy-MM-dd"), rewatch, rating);

        for (int attempt = 0; attempt < LetterboxdHttpClient.MaxRetries; attempt++)
        {
            await _http.RefreshCsrfAsync().ConfigureAwait(false);

            var result = await TryMarkWithEndpointFallbackAsync(
                filmSlug, filmId, productionId, viewingDate, liked, rewatch, rating, attempt).ConfigureAwait(false);

            switch (result)
            {
                case MarkResult.Success:
                    _auth.ResetReauthGuard();
                    return;

                case MarkResult.NeedsReauth:
                    if (_auth.ShouldReauthenticate())
                    {
                        _logger.LogWarning("Got 401, session expired. Re-authenticating and retrying");
                        await _auth.ForceReauthenticateAsync().ConfigureAwait(false);
                        continue;
                    }
                    throw new Exception($"Letterboxd returned 401 for {filmSlug} after re-authentication. Session may be permanently invalid.");

                case MarkResult.TransientError:
                    var delayMs = (attempt + 1) * 5000 + Random.Shared.Next(3000);
                    _logger.LogWarning("Transient error, retrying in {Delay}ms", delayMs);
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    continue;

                case MarkResult.AllEndpoints404:
                    if (attempt < LetterboxdHttpClient.MaxRetries - 1)
                    {
                        var d = (attempt + 1) * 5000 + Random.Shared.Next(3000);
                        _logger.LogWarning("All endpoints returned 404, retrying in {Delay}ms", d);
                        await Task.Delay(d).ConfigureAwait(false);
                    }
                    continue;
            }
        }

        throw new Exception($"Failed to log {filmSlug} after {LetterboxdHttpClient.MaxRetries} retries.");
    }

    private async Task<MarkResult> TryMarkWithEndpointFallbackAsync(
        string filmSlug, string filmId, string? productionId,
        DateTime viewingDate, bool liked, bool rewatch, double? rating, int attempt)
    {
        var endpoints = new[] { "/api/v0/production-log-entries", "/api/v0/log-entries" };

        foreach (var endpoint in endpoints)
        {
            var payload = BuildDiaryPayload(endpoint, filmId, productionId, viewingDate, liked, rewatch, rating);
            string jsonBody = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            _http.SetApiHeaders(req, filmSlug);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var res = await _http.Http.SendAsync(req).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            _logger.LogInformation("MarkAsWatched attempt {Attempt} ({Endpoint}): status={Status}, bodyLen={Len}",
                attempt + 1, endpoint, (int)res.StatusCode, body.Length);

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Endpoint {Endpoint} returned 404, trying next", endpoint);
                continue;
            }

            if (res.StatusCode == HttpStatusCode.Unauthorized)
                return MarkResult.NeedsReauth;

            if (res.StatusCode == HttpStatusCode.Forbidden)
                throw new Exception($"Letterboxd returned 403 for {filmSlug}. Likely anti-bot.");

            if ((int)res.StatusCode >= 200 && (int)res.StatusCode < 300)
            {
                _logger.LogInformation("Successfully logged {Slug} via {Endpoint}", filmSlug, endpoint);
                return MarkResult.Success;
            }

            var sanitized = Regex.Replace(body, @"\s+", " ").Trim();
            _logger.LogError("Diary save failed for {Slug} via {Endpoint}: status={Status}, body={Body}",
                filmSlug, endpoint, (int)res.StatusCode, LetterboxdHttpClient.Truncate(sanitized, 500));

            if (sanitized.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                sanitized.Contains("try again", StringComparison.OrdinalIgnoreCase))
            {
                return MarkResult.TransientError;
            }

            throw new Exception($"Letterboxd returned {(int)res.StatusCode} for {filmSlug}");
        }

        return MarkResult.AllEndpoints404;
    }

    public async Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null)
    {
        await _http.RefreshCsrfAsync().ConfigureAwait(false);

        // Look up film identifiers
        using var filmReq = new HttpRequestMessage(HttpMethod.Get, $"/film/{filmSlug}/");
        _http.SetNavHeaders(filmReq.Headers, "same-origin");
        using var filmRes = await _http.Http.SendAsync(filmReq).ConfigureAwait(false);
        filmRes.EnsureSuccessStatusCode();

        var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
        var (filmId, productionId) = _scraper.ExtractFilmIdentifiers(filmHtml, filmSlug, filmRes.Headers);

        if (string.IsNullOrEmpty(productionId) && string.IsNullOrEmpty(filmId))
            throw new Exception($"Could not resolve film identifiers for /film/{filmSlug}/");

        var diaryDate = !string.IsNullOrEmpty(date) ? date : DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var payload = new Dictionary<string, object?>
        {
            ["diaryDetails"] = new Dictionary<string, object>
            {
                ["diaryDate"] = diaryDate,
                ["rewatch"] = isRewatch
            },
            ["tags"] = Array.Empty<string>(),
            ["like"] = false
        };

        if (!string.IsNullOrWhiteSpace(reviewText))
        {
            payload["review"] = new Dictionary<string, object>
            {
                ["text"] = reviewText,
                ["containsSpoilers"] = containsSpoilers
            };
        }

        if (rating.HasValue)
            payload["rating"] = rating.Value;

        if (!string.IsNullOrEmpty(productionId))
            payload["productionId"] = productionId;
        else
            payload["filmId"] = filmId;

        var jsonBody = JsonSerializer.Serialize(payload);
        // Log payload metadata only, never the review text itself, since users
        // share these logs for support and review drafts can be private.
        _logger.LogInformation(
            "PostReview prepared for {FilmSlug}: hasText={HasText}, textLen={TextLen}, " +
            "rating={Rating}, isRewatch={IsRewatch}, containsSpoilers={Spoilers}",
            filmSlug,
            !string.IsNullOrEmpty(reviewText),
            reviewText?.Length ?? 0,
            rating,
            isRewatch,
            containsSpoilers);

        for (int attempt = 0; attempt < LetterboxdHttpClient.MaxRetries; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v0/production-log-entries");
            _http.SetApiHeaders(req, filmSlug);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var res = await _http.Http.SendAsync(req).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (res.StatusCode == HttpStatusCode.Forbidden && attempt < LetterboxdHttpClient.MaxRetries - 1)
            {
                var backoff = (attempt + 1) * 15000 + Random.Shared.Next(10000);
                _logger.LogWarning("Review post for {FilmSlug} got 403. Backing off {Delay}ms (attempt {Attempt}/3)",
                    filmSlug, backoff, attempt + 1);
                await Task.Delay(backoff).ConfigureAwait(false);
                continue;
            }

            _logger.LogInformation("Review response for {FilmSlug}: status={Status}, body={Body}",
                filmSlug, (int)res.StatusCode, LetterboxdHttpClient.Truncate(body, 500));

            if ((int)res.StatusCode < 200 || (int)res.StatusCode >= 300)
                throw new Exception($"Review post returned {(int)res.StatusCode} for {filmSlug}: {LetterboxdHttpClient.Truncate(body, 300)}");

            _logger.LogInformation("Posted review for {FilmSlug}", filmSlug);
            _auth.ResetReauthGuard();
            return;
        }

        throw new Exception($"Failed to post review for {filmSlug} after {LetterboxdHttpClient.MaxRetries} attempts");
    }

    /// <summary>
    /// Sets the member's film rating through the site's rate action. Unlike the official API,
    /// this endpoint takes a 0-10 integer (half-stars x 2) and the numeric film id.
    /// </summary>
    public async Task SetFilmRatingAsync(string filmSlug, string filmId, double rating)
    {
        var wireRating = ToRateEndpointScale(rating).ToString(CultureInfo.InvariantCulture);

        for (int attempt = 0; attempt < LetterboxdHttpClient.MaxRetries; attempt++)
        {
            await _http.RefreshCsrfAsync().ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"/s/film:{Uri.EscapeDataString(filmId)}/rate/");
            _http.SetApiHeaders(req, filmSlug);
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["rating"] = wireRating,
                ["__csrf"] = _http.Csrf
            });

            using var res = await _http.Http.SendAsync(req).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            _logger.LogInformation("SetFilmRating attempt {Attempt} for {Slug}: status={Status}, bodyLen={Len}",
                attempt + 1, filmSlug, (int)res.StatusCode, body.Length);

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (_auth.ShouldReauthenticate())
                {
                    _logger.LogWarning("Got 401 rating {Slug}, session expired. Re-authenticating and retrying", filmSlug);
                    await _auth.ForceReauthenticateAsync().ConfigureAwait(false);
                    continue;
                }
                throw new Exception($"Letterboxd returned 401 rating {filmSlug} after re-authentication. Session may be permanently invalid.");
            }

            if (res.StatusCode == HttpStatusCode.Forbidden && attempt < LetterboxdHttpClient.MaxRetries - 1)
            {
                var backoff = (attempt + 1) * 15000 + Random.Shared.Next(10000);
                _logger.LogWarning("Rating {Slug} got 403. Backing off {Delay}ms (attempt {Attempt}/3)",
                    filmSlug, backoff, attempt + 1);
                await Task.Delay(backoff).ConfigureAwait(false);
                continue;
            }

            if ((int)res.StatusCode < 200 || (int)res.StatusCode >= 300)
                throw new Exception($"Rating {filmSlug} returned {(int)res.StatusCode}: {LetterboxdHttpClient.Truncate(body, 300)}");

            // Only an explicit {"result": true} counts. A 200 can also be a sign-in or challenge
            // page, and recording that as pushed would suppress every retry of this value.
            var failure = SiteActionFailure(body);
            if (failure != null)
                throw new Exception($"Letterboxd did not confirm rating {rating} for {filmSlug}: {failure}");

            _logger.LogInformation("Rated {Slug} {Rating} stars", filmSlug, rating);
            _auth.ResetReauthGuard();
            return;
        }

        throw new Exception($"Failed to rate {filmSlug} after {LetterboxdHttpClient.MaxRetries} attempts");
    }

    /// <summary>Half-stars (0.5 to 5.0) to the rate action's 0-10 integer scale.</summary>
    internal static int ToRateEndpointScale(double rating)
        => (int)Math.Clamp(Math.Round(rating * 2, MidpointRounding.AwayFromZero), 1, 10);

    /// <summary>
    /// Null when a site action's body is JSON with <c>"result": true</c>; otherwise why it does
    /// not count as success (the messages of a <c>"result": false</c>, or a non-JSON body).
    /// </summary>
    internal static string? SiteActionFailure(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "empty response";
        if (!body.TrimStart().StartsWith('{'))
            return "non-JSON response (likely a sign-in or challenge page)";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.True)
                return null;

            if (doc.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                var texts = new List<string>();
                foreach (var m in messages.EnumerateArray())
                    texts.Add(m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : m.GetRawText());
                if (texts.Count > 0)
                    return string.Join("; ", texts);
            }

            return "result was not true";
        }
        catch (JsonException)
        {
            return "unparseable JSON response";
        }
    }

    private static Dictionary<string, object?> BuildDiaryPayload(
        string endpoint, string filmId, string? productionId,
        DateTime viewingDate, bool liked, bool rewatch, double? rating)
    {
        var payload = new Dictionary<string, object?>
        {
            ["diaryDetails"] = new Dictionary<string, object>
            {
                ["diaryDate"] = viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["rewatch"] = rewatch
            },
            ["tags"] = Array.Empty<string>(),
            ["like"] = liked
        };

        if (rating.HasValue)
            payload["rating"] = rating.Value;

        if (endpoint.Contains("production") && !string.IsNullOrEmpty(productionId))
            payload["productionId"] = productionId;
        else
            payload["filmId"] = filmId;

        return payload;
    }

    private enum MarkResult
    {
        Success,
        NeedsReauth,
        TransientError,
        AllEndpoints404
    }
}
