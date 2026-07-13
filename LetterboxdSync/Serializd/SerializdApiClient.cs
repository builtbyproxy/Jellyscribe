using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Client for the Serializd private API. Auth is email/password → bearer token
/// (no Cloudflare on this host, so none of the Letterboxd client's cookie/backoff
/// machinery is needed).
///
/// Write payloads MUST be snake_case (<c>show_id</c>, <c>season_ids</c>,
/// <c>episode_numbers</c>): the API returns HTTP 500 for camelCase bodies. Every
/// request body is built from an explicit key dictionary so the casing can't drift.
/// </summary>
public class SerializdApiClient : ISerializdService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _token = string.Empty;
    private string _username = string.Empty;

    /// <summary>Serializd username returned by the most recent fresh login (null when a cached token was reused).</summary>
    public string? Username { get; private set; }

    // Token reuse across sync events within a process. Keyed by email. Serializd
    // does not advertise a token TTL, so we trust a cached token until a 401, then
    // clear + re-login once (see SendAsync).
    private static readonly ConcurrentDictionary<string, string> TokenCache = new();

    // showTmdbId -> (seasonNumber -> serializd seasonId). Season structure is
    // stable enough to cache; a miss on a season number triggers one forced refetch
    // (a newly-aired season) before giving up (see ResolveSeasonIdAsync).
    private static readonly ConcurrentDictionary<int, IReadOnlyDictionary<int, int>> SeasonCache = new();

    // Serializd 500s when hit with a concurrent burst (seen during the initial diary backfill:
    // 6-7 parallel /show/reviews/add in the same second all returned 500). Two guards keep the
    // backfill polite and self-healing:
    //   1. a process-wide gate caps how many Serializd requests are in flight at once, and
    //   2. SendAsync retries transient 5xx with exponential backoff (a slower pass succeeds).
    private const int MaxConcurrentRequests = 2;
    private const int MaxSendAttempts = 4; // initial try + 3 backoff retries
    private static readonly SemaphoreSlim RequestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);

    public SerializdApiClient(ILogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(SerializdApiConstants.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", SerializdApiConstants.FrontPageUrl);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", SerializdApiConstants.FrontPageUrl);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", SerializdApiConstants.AppId);
    }

    /// <summary>Test hook: drop cached tokens + season maps so tests don't leak state into each other.</summary>
    internal static void ResetCachesForTesting()
    {
        TokenCache.Clear();
        SeasonCache.Clear();
    }

    /// <summary>
    /// Logs in (or reuses a cached token) for the given account. Called by the
    /// factory before the client is handed to business code.
    /// </summary>
    internal async Task AuthenticateAsync(string email, string password)
    {
        _email = email;
        _password = password;

        if (TokenCache.TryGetValue(email, out var cached) && !string.IsNullOrEmpty(cached))
        {
            _token = cached;
            _logger.LogDebug("Reusing cached Serializd token for {Email}", email);
            return;
        }

        await LoginAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Definitive credential check for the config-page "Verify login" button: always hits
    /// <c>/login</c> (bypassing the token cache) and returns the Serializd username on success,
    /// or throws on failure. Does not persist anything.
    /// </summary>
    internal async Task<string?> VerifyLoginAsync(string email, string password)
    {
        _email = email;
        _password = password;
        await LoginAsync().ConfigureAwait(false);
        return Username;
    }

    private async Task LoginAsync()
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["email"] = _email,
            ["password"] = _password,
        });

        using var resp = await SendAsync(HttpMethod.Post, "/login", body, authenticated: false)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Serializd login failed ({(int)resp.StatusCode}): {err}");
        }

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        _token = doc.RootElement.GetProperty("token").GetString()
            ?? throw new Exception("Serializd login response had no token");
        Username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
        _username = Username ?? string.Empty;

        TokenCache[_email] = _token;
        _logger.LogInformation("Authenticated with Serializd as {Email}", _email);
    }

    public async Task<int?> ResolveSeasonIdAsync(int showTmdbId, int seasonNumber)
    {
        if (SeasonCache.TryGetValue(showTmdbId, out var map) && map.TryGetValue(seasonNumber, out var cachedId))
            return cachedId;

        // Cache miss (or unknown season number): fetch the show fresh once. This
        // also refreshes a stale cache when a new season has aired since last time.
        var fresh = await FetchSeasonMapAsync(showTmdbId).ConfigureAwait(false);
        SeasonCache[showTmdbId] = fresh;

        return fresh.TryGetValue(seasonNumber, out var id) ? id : null;
    }

    private async Task<IReadOnlyDictionary<int, int>> FetchSeasonMapAsync(int showTmdbId)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"/show/{showTmdbId}").ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Serializd get-show {showTmdbId} failed ({(int)resp.StatusCode}): {err}");
        }

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var map = new Dictionary<int, int>();
        if (doc.RootElement.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in seasons.EnumerateArray())
            {
                if (s.TryGetProperty("seasonNumber", out var numEl) && numEl.TryGetInt32(out var num) &&
                    s.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                {
                    map[num] = id;
                }
            }
        }

        return map;
    }

    public async Task LogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers)
        => await PostEpisodeLogAsync("/episode_log/add", showTmdbId, seasonId, episodeNumbers).ConfigureAwait(false);

    public async Task UnlogEpisodesAsync(int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers)
        => await PostEpisodeLogAsync("/episode_log/remove", showTmdbId, seasonId, episodeNumbers).ConfigureAwait(false);

    public async Task CreateEpisodeLogAsync(int showTmdbId, int seasonId, int episodeNumber,
        DateTime watchedAtUtc, int? rating, bool isRewatch)
    {
        // snake_case body (see /show/reviews/add). is_log=true makes it a dated Diary entry;
        // backdate is the watch date. rating is omitted when unrated so we don't post a 0.
        var payload = new Dictionary<string, object>
        {
            ["show_id"] = showTmdbId,
            ["season_id"] = seasonId,
            ["episode_number"] = episodeNumber,
            ["review_text"] = string.Empty,
            ["contains_spoiler"] = false,
            ["backdate"] = watchedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["is_log"] = true,
            ["is_rewatch"] = isRewatch,
            ["tags"] = Array.Empty<string>(),
            ["allows_comments"] = true,
            ["like"] = false,
        };
        // rating is required by /show/reviews/add (omitting it returns HTTP 500); 0 = unrated.
        payload["rating"] = rating is > 0 ? Math.Clamp(rating.Value, 1, 10) : 0;

        var body = JsonSerializer.Serialize(payload);
        using var resp = await SendAsync(HttpMethod.Post, "/show/reviews/add", body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Serializd /show/reviews/add failed ({(int)resp.StatusCode}): {err}");
        }
    }

    private async Task PostEpisodeLogAsync(string path, int showTmdbId, int seasonId, IReadOnlyList<int> episodeNumbers)
    {
        // snake_case is load-bearing: camelCase returns 500.
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["episode_numbers"] = episodeNumbers,
            ["season_id"] = seasonId,
            ["show_id"] = showTmdbId,
        });

        using var resp = await SendAsync(HttpMethod.Post, path, body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Serializd {path} failed ({(int)resp.StatusCode}): {err}");
        }
    }

    public async Task<List<SerializdWatchlistEntry>> GetWatchlistAsync()
    {
        await EnsureUsernameAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(_username))
            throw new Exception("Serializd username unknown; cannot read watchlist");

        var entries = new List<SerializdWatchlistEntry>();
        var seen = new HashSet<int>();
        for (var page = 1; page <= 100; page++)
        {
            using var resp = await SendAsync(HttpMethod.Get,
                $"/user/{Uri.EscapeDataString(_username)}/watchlistpage_v2/{page}?sort_by=date_added_desc")
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) break;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                break;

            foreach (var it in items.EnumerateArray())
            {
                // Watchlist items carry a TMDb `showId` plus `seasonIds` (Serializd's internal
                // season ids) for the specific seasons watchlisted.
                if (!it.TryGetProperty("showId", out var sid) || !sid.TryGetInt32(out var showTmdb) || !seen.Add(showTmdb))
                    continue;

                var serializdSeasonIds = new List<int>();
                if (it.TryGetProperty("seasonIds", out var seasonIdsEl) && seasonIdsEl.ValueKind == JsonValueKind.Array)
                    foreach (var s in seasonIdsEl.EnumerateArray())
                        if (s.TryGetInt32(out var sidVal)) serializdSeasonIds.Add(sidVal);

                var seasonNumbers = await ResolveSeasonNumbersAsync(showTmdb, serializdSeasonIds).ConfigureAwait(false);
                entries.Add(new SerializdWatchlistEntry(showTmdb, seasonNumbers));
            }

            var totalPages = doc.RootElement.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var t) ? t : page;
            if (page >= totalPages) break;
        }

        return entries;
    }

    /// <summary>Maps Serializd internal season ids to season numbers via the show's season map.</summary>
    private async Task<IReadOnlyList<int>> ResolveSeasonNumbersAsync(int showTmdbId, IReadOnlyList<int> serializdSeasonIds)
    {
        if (serializdSeasonIds.Count == 0) return Array.Empty<int>();

        var numberToId = await FetchSeasonMapAsync(showTmdbId).ConfigureAwait(false);
        var idToNumber = new Dictionary<int, int>();
        foreach (var kv in numberToId) idToNumber[kv.Value] = kv.Key;

        var numbers = new List<int>();
        foreach (var sid in serializdSeasonIds)
            if (idToNumber.TryGetValue(sid, out var num)) numbers.Add(num);
        numbers.Sort();
        return numbers;
    }

    public async Task<List<SerializdDiaryEpisode>> GetDiaryEpisodesAsync()
    {
        await EnsureUsernameAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(_username))
            throw new Exception("Serializd username unknown; cannot read diary");

        var result = new List<SerializdDiaryEpisode>();
        var seen = new HashSet<(int, int, int)>();
        for (var page = 1; page <= 200; page++)
        {
            using var resp = await SendAsync(HttpMethod.Get,
                $"/user/{Uri.EscapeDataString(_username)}/diary?page={page}").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) break;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("reviews", out var reviews)
                || reviews.ValueKind != JsonValueKind.Array || reviews.GetArrayLength() == 0)
                break;

            foreach (var r in reviews.EnumerateArray())
            {
                if (!r.TryGetProperty("showId", out var showEl) || !showEl.TryGetInt32(out var show)) continue;
                if (!r.TryGetProperty("episodeNumber", out var epEl) || !epEl.TryGetInt32(out var ep) || ep <= 0) continue;
                if (!r.TryGetProperty("seasonId", out var sidEl) || !sidEl.TryGetInt32(out var seasonId)) continue;

                // Resolve the Serializd seasonId to a season number via the entry's own season list.
                int? seasonNumber = null;
                if (r.TryGetProperty("showSeasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in seasons.EnumerateArray())
                    {
                        if (s.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var sid) && sid == seasonId
                            && s.TryGetProperty("seasonNumber", out var numEl) && numEl.TryGetInt32(out var num))
                        {
                            seasonNumber = num;
                            break;
                        }
                    }
                }

                if (seasonNumber == null) continue;
                if (seen.Add((show, seasonNumber.Value, ep)))
                    result.Add(new SerializdDiaryEpisode(show, seasonNumber.Value, ep));
            }

            var totalPages = doc.RootElement.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var t) ? t : page;
            if (page >= totalPages) break;
        }

        return result;
    }

    /// <summary>Resolves the authenticated user's own username (needed for user-scoped reads) if not already known.</summary>
    private async Task EnsureUsernameAsync()
    {
        if (!string.IsNullOrEmpty(_username)) return;

        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["token"] = _token });
        using var resp = await SendAsync(HttpMethod.Post, "/validateauthtoken", body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return;

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("username", out var u))
            _username = u.GetString() ?? string.Empty;
    }

    public async Task CreateShowReviewAsync(int showTmdbId, int? rating, string? reviewText, bool containsSpoiler)
    {
        var payload = new Dictionary<string, object?>
        {
            ["show_id"] = showTmdbId,
            ["season_id"] = null,
            ["episode_number"] = null,
            ["review_text"] = reviewText ?? string.Empty,
            ["contains_spoiler"] = containsSpoiler,
            ["backdate"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["is_log"] = false,
            ["is_rewatch"] = false,
            ["tags"] = Array.Empty<string>(),
            ["allows_comments"] = true,
            ["like"] = false,
            // rating is required by /show/reviews/add (omitting it 500s); 0 = unrated.
            ["rating"] = rating is > 0 ? Math.Clamp(rating.Value, 1, 10) : 0,
        };

        var body = JsonSerializer.Serialize(payload);
        using var resp = await SendAsync(HttpMethod.Post, "/show/reviews/add", body).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        _logger.LogInformation(
            "Serializd review POST show {Show} (is_log=false, rating={Rating}, textLen={Len}) → HTTP {Status}: {Body}",
            showTmdbId, payload["rating"], (reviewText ?? string.Empty).Length, (int)resp.StatusCode,
            respBody.Length > 400 ? respBody.Substring(0, 400) : respBody);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Serializd review ({showTmdbId}) failed ({(int)resp.StatusCode}): {respBody}");
    }

    public async Task SetShowMetaAsync(int showTmdbId, int? rating, bool like)
    {
        // Whole-show entry, is_log:false so it's a rating/like rather than a Diary row.
        // season_id/episode_number are sent as null (the API requires the keys present).
        var payload = new Dictionary<string, object?>
        {
            ["show_id"] = showTmdbId,
            ["season_id"] = null,
            ["episode_number"] = null,
            ["review_text"] = string.Empty,
            ["contains_spoiler"] = false,
            ["backdate"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["is_log"] = false,
            ["is_rewatch"] = false,
            ["tags"] = Array.Empty<string>(),
            ["allows_comments"] = true,
            ["like"] = like,
        };
        // rating is required by /show/reviews/add (omitting it returns HTTP 500); 0 = unrated.
        payload["rating"] = rating is > 0 ? Math.Clamp(rating.Value, 1, 10) : 0;

        var body = JsonSerializer.Serialize(payload);
        using var resp = await SendAsync(HttpMethod.Post, "/show/reviews/add", body).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new Exception($"Serializd show-meta ({showTmdbId}) failed ({(int)resp.StatusCode}): {err}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path,
        string? body = null, bool authenticated = true, bool isRetry = false, int attempt = 0)
    {
        using var request = new HttpRequestMessage(method, SerializdApiConstants.BaseUrl + path);
        if (authenticated && !string.IsNullOrEmpty(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        // Cap concurrency: the gate wraps only the raw send (never a delay or the recursive
        // retry), so a slow endpoint can't hold a slot and the 401/login recursion can't deadlock.
        HttpResponseMessage response;
        await RequestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        finally
        {
            RequestGate.Release();
        }

        if (response.StatusCode == (HttpStatusCode)429 && !isRetry)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10;
            _logger.LogWarning("Serializd rate limited, waiting {Seconds}s", retryAfter);
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(retryAfter)).ConfigureAwait(false);
            return await SendAsync(method, path, body, authenticated, isRetry: true).ConfigureAwait(false);
        }

        // Transient server errors (Serializd 500s under load, plus 502/503/504): back off and
        // retry a few times. A correct-but-overloaded request reliably lands on a later pass;
        // a genuinely-broken one just exhausts the retries and throws as before.
        var code = (int)response.StatusCode;
        if ((code == 500 || code == 502 || code == 503 || code == 504) && attempt + 1 < MaxSendAttempts)
        {
            response.Dispose();
            var delayMs = (int)(Math.Pow(2, attempt) * 500) + Random.Shared.Next(0, 400); // ~0.5s, 1s, 2s (+jitter)
            _logger.LogWarning("Serializd {Path} returned {Code}; backing off {Ms}ms then retry {Next}/{Max}",
                path, code, delayMs, attempt + 2, MaxSendAttempts);
            await Task.Delay(delayMs).ConfigureAwait(false);
            return await SendAsync(method, path, body, authenticated, isRetry, attempt + 1).ConfigureAwait(false);
        }

        // Token went stale: clear the cache, re-login with stored credentials, retry once.
        if (authenticated && response.StatusCode == HttpStatusCode.Unauthorized && !isRetry
            && !string.IsNullOrEmpty(_email))
        {
            _logger.LogWarning("Serializd token rejected (401), re-authenticating for {Email}", _email);
            response.Dispose();
            TokenCache.TryRemove(_email, out _);
            _token = string.Empty;
            await LoginAsync().ConfigureAwait(false);
            return await SendAsync(method, path, body, authenticated, isRetry: true).ConfigureAwait(false);
        }

        return response;
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
