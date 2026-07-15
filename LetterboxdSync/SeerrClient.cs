using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Lightweight Seerr client for fetching the Jellyfin-user-to-Seerr-user
/// mapping, creating per-user movie requests, and mirroring a user's watchlist.
/// </summary>
public class SeerrClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _baseUrl;

    private Dictionary<string, int>? _jellyfinIdToJellyseerrId;

    public SeerrClient(string baseUrl, string apiKey, ILogger logger, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
    }

    /// <summary>Returns true if the client looks usable (URL + key set).</summary>
    public static bool IsConfigured(string? url, string? apiKey)
        => !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(apiKey);

    /// <summary>
    /// Looks up the Seerr user ID corresponding to a Jellyfin user ID (32-char hex).
    /// Returns null if no matching user is found.
    /// </summary>
    public async Task<int?> GetJellyseerrUserIdAsync(string jellyfinUserId)
    {
        if (_jellyfinIdToJellyseerrId == null)
            await LoadUserMapAsync().ConfigureAwait(false);

        var normalized = jellyfinUserId.Replace("-", string.Empty).ToLowerInvariant();
        return _jellyfinIdToJellyseerrId!.TryGetValue(normalized, out var id) ? id : null;
    }

    private async Task LoadUserMapAsync()
    {
        _jellyfinIdToJellyseerrId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var take = 100;
        var skip = 0;
        while (true)
        {
            var url = $"{_baseUrl}/api/v1/user?take={take}&skip={skip}";
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("results");

            var count = results.GetArrayLength();
            if (count == 0) break;

            foreach (var user in results.EnumerateArray())
            {
                if (!user.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                if (!user.TryGetProperty("jellyfinUserId", out var jfIdEl) || jfIdEl.ValueKind != JsonValueKind.String)
                    continue;
                var jfId = jfIdEl.GetString();
                if (string.IsNullOrWhiteSpace(jfId)) continue;
                var key = jfId.Replace("-", string.Empty).ToLowerInvariant();
                _jellyfinIdToJellyseerrId[key] = idEl.GetInt32();
            }

            skip += count;
            if (count < take) break;
        }

        _logger.LogInformation("Seerr user map loaded: {Count} users with linked Jellyfin IDs",
            _jellyfinIdToJellyseerrId.Count);
    }

    /// <summary>
    /// Looks up the current MediaStatus for a TMDb movie in Seerr.
    /// Returns null when Seerr has no record of the title (so it's safe to request),
    /// or when the call fails (caller should fall through to attempting the request and
    /// let Seerr surface the real error). Status values follow Seerr's enum:
    /// 1=UNKNOWN, 2=PENDING, 3=PROCESSING, 4=PARTIALLY_AVAILABLE, 5=AVAILABLE,
    /// 6=BLOCKLISTED, 7=DELETED.
    /// </summary>
    public async Task<int?> GetMovieMediaStatusAsync(int tmdbId)
        => (await GetMovieInfoAsync(tmdbId).ConfigureAwait(false)).Status;

    /// <summary>
    /// Single movie lookup returning both the Seerr MediaStatus and the set of Seerr
    /// user IDs that already have a request for the title. Status is null when Seerr has no
    /// record of the title (safe to request) or the call fails; the requester set is empty in
    /// those cases. Status enum: 1=UNKNOWN, 2=PENDING, 3=PROCESSING, 4=PARTIALLY_AVAILABLE,
    /// 5=AVAILABLE, 6=BLOCKLISTED, 7=DELETED.
    /// </summary>
    private async Task<(int? Status, HashSet<int> RequesterUserIds)> GetMovieInfoAsync(int tmdbId)
    {
        var requesters = new HashSet<int>();
        var url = $"{_baseUrl}/api/v1/movie/{tmdbId}";
        try
        {
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Seerr movie lookup non-success for TMDb {TmdbId}: {Status}",
                    tmdbId, (int)response.StatusCode);
                return (null, requesters);
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mediaInfo", out var mediaInfo) ||
                mediaInfo.ValueKind != JsonValueKind.Object)
                return (null, requesters);

            int? status = null;
            if (mediaInfo.TryGetProperty("status", out var statusEl) &&
                statusEl.ValueKind == JsonValueKind.Number)
                status = statusEl.GetInt32();

            // mediaInfo.requests[].requestedBy.id, who already has a request for this title.
            if (mediaInfo.TryGetProperty("requests", out var requests) &&
                requests.ValueKind == JsonValueKind.Array)
            {
                foreach (var req in requests.EnumerateArray())
                {
                    if (req.TryGetProperty("requestedBy", out var by) &&
                        by.ValueKind == JsonValueKind.Object &&
                        by.TryGetProperty("id", out var reqUserId) &&
                        reqUserId.ValueKind == JsonValueKind.Number)
                        requesters.Add(reqUserId.GetInt32());
                }
            }

            return (status, requesters);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Seerr movie lookup errored for TMDb {TmdbId}: {Message}",
                tmdbId, ex.Message);
            return (null, requesters);
        }
    }

    /// <summary>
    /// Outcome of a request attempt. <see cref="AlreadyExists"/> means Seerr already
    /// had the title in some non-UNKNOWN state (pending, processing, available, blocklisted)
    /// and we did NOT POST a new request; it should not be counted as a fresh request.
    /// </summary>
    public enum RequestResult
    {
        Requested,
        AlreadyExists,
        Failed
    }

    /// <summary>
    /// Creates a movie request in Seerr for the given TMDb ID, attributed to the given Seerr user.
    /// <para>
    /// Default (<paramref name="backfillAvailable"/> false): pre-checks media status and skips the POST when
    /// Seerr already has a record of the title (pending / processing / available), so re-runs don't pile
    /// up duplicates for films the library already covers.
    /// </para>
    /// <para>
    /// Backfill (<paramref name="backfillAvailable"/> true): skips only when the title is blocklisted or THIS
    /// user already has a request for it. Available titles with no request from this user are still requested,
    /// Seerr 3.x accepts a request for available media as long as no active request exists, which restores
    /// an attributed requester trail for films that entered the library outside Seerr. If another user
    /// holds the single active request, Seerr returns 409 and we report <see cref="RequestResult.AlreadyExists"/>.
    /// </para>
    /// </summary>
    public async Task<RequestResult> RequestMovieAsync(int tmdbId, int jellyseerrUserId, bool backfillAvailable = false)
    {
        var (status, requesters) = await GetMovieInfoAsync(tmdbId).ConfigureAwait(false);

        // Never request blocklisted media (6), regardless of mode.
        if (status == 6)
        {
            _logger.LogDebug("Skipping Seerr request for TMDb {TmdbId}: blocklisted", tmdbId);
            return RequestResult.AlreadyExists;
        }

        if (backfillAvailable)
        {
            // Only this user's own existing request blocks a backfill; an available-but-unrequested
            // title is exactly what we want to attribute.
            if (requesters.Contains(jellyseerrUserId))
            {
                _logger.LogDebug("Skipping Seerr backfill for TMDb {TmdbId}: user {UserId} already has a request",
                    tmdbId, jellyseerrUserId);
                return RequestResult.AlreadyExists;
            }
        }
        else
        {
            // Re-request DELETED (7); skip everything else above UNKNOWN (1).
            if (status.HasValue && status.Value > 1 && status.Value != 7)
            {
                _logger.LogDebug("Skipping Seerr request for TMDb {TmdbId}: already has status {Status}",
                    tmdbId, status.Value);
                return RequestResult.AlreadyExists;
            }
        }

        var url = $"{_baseUrl}/api/v1/request";
        var body = $"{{\"mediaType\":\"movie\",\"mediaId\":{tmdbId},\"userId\":{jellyseerrUserId}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return RequestResult.Requested;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        // Belt-and-braces: Seerr returns 409 when an active request already exists; treat as a no-op.
        if (IsAlreadyExistsResponse(response.StatusCode, responseBody))
        {
            _logger.LogDebug("Seerr already has request for TMDb {TmdbId} (user {UserId}): {Body}",
                tmdbId, jellyseerrUserId, Truncate(responseBody, 200));
            return RequestResult.AlreadyExists;
        }

        _logger.LogWarning("Seerr request failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return RequestResult.Failed;
    }

    /// <summary>
    /// Requests a TV series on Seerr (the TV counterpart to <see cref="RequestMovieAsync"/>).
    /// Requests the given season numbers, or all seasons when none are specified. 409 /
    /// already-exists responses are treated as a no-op, which is also how a backfill request
    /// for an already-available show resolves (so <paramref name="backfillAvailable"/> callers
    /// get an attributed request when possible and a harmless no-op otherwise).
    /// </summary>
    public async Task<RequestResult> RequestSeriesAsync(int tmdbId, int jellyseerrUserId, IReadOnlyList<int> seasonNumbers, bool backfillAvailable = false)
    {
        _ = backfillAvailable; // Behaviour is driven by the caller's candidate set; Seerr's 409 handling covers the available case.

        var url = $"{_baseUrl}/api/v1/request";
        var seasonsJson = seasonNumbers.Count > 0 ? "[" + string.Join(",", seasonNumbers) + "]" : "\"all\"";
        var body = $"{{\"mediaType\":\"tv\",\"mediaId\":{tmdbId},\"userId\":{jellyseerrUserId},\"seasons\":{seasonsJson}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync(url, content).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var seasonsLabel = seasonNumbers.Count > 0 ? string.Join(",", seasonNumbers) : "all";

        if (response.IsSuccessStatusCode)
        {
            // A 2xx isn't always a real grab: Jellyseerr also returns success when the requested
            // seasons are already available (nothing to fetch). Classify from the body so the
            // caller's tally reflects reality instead of counting no-ops as "new". Full response
            // logged so mismatches between Jellyfin's and Jellyseerr's view are diagnosable.
            var available = AllRequestedSeasonsAvailable(responseBody, seasonNumbers);
            _logger.LogInformation(
                "Seerr TV request TMDb {TmdbId} S[{Seasons}] → {Outcome} (HTTP {Status}): {Body}",
                tmdbId, seasonsLabel, available ? "already available (no-op)" : "requested",
                (int)response.StatusCode, Truncate(responseBody, 400));
            return available ? RequestResult.AlreadyExists : RequestResult.Requested;
        }

        if (IsAlreadyExistsResponse(response.StatusCode, responseBody))
        {
            _logger.LogInformation("Seerr TV request TMDb {TmdbId} S[{Seasons}] → already exists (HTTP {Status})",
                tmdbId, seasonsLabel, (int)response.StatusCode);
            return RequestResult.AlreadyExists;
        }

        _logger.LogWarning("Seerr TV request failed for TMDb {TmdbId} S[{Seasons}] (user {UserId}): {Status} {Body}",
            tmdbId, seasonsLabel, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return RequestResult.Failed;
    }

    /// <summary>
    /// Best-effort read of a POST /request 2xx body: true when every requested season is already
    /// AVAILABLE (Jellyseerr status 5) — i.e. the "request" was a no-op with nothing to fetch.
    /// Prefers per-season detail on <c>media.seasons</c>; falls back to the media-level status when
    /// the response carries none. Returns false (treat as a real request) on any parse failure so
    /// a genuine grab is never silently hidden.
    /// </summary>
    private static bool AllRequestedSeasonsAvailable(string body, IReadOnlyList<int> requestedSeasons)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("media", out var media)
                && media.TryGetProperty("seasons", out var seasons)
                && seasons.ValueKind == JsonValueKind.Array)
            {
                var statusByNumber = new Dictionary<int, int>();
                foreach (var s in seasons.EnumerateArray())
                {
                    if (s.TryGetProperty("seasonNumber", out var sn) && sn.ValueKind == JsonValueKind.Number &&
                        s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number)
                        statusByNumber[sn.GetInt32()] = st.GetInt32();
                }

                IEnumerable<int> relevant = requestedSeasons.Count > 0 ? requestedSeasons : statusByNumber.Keys;
                var any = false;
                foreach (var n in relevant)
                {
                    any = true;
                    if (!statusByNumber.TryGetValue(n, out var st) || st != 5) return false; // 5 = AVAILABLE
                }
                return any;
            }

            if (root.TryGetProperty("media", out var media2)
                && media2.TryGetProperty("status", out var ms)
                && ms.ValueKind == JsonValueKind.Number)
                return ms.GetInt32() == 5;

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the set of TMDb IDs of <paramref name="mediaType"/> currently on the given Seerr
    /// user's watchlist. Pages through results until exhausted. Returns an empty set on failure
    /// (caller should treat that as "unknown" and avoid destructive removals).
    /// </summary>
    public async Task<HashSet<int>> GetUserWatchlistTmdbIdsAsync(int jellyseerrUserId, string mediaType = "movie")
    {
        var ids = new HashSet<int>();
        var page = 1;
        var totalPages = 1; // Updated from the first response.

        while (page <= totalPages)
        {
            // Seerr's user/:id/watchlist endpoint paginates with `page=N`. It rejects
            // unknown params with a 400, so do NOT pass take/skip here.
            var url = $"{_baseUrl}/api/v1/user/{jellyseerrUserId}/watchlist?page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-API-User",
                jellyseerrUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning("Seerr watchlist fetch failed for user {UserId}: {Status} {Body}",
                    jellyseerrUserId, (int)response.StatusCode, Truncate(errBody, 300));
                return ids;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number)
                totalPages = tp.GetInt32();

            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                break;

            var count = results.GetArrayLength();
            if (count == 0) break;

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("mediaType", out var mt) && mt.ValueKind == JsonValueKind.String)
                {
                    if (!string.Equals(mt.GetString(), mediaType, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // tmdbId can come back as either a number (DB shape) or a string (Plex shape).
                if (!item.TryGetProperty("tmdbId", out var idEl)) continue;
                int parsed;
                if (idEl.ValueKind == JsonValueKind.Number)
                {
                    parsed = idEl.GetInt32();
                }
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var s))
                {
                    parsed = s;
                }
                else
                {
                    continue;
                }

                ids.Add(parsed);
            }

            page++;
        }

        return ids;
    }

    /// <summary>
    /// Adds a TMDb title to the given Seerr user's watchlist. Acts as that user via
    /// the X-API-User header so the entry is owned by them, not the API key's default admin.
    /// Returns true on success or "already there"; false on transient error.
    /// </summary>
    public async Task<bool> AddToWatchlistAsync(int tmdbId, int jellyseerrUserId, string mediaType = "movie")
    {
        var url = $"{_baseUrl}/api/v1/watchlist";
        var body = $"{{\"tmdbId\":{tmdbId},\"mediaType\":\"{mediaType}\"}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("X-API-User", jellyseerrUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return true;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)response.StatusCode == 409 ||
            responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            responseBody.Contains("already on", StringComparison.OrdinalIgnoreCase))
            return true;

        _logger.LogWarning("Seerr watchlist add failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return false;
    }

    /// <summary>
    /// Removes a TMDb title from the given Seerr user's watchlist. Acts as that user
    /// via the X-API-User header. 404 is treated as success (the entry was already gone).
    /// </summary>
    public async Task<bool> RemoveFromWatchlistAsync(int tmdbId, int jellyseerrUserId, string mediaType = "movie")
    {
        var url = $"{_baseUrl}/api/v1/watchlist/{tmdbId}?mediaType={mediaType}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.TryAddWithoutValidation("X-API-User", jellyseerrUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return true;

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        _logger.LogWarning("Seerr watchlist remove failed for TMDb {TmdbId} (user {UserId}): {Status} {Body}",
            tmdbId, jellyseerrUserId, (int)response.StatusCode, Truncate(responseBody, 200));
        return false;
    }

    /// <summary>
    /// Shared by RequestMovieAsync and RequestSeriesAsync: Jellyseerr signals "you already have
    /// this" a few different ways (an HTTP 409, or one of several phrases in the error body
    /// depending on version/media type). Kept in one place so a new phrase Jellyseerr starts
    /// using only needs updating once, not once per media-type method.
    /// </summary>
    private static bool IsAlreadyExistsResponse(HttpStatusCode status, string responseBody)
        => (int)status == 409 ||
           responseBody.Contains("REQUEST_EXISTS", StringComparison.OrdinalIgnoreCase) ||
           responseBody.Contains("already requested", StringComparison.OrdinalIgnoreCase) ||
           responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
           responseBody.Contains("already available", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) + "..." : s;

    public void Dispose() => _http.Dispose();
}
