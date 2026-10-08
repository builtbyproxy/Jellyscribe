using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class LetterboxdApiClient : ILetterboxdService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string _cacheKey = string.Empty;
    private string _memberId = string.Empty;
    private string _accessToken = string.Empty;

    // Keyed by Helpers.TokenCacheKey (username + password hash): a caller that only knows the
    // username must never get, or refresh, another account's token.
    private static readonly ConcurrentDictionary<string, TokenInfo> TokenCache = new();

    // tmdbId -> film, shared across instances so later runs and other accounts skip the
    // request. Never shared with the scraper: FilmId here is the LID.
    // Kept for the process lifetime: it grows with the library at most, and a film Letterboxd
    // renames keeps its old slug until a restart.
    private static readonly ConcurrentDictionary<int, FilmResult> FilmCache = new();

    /// <summary>
    /// Test hook: forget cached lookups. With no ids it clears the whole cache; test classes that
    /// run in parallel with others pass the ids they use, so they never clear a cache another
    /// class is relying on.
    /// </summary>
    internal static void ResetFilmCacheForTesting(params int[] tmdbIds)
    {
        if (tmdbIds.Length == 0)
        {
            FilmCache.Clear();
            return;
        }

        foreach (var id in tmdbIds)
            FilmCache.TryRemove(id, out _);
    }

    // One process-wide client: a client per instance (one per sync, per account and per
    // dashboard request) opened a fresh connection pool and TLS handshake every time. The
    // default headers are the same for every account; the bearer token goes on each request.
    // The bounded connection lifetime keeps DNS changes visible.
    private static readonly HttpClient SharedHttp = WithDefaultHeaders(new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // Shared by every account: a cookie one response set must never ride on another's request.
        UseCookies = false,
    }));

    private readonly bool _ownsHttp;

    public LetterboxdApiClient(ILogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _ownsHttp = handler != null;
        _http = handler != null ? WithDefaultHeaders(new HttpClient(handler)) : SharedHttp;
    }

    internal HttpClient HttpForTesting => _http;

    private static HttpClient WithDefaultHeaders(HttpClient http)
    {
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LetterboxdSync/1.6");
        return http;
    }

    public async Task AuthenticateAsync(string username, string password, string? rawCookies = null)
    {
        _cacheKey = Helpers.TokenCacheKey(username, password);

        // Check token cache first
        if (TokenCache.TryGetValue(_cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
        {
            _accessToken = cached.AccessToken;
            _memberId = cached.MemberId;
            _logger.LogDebug("Reusing cached API token for {Username}", username);
            return;
        }

        // Try refresh if we have a refresh token
        if (cached != null && !string.IsNullOrEmpty(cached.RefreshToken))
        {
            try
            {
                await RefreshTokenAsync(cached.RefreshToken).ConfigureAwait(false);
                _logger.LogDebug("Refreshed API token for {Username}", username);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Token refresh failed for {Username}, doing full auth: {Message}", username, ex.Message);
            }
        }

        // Full password auth
        var body = $"grant_type=password&username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}";
        var response = await SendSignedAsync(HttpMethod.Post, "/auth/token", body, "application/x-www-form-urlencoded")
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new LetterboxdApiAuthException(response.StatusCode, $"Letterboxd API auth failed ({response.StatusCode}): {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        ParseTokenResponse(json);

        // Fetch member ID
        await FetchMemberIdAsync().ConfigureAwait(false);

        _logger.LogInformation("Authenticated with Letterboxd API as {Username}", username);
    }

    public async Task<FilmResult> LookupFilmByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        if (FilmCache.TryGetValue(tmdbId, out var cached))
            return cached;

        var response = await SendSignedAsync(HttpMethod.Get, "/films", queryParams: $"filmId=tmdb%3A{tmdbId}&perPage=1",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");

        if (items.GetArrayLength() == 0)
            throw new FilmNotFoundException(tmdbId, $"Film with TMDb ID {tmdbId} not found on Letterboxd");

        var film = items[0];
        var lid = film.GetProperty("id").GetString()!;
        var slug = ExtractSlugFromLink(film);

        var result = new FilmResult(slug, lid, null);
        FilmCache[tmdbId] = result;
        return result;
    }

    public async Task<DiaryInfo> GetDiaryInfoAsync(string filmIdOrSlug, string username, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        // filmIdOrSlug is the LID when coming from the API path
        var response = await SendSignedAsync(HttpMethod.Get, "/log-entries",
            queryParams: $"member={Uri.EscapeDataString(_memberId)}&film={Uri.EscapeDataString(filmIdOrSlug)}&perPage=1&sort=WhenAdded",
            authenticated: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        // A failed check is not "no entries": reading it that way logs the film again, a
        // duplicate whenever it was already on the diary. The caller records a retryable failure.
        if (!response.IsSuccessStatusCode)
            throw new DiaryCheckFailedException(
                $"Could not check the Letterboxd diary: returned {(int)response.StatusCode}",
                blocked: response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");

        if (items.GetArrayLength() == 0)
            return new DiaryInfo(null, false);

        var entry = items[0];
        DateTime? lastDate = null;
        if (entry.TryGetProperty("diaryDetails", out var details) &&
            details.TryGetProperty("diaryDate", out var dateStr))
        {
            if (DateTime.TryParse(dateStr.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                lastDate = parsed;
        }

        return new DiaryInfo(lastDate, true);
    }

    public async Task MarkAsWatchedAsync(string filmSlug, string filmId, DateTime? date, bool liked,
        string? productionId = null, bool rewatch = false, double? rating = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var viewingDate = date ?? DateTime.Now;
        var bodyObj = new Dictionary<string, object>
        {
            ["filmId"] = filmId,
            ["diaryDetails"] = new Dictionary<string, object>
            {
                ["diaryDate"] = viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["rewatch"] = rewatch
            },
            ["like"] = liked,
            ["tags"] = Array.Empty<string>()
        };

        if (rating.HasValue)
            bodyObj["rating"] = rating.Value;

        var body = JsonSerializer.Serialize(bodyObj);

        var response = await SendSignedAsync(HttpMethod.Post, "/log-entries", body, "application/json", authenticated: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearCachedToken();
            throw new Exception("Letterboxd API token expired. Will re-authenticate on next sync.");
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Failed to log film: {response.StatusCode} {errorBody}");
        }
    }

    public async Task PostReviewAsync(string filmSlug, string? reviewText, bool containsSpoilers = false,
        bool isRewatch = false, string? date = null, double? rating = null, int? tmdbId = null)
    {
        EnsureAuthenticated();

        // The Letterboxd API /log-entries endpoint requires an LID, not a slug.
        // Resolve the LID via TMDb ID (the only identifier SyncHistory stores that the API accepts).
        if (!tmdbId.HasValue || tmdbId.Value <= 0)
            throw new Exception($"Cannot post review for {filmSlug} via the official API without a TMDb ID.");

        var film = await LookupFilmByTmdbIdAsync(tmdbId.Value).ConfigureAwait(false);
        var filmId = film.FilmId;

        var bodyObj = new Dictionary<string, object>
        {
            ["filmId"] = filmId,
            ["diaryDetails"] = new Dictionary<string, object>
            {
                ["diaryDate"] = date ?? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["rewatch"] = isRewatch
            },
            ["like"] = false,
            ["tags"] = Array.Empty<string>()
        };

        if (!string.IsNullOrWhiteSpace(reviewText))
        {
            bodyObj["review"] = new Dictionary<string, object>
            {
                ["text"] = reviewText,
                ["containsSpoilers"] = containsSpoilers
            };
        }

        if (rating.HasValue)
            bodyObj["rating"] = rating.Value;

        var body = JsonSerializer.Serialize(bodyObj);

        var response = await SendSignedAsync(HttpMethod.Post, "/log-entries", body, "application/json", authenticated: true)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
        {
            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Failed to post review: {response.StatusCode} {errorBody}");
        }
    }

    /// <summary>
    /// Finds the member's log entry for the film on <paramref name="diaryDate"/> and updates it
    /// with <c>PATCH /log-entry/{id}</c> (a LogEntryUpdateRequest carrying only <c>review</c> and,
    /// when given, <c>rating</c>), so the entry keeps its date, like and tags. An entry that
    /// already has a review is never overwritten.
    /// </summary>
    public async Task<ReviewAttachResult> AddReviewToDiaryEntryAsync(int tmdbId, DateTime diaryDate, string reviewText,
        bool containsSpoilers, double? rating)
    {
        EnsureAuthenticated();

        var film = await LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
        var onDate = (await GetMemberLogEntriesAsync(film.FilmId).ConfigureAwait(false))
            .Where(e => e.DiaryDate?.Date == diaryDate.Date)
            .ToList();
        if (onDate.Count == 0)
            return ReviewAttachResult.NoEntry;

        var target = onDate.FirstOrDefault(e => !e.HasReview);
        if (target == null)
            return ReviewAttachResult.AlreadyReviewed;

        var bodyObj = new Dictionary<string, object>
        {
            ["review"] = new Dictionary<string, object>
            {
                ["text"] = reviewText,
                ["containsSpoilers"] = containsSpoilers
            }
        };
        if (rating.HasValue)
            bodyObj["rating"] = rating.Value;

        var response = await SendSignedAsync(HttpMethod.Patch, $"/log-entry/{Uri.EscapeDataString(target.Id)}",
            JsonSerializer.Serialize(bodyObj), "application/json", authenticated: true).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearCachedToken();
            throw new Exception("Letterboxd API token expired. Will re-authenticate on next sync.");
        }

        // A failed reply can echo the review in whatever escaping Letterboxd chose, so the error
        // carries only the status.
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Failed to add the review to the diary entry: {response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // A LogEntryUpdateResponse reports a refused field (a rating off the scale, say) as an
        // Error message in a 200 reply, the same shape as a film relationship update.
        var error = ExtractRelationshipUpdateError(json);
        if (error != null)
            throw new Exception($"Letterboxd refused the review update: {LetterboxdHttpClient.Truncate(LetterboxdDiary.WithoutReview(error, reviewText), 300)}");

        return ReviewAttachResult.Attached;
    }

    /// <summary>One of the member's log entries for a film, as <c>GET /log-entries</c> lists it.</summary>
    internal sealed record LogEntrySummary(string Id, DateTime? DiaryDate, bool HasReview, string? ReviewText, double? Rating);

    /// <summary>
    /// The member's log entries for one film (<c>GET /log-entries?member=&amp;film=</c>), every
    /// page. A failed or incomplete read throws: reading it as "no entries" would log the film again.
    /// </summary>
    internal async Task<List<LogEntrySummary>> GetMemberLogEntriesAsync(string filmLid)
    {
        EnsureAuthenticated();

        var entries = new List<LogEntrySummary>();
        await ReadAllPagesAsync("/log-entries",
            $"member={Uri.EscapeDataString(_memberId)}&film={Uri.EscapeDataString(filmLid)}&perPage=100&sort=WhenAdded",
            "diary entries for this film",
            item =>
            {
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(idEl.GetString()))
                    return;

                DateTime? date = null;
                if (item.TryGetProperty("diaryDetails", out var details) && details.ValueKind == JsonValueKind.Object
                    && details.TryGetProperty("diaryDate", out var dateEl) && dateEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(dateEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    date = parsed;

                // A Review carries the member's text as LBML (as typed) and as HTML (text).
                string? reviewText = null;
                var hasReview = item.TryGetProperty("review", out var review) && review.ValueKind == JsonValueKind.Object;
                if (hasReview)
                {
                    reviewText = review.TryGetProperty("lbml", out var lbml) && lbml.ValueKind == JsonValueKind.String ? lbml.GetString()
                        : review.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()
                        : null;
                }

                double? rating = item.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null;
                entries.Add(new LogEntrySummary(idEl.GetString()!, date, hasReview, reviewText, rating));
            }, CancellationToken.None).ConfigureAwait(false);

        return entries;
    }

    public async Task SetFilmRatingAsync(string filmSlug, string filmId, double rating, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["rating"] = rating });
        var response = await SendSignedAsync(HttpMethod.Patch, $"/film/{Uri.EscapeDataString(filmId)}/me", body, "application/json", authenticated: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            ClearCachedToken();
            throw new Exception("Letterboxd API token expired. Will re-authenticate on next sync.");
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Failed to rate {filmSlug}: {response.StatusCode} {LetterboxdHttpClient.Truncate(json, 300)}");

        // Business-rule rejections (e.g. InvalidRatingValue) come back as HTTP 200 with an
        // Error message, and the rating is left unchanged.
        var error = ExtractRelationshipUpdateError(json);
        if (error != null)
            throw new Exception($"Letterboxd rejected rating {rating} for {filmSlug}: {error}");
    }

    /// <summary>
    /// First Error-type entry in a FilmRelationshipUpdateResponse's messages, as "code: title",
    /// or null when the update was accepted.
    /// </summary>
    internal static string? ExtractRelationshipUpdateError(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("type", out var type) && type.GetString() == "Error")
                {
                    var code = message.TryGetProperty("code", out var c) ? c.GetString() : null;
                    var title = message.TryGetProperty("title", out var t) ? t.GetString() : null;
                    return $"{code ?? "Error"}: {title}";
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public async Task<List<int>> GetWatchlistTmdbIdsAsync(string username, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tmdbIds = new List<int>();
        var seen = new HashSet<int>();

        await ReadAllPagesAsync($"/member/{Uri.EscapeDataString(_memberId)}/watchlist", "perPage=100", "watchlist",
            item =>
            {
                var tmdbId = ExtractTmdbId(item);
                if (tmdbId.HasValue && seen.Add(tmdbId.Value))
                    tmdbIds.Add(tmdbId.Value);
            }, cancellationToken).ConfigureAwait(false);

        return tmdbIds;
    }

    public async Task<List<int>> GetDiaryTmdbIdsAsync(string username)
    {
        var entries = await GetDiaryFilmEntriesAsync(username).ConfigureAwait(false);
        return entries.Select(e => e.TmdbId).ToList();
    }

    /// <summary>
    /// Returns every film the member has marked Watched on Letterboxd, including
    /// ones rated without a diary entry. Each entry includes the member's personal
    /// rating when set. Backed by /films?memberRelationship=Watched, which is a
    /// superset of /log-entries (the diary endpoint), so films you rated without
    /// logging a watch are also included.
    /// </summary>
    public async Task<List<DiaryFilmEntry>> GetDiaryFilmEntriesAsync(string username, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var entries = new List<DiaryFilmEntry>();
        var seen = new HashSet<int>();

        await ReadAllPagesAsync("/films",
            $"perPage=100&member={Uri.EscapeDataString(_memberId)}&memberRelationship=Watched&include=MemberRelationship",
            "watched films",
            item =>
            {
                var tmdbId = ExtractTmdbIdFromLinks(item);
                if (!tmdbId.HasValue || !seen.Add(tmdbId.Value)) return;

                double? rating = ExtractMemberRating(item);
                entries.Add(new DiaryFilmEntry(tmdbId.Value, rating));
            }, cancellationToken).ConfigureAwait(false);

        return entries;
    }

    // 200 pages of 100 is 20,000 films, far beyond any real watchlist or diary; the cap only
    // exists so a misbehaving API can never loop forever.
    private const int MaxPages = 200;

    /// <summary>
    /// Reads every page of a cursored Letterboxd list. Letterboxd paginates with an opaque
    /// cursor: each response's <c>next</c> value is sent back unchanged as the <c>cursor</c>
    /// request parameter. There is no offset parameter; an earlier version sent
    /// <c>start=N</c>, which the API ignores, so every "next page" request returned page one
    /// again and lists were silently cut at the first 100 items (issues #109 and #125).
    /// The list ends only when <c>next</c> is absent or null. Anything else that stops the
    /// read early (an empty or malformed page, an unrecognised <c>next</c>, a page with nothing
    /// new, a repeated cursor, or the page cap) throws instead of returning a partial list:
    /// callers treat the result as the complete list (watchlist sync reconciles the playlist
    /// to it), so a short list would quietly remove films, while a throw is logged by the
    /// caller and leaves everything as it was.
    /// </summary>
    private async Task ReadAllPagesAsync(string path, string baseQuery, string what, Action<JsonElement> onItem,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var seenItemIds = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < MaxPages; page++)
        {
            var qp = cursor == null ? baseQuery : $"{baseQuery}&cursor={Uri.EscapeDataString(cursor)}";
            var response = await SendSignedAsync(HttpMethod.Get, path, queryParams: qp, authenticated: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw IncompleteRead(what, seenItemIds.Count, "a page with no list of items");

            var hasNext = root.TryGetProperty("next", out var nextEl) && nextEl.ValueKind != JsonValueKind.Null;
            if (hasNext && (nextEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nextEl.GetString())))
                throw IncompleteRead(what, seenItemIds.Count, $"a next-page marker it could not read ({nextEl.ValueKind})");

            if (items.GetArrayLength() == 0)
            {
                if (hasNext)
                    throw IncompleteRead(what, seenItemIds.Count, "an empty page in the middle of the list");
                return;
            }

            var newItems = 0;
            foreach (var item in items.EnumerateArray())
            {
                // Letterboxd's own item id, present on every item whether or not it carries a
                // TMDb link, so it reliably tells a fresh page from a repeated one.
                var itemId = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (itemId == null || seenItemIds.Add(itemId))
                    newItems++;
                onItem(item);
            }

            if (!hasNext)
                return;

            var next = nextEl.GetString()!;
            if (newItems == 0)
                throw IncompleteRead(what, seenItemIds.Count, "the same page again instead of the next one");
            if (!seenCursors.Add(next))
                throw IncompleteRead(what, seenItemIds.Count, "a next-page marker it had already followed");

            cursor = next;
        }

        throw IncompleteRead(what, seenItemIds.Count, $"more than {MaxPages} pages");
    }

    private static InvalidOperationException IncompleteRead(string what, int count, string reason)
        => new($"Could not read the whole Letterboxd {what}: after {count} items Letterboxd returned {reason}. Nothing was changed this run.");

    /// <summary>
    /// Pull TMDb ID from a FilmSummary's `links` array.
    /// FilmSummary uses `links: [{type:"tmdb", id:"123"}, ...]` rather than a
    /// nested `film` object with provider IDs (which is what /log-entries returns).
    /// Skips TV-show TMDb links: TMDb has separate ID namespaces for movies
    /// and TV, so e.g. tv/198102 (Hijack, BBC drama) collides with movie/198102
    /// (Cutie Honey Flash). The watchlist/diary syncs target movies only.
    /// </summary>
    internal static int? ExtractTmdbIdFromLinks(JsonElement film)
    {
        if (!film.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var link in links.EnumerateArray())
        {
            if (!link.TryGetProperty("type", out var type)) continue;
            if (!string.Equals(type.GetString(), "tmdb", StringComparison.OrdinalIgnoreCase)) continue;

            if (link.TryGetProperty("url", out var urlEl) &&
                urlEl.GetString() is string url &&
                url.Contains("/tv/", StringComparison.Ordinal))
                continue;

            if (!link.TryGetProperty("id", out var idEl)) continue;
            if (int.TryParse(idEl.GetString(), out var id)) return id;
        }
        return null;
    }

    /// <summary>
    /// Pull the member's personal rating from a FilmSummary returned with
    /// `include=MemberRelationship`. Lives at relationships[0].relationship.rating.
    /// </summary>
    internal static double? ExtractMemberRating(JsonElement film)
    {
        if (!film.TryGetProperty("relationships", out var rels) ||
            rels.ValueKind != JsonValueKind.Array || rels.GetArrayLength() == 0)
            return null;

        var first = rels[0];
        if (!first.TryGetProperty("relationship", out var rel)) return null;
        if (!rel.TryGetProperty("rating", out var ratingEl)) return null;
        if (ratingEl.ValueKind != JsonValueKind.Number) return null;

        return ratingEl.GetDouble();
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    // --- Test-only helpers (InternalsVisibleTo LetterboxdSync.Tests) ---

    /// <summary>
    /// Deletes every diary log entry the authenticated user has for the given film LID.
    /// Used by the integration test suite to clean up after a write so the test account
    /// stays predictable across runs. Best-effort: per-entry failures are swallowed and
    /// logged but the loop continues. Not on ILetterboxdService because production sync
    /// paths never want to delete user data.
    /// </summary>
    internal async Task DeleteAllLogEntriesForFilmAsync(string filmLid)
    {
        EnsureAuthenticated();

        var listResponse = await SendSignedAsync(HttpMethod.Get, "/log-entries",
            queryParams: $"member={Uri.EscapeDataString(_memberId)}&film={Uri.EscapeDataString(filmLid)}&perPage=50",
            authenticated: true).ConfigureAwait(false);

        if (!listResponse.IsSuccessStatusCode) return;

        var json = await listResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return;

        // Throttle deletes so an account with accumulated residue (e.g. from a prior
        // run that died mid-cleanup) doesn't fire dozens of DELETEs back-to-back and
        // trip Letterboxd's rate limit, which would leave entries undeleted and pollute
        // future runs.
        var first = true;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl)) continue;
            var entryId = idEl.GetString();
            if (string.IsNullOrEmpty(entryId)) continue;

            if (!first)
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            first = false;

            try
            {
                // Letterboxd splits the resource path: /log-entries (collection, GET/POST)
                // vs /log-entry/{id} (individual, GET/PATCH/DELETE). Plural-DELETE returns 404.
                var del = await SendSignedAsync(HttpMethod.Delete,
                    $"/log-entry/{Uri.EscapeDataString(entryId)}", authenticated: true)
                    .ConfigureAwait(false);
                if (!del.IsSuccessStatusCode)
                    _logger.LogWarning("Cleanup: DELETE /log-entry/{Id} returned {Status}", entryId, del.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Cleanup: DELETE /log-entry/{Id} threw: {Message}", entryId, ex.Message);
            }
        }
    }

    // --- Private helpers ---

    /// <summary>
    /// Sends a signed request. A 429 is retried once after its Retry-After (at most
    /// <see cref="RetryAfterLimit.Max"/>; longer, the 429 is returned and the caller fails the
    /// item). The retry is signed afresh, since the signature covers the nonce and timestamp.
    /// <paramref name="cancellationToken"/> cancels that wait and a read in flight, never a write
    /// already sent, which may have landed.
    /// </summary>
    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path,
        string? body = null, string? contentType = null, string? queryParams = null, bool authenticated = false,
        CancellationToken cancellationToken = default)
    {
        var sendToken = method == HttpMethod.Get ? cancellationToken : CancellationToken.None;
        var response = await SendSignedOnceAsync(method, path, body, contentType, queryParams, authenticated, sendToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
            return response;

        var wait = RetryAfterLimit.Wait(response.Headers.RetryAfter);
        if (wait == null)
        {
            _logger.LogWarning("Letterboxd API rate limited {Path} for longer than {Max}s, not retrying",
                path, RetryAfterLimit.Max.TotalSeconds);
            return response;
        }

        _logger.LogWarning("Letterboxd API rate limited, waiting {Seconds}s", wait.Value.TotalSeconds);
        response.Dispose();
        await Task.Delay(wait.Value, cancellationToken).ConfigureAwait(false);
        return await SendSignedOnceAsync(method, path, body, contentType, queryParams, authenticated, sendToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendSignedOnceAsync(HttpMethod method, string path,
        string? body, string? contentType, string? queryParams, bool authenticated, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var url = $"{LetterboxdApiConstants.BaseUrl}{path}?apikey={LetterboxdApiConstants.ApiKey}&nonce={nonce}&timestamp={timestamp}";
        if (!string.IsNullOrEmpty(queryParams))
            url = $"{LetterboxdApiConstants.BaseUrl}{path}?{queryParams}&apikey={LetterboxdApiConstants.ApiKey}&nonce={nonce}&timestamp={timestamp}";

        var bodyStr = body ?? string.Empty;
        var sigInput = $"{method.Method}\0{url}\0{bodyStr}";
        var signature = ComputeHmacSha256(LetterboxdApiConstants.ApiSecret, sigInput);

        url += $"&signature={signature}";

        using var request = new HttpRequestMessage(method, url);

        if (authenticated && !string.IsNullOrEmpty(_accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        if (body != null && contentType != null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeHmacSha256(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }

    private void ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

        TokenCache[_cacheKey] = new TokenInfo(
            _accessToken,
            refreshToken,
            DateTime.UtcNow.AddSeconds(expiresIn),
            _memberId);
    }

    private async Task RefreshTokenAsync(string refreshToken)
    {
        var body = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}";
        var response = await SendSignedAsync(HttpMethod.Post, "/auth/token", body, "application/x-www-form-urlencoded")
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        ParseTokenResponse(json);
        await FetchMemberIdAsync().ConfigureAwait(false);
    }

    private async Task FetchMemberIdAsync()
    {
        var response = await SendSignedAsync(HttpMethod.Get, "/me", authenticated: true).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        _memberId = doc.RootElement.GetProperty("member").GetProperty("id").GetString()!;

        // Update cache with member ID
        if (TokenCache.TryGetValue(_cacheKey, out var cached))
        {
            TokenCache[_cacheKey] = cached with { MemberId = _memberId };
        }
    }

    private void ClearCachedToken()
    {
        TokenCache.TryRemove(_cacheKey, out _);
        _accessToken = string.Empty;
        _logger.LogWarning("API token expired, cleared cache");
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");
    }

    private static string ExtractSlugFromLink(JsonElement film)
    {
        if (film.TryGetProperty("links", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("type", out var type) && type.GetString() == "letterboxd" &&
                    link.TryGetProperty("url", out var url))
                {
                    var urlStr = url.GetString() ?? string.Empty;
                    // https://letterboxd.com/film/fight-club/ -> fight-club
                    var parts = urlStr.TrimEnd('/').Split('/');
                    return parts.Length > 0 ? parts[^1] : string.Empty;
                }
            }
        }

        // Fallback: extract from top-level link property
        if (film.TryGetProperty("link", out var directLink))
        {
            var urlStr = directLink.GetString() ?? string.Empty;
            var parts = urlStr.TrimEnd('/').Split('/');
            return parts.Length > 0 ? parts[^1] : string.Empty;
        }

        return string.Empty;
    }

    private static int? ExtractTmdbId(JsonElement film) => ExtractTmdbIdFromLinks(film);

    internal record TokenInfo(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, string MemberId);
}
